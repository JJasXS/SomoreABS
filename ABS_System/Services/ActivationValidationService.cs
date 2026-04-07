using System.Data.Common;
using System.Globalization;
using System.Threading;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using YourApp.Data;
using YourApp.Models;

namespace YourApp.Services;

public sealed class ActivationValidationService : IActivationValidationService
{
    /// <summary>Firebird 3 max VARCHAR length; explicit size avoids "string right truncation" when binding long codes.</summary>
    private const int FirebirdVarcharMax = 32765;

    private readonly ActivationOptions _opt;
    private readonly ILogger<ActivationValidationService> _log;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _localDeploymentSchemaGate = new(1, 1);
    private int _localDeploymentTableEnsured;
    private ActivationValidationResult? _cached;
    private bool _validated;
    private ActivationTenantSnapshot? _activatedTenant;

    public ActivationValidationService(
        IOptions<ActivationOptions> options,
        ILogger<ActivationValidationService> log,
        IDbContextFactory<AppDbContext> dbFactory)
    {
        _opt = options.Value;
        _log = log;
        _dbFactory = dbFactory;
    }

    public bool IsActivationValid =>
        !_opt.Enabled || (_validated && _cached?.Success == true);

    public string? LastFailureMessage =>
        _validated && _cached?.Success == false ? _cached.Message : null;

    public ActivationTenantSnapshot? ActivatedTenant
    {
        get
        {
            lock (_sync)
                return _activatedTenant;
        }
    }

    public async Task<ActivationValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (!_opt.Enabled)
        {
            var ok = ActivationValidationResult.Ok("Activation gate disabled in configuration.");
            lock (_sync)
            {
                _cached = ok;
                _validated = true;
                _activatedTenant = null;
            }
            return ok;
        }

        var code = (_opt.ActivationCode ?? "").Trim();
        var fp = await ResolveMachineFingerprintAsync(cancellationToken).ConfigureAwait(false);
        var result = await ValidateCoreAsync(code, fp, cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            _cached = result;
            _validated = true;
        }

        if (!result.Success)
            _log.LogWarning("Activation validation failed: {Message}", result.Message);
        else
            _log.LogInformation("Activation validation succeeded.");

        return result;
    }

    public async Task<ActivationValidationResult> ValidateSubmittedCodeAsync(string? activationCode, CancellationToken cancellationToken = default)
    {
        if (!_opt.Enabled)
        {
            var ok = ActivationValidationResult.Ok("Activation gate disabled in configuration.");
            lock (_sync)
            {
                _cached = ok;
                _validated = true;
                _activatedTenant = null;
            }
            return ok;
        }

        var code = (activationCode ?? "").Trim();
        var fp = await ResolveMachineFingerprintAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(fp))
            return ActivationValidationResult.Fail("Enter your activation code.");

        var result = await ValidateCoreAsync(code, fp, cancellationToken).ConfigureAwait(false);

        lock (_sync)
        {
            _cached = result;
            _validated = true;
        }

        if (!result.Success)
            _log.LogWarning("Activation submit failed: {Message}", result.Message);
        else
            _log.LogInformation("Activation succeeded via submitted code.");

        return result;
    }

    public Task<string> GetMachineFingerprintForDisplayAsync(CancellationToken cancellationToken = default) =>
        ResolveMachineFingerprintAsync(cancellationToken);

    private async Task<string> ResolveMachineFingerprintAsync(CancellationToken cancellationToken)
    {
        var manual = (_opt.MachineFingerprint ?? "").Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(manual))
            return manual;

        if (_opt.PersistMachineFingerprintInSqlServer)
        {
            try
            {
                var persisted = await GetOrCreatePersistedFingerprintAsync(cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrEmpty(persisted))
                {
                    _log.LogInformation(
                        "Activation using machine fingerprint from SQL Server (LocalDeploymentInfo).");
                    return persisted;
                }
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Could not read or save machine fingerprint in SQL Server; falling back to auto-compute if enabled.");
            }
        }

        if (_opt.UseAutoMachineFingerprint)
        {
            var auto = MachineFingerprint.Compute().ToUpperInvariant();
            _log.LogInformation(
                "Activation using auto-computed machine fingerprint (Activation:MachineFingerprint is empty).");
            return auto;
        }

        return "";
    }

    private async Task<string> GetOrCreatePersistedFingerprintAsync(CancellationToken cancellationToken)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureLocalDeploymentTableAsync(db, cancellationToken).ConfigureAwait(false);

        var tracked = await db.LocalDeploymentInfos
            .FirstOrDefaultAsync(x => x.Id == LocalDeploymentInfo.SingletonRowId, cancellationToken)
            .ConfigureAwait(false);

        if (tracked != null && !string.IsNullOrWhiteSpace(tracked.MachineFingerprintHex))
            return tracked.MachineFingerprintHex.Trim().ToUpperInvariant();

        var computed = MachineFingerprint.Compute().ToUpperInvariant();

        if (tracked == null)
        {
            db.LocalDeploymentInfos.Add(new LocalDeploymentInfo
            {
                Id = LocalDeploymentInfo.SingletonRowId,
                MachineFingerprintHex = computed,
                UpdatedAtUtc = DateTime.UtcNow
            });
        }
        else
        {
            tracked.MachineFingerprintHex = computed;
            tracked.UpdatedAtUtc = DateTime.UtcNow;
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        _log.LogInformation("Saved machine fingerprint to SQL Server table LocalDeploymentInfo (Id=1).");
        return computed;
    }

    private async Task EnsureLocalDeploymentTableAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _localDeploymentTableEnsured) != 0)
            return;

        await _localDeploymentSchemaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _localDeploymentTableEnsured) != 0)
                return;

            await db.Database.ExecuteSqlRawAsync(
                """
                IF OBJECT_ID(N'dbo.LocalDeploymentInfo', N'U') IS NULL
                BEGIN
                    CREATE TABLE dbo.LocalDeploymentInfo (
                        Id INT NOT NULL CONSTRAINT PK_LocalDeploymentInfo PRIMARY KEY,
                        MachineFingerprintHex NVARCHAR(64) NOT NULL,
                        UpdatedAtUtc DATETIME2 NOT NULL
                    );
                END
                """,
                cancellationToken).ConfigureAwait(false);

            Volatile.Write(ref _localDeploymentTableEnsured, 1);
        }
        finally
        {
            _localDeploymentSchemaGate.Release();
        }
    }

    private async Task<ActivationValidationResult> ValidateCoreAsync(string code, string fingerprint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(fingerprint))
            return ActivationValidationResult.Fail(
                "Activation is not configured: set Activation:ActivationCode or Activation:MachineFingerprint, or enable Activation:UseAutoMachineFingerprint.");

        if (!string.IsNullOrEmpty(code) && string.IsNullOrEmpty(fingerprint))
            return ActivationValidationResult.Fail(
                "Machine fingerprint is required. Set Activation:MachineFingerprint, or enable Activation:UseAutoMachineFingerprint (default), to match LICENSE_ACTIVATION.MACHINE_FINGERPRINT.");

        lock (_sync)
            _activatedTenant = null;

        string cs;
        try
        {
            cs = BuildConnectionString();
        }
        catch (Exception ex)
        {
            return ActivationValidationResult.Fail($"Invalid activation database configuration: {ex.Message}");
        }

        await using var conn = new FbConnection(cs);
        try
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return ActivationValidationResult.Fail(
                $"Cannot connect to activation database: {ex.Message}");
        }

        const string tenantMayActivate =
            "AND (t.STATUS IS NULL OR UPPER(TRIM(t.STATUS)) = 'ACTIVE')";

        var sql = """
            SELECT
                la.LICENSE_ACTIVATION_ID,
                la.LICENSE_ID,
                TRIM(la.ACTIVATION_CODE),
                TRIM(la.MACHINE_FINGERPRINT),
                la.ACTIVATED_AT,
                la.EXPIRED_ON,
                TRIM(la.STATUS),
                TRIM(l.LICENSE_KEY),
                TRIM(l.STATUS),
                l.START_DATE,
                l.END_DATE,
                l.MAX_DEVICE_COUNT,
                TRIM(t.TENANT_CODE),
                TRIM(t.COMPANY_NAME),
                TRIM(p.PRODUCT_CODE),
                TRIM(p.PRODUCT_NAME),
                TRIM(dbp.DB_SERVER_IP),
                dbp.DB_PORT,
                TRIM(dbp.DB_NAME),
                TRIM(dbp.DB_FILE_REF),
                TRIM(dbp.DB_PATH_ENC),
                TRIM(dbp.DB_USERNAME),
                TRIM(dbp.DB_PASSWORD_ENC),
                CURRENT_TIMESTAMP,
                CURRENT_DATE
            FROM LICENSE_ACTIVATION la
            JOIN LICENSE l ON l.LICENSE_ID = la.LICENSE_ID
            JOIN TENANT t ON t.TENANT_ID = l.TENANT_ID
            JOIN PRODUCT p ON p.PRODUCT_ID = l.PRODUCT_ID
            LEFT JOIN TENANT_DB_PROFILE dbp
                ON dbp.TENANT_DB_PROFILE_ID = COALESCE(
                    l.TENANT_DB_PROFILE_ID,
                    (SELECT FIRST 1 tdp.TENANT_DB_PROFILE_ID
                     FROM TENANT_DB_PROFILE tdp
                     WHERE tdp.TENANT_ID = t.TENANT_ID
                       AND tdp.IS_DEFAULT = 1
                       AND tdp.STATUS = 'ACTIVE'
                     ORDER BY tdp.TENANT_DB_PROFILE_ID),
                    (SELECT FIRST 1 tdp.TENANT_DB_PROFILE_ID
                     FROM TENANT_DB_PROFILE tdp
                     WHERE tdp.TENANT_ID = t.TENANT_ID
                       AND tdp.STATUS = 'ACTIVE'
                     ORDER BY tdp.TENANT_DB_PROFILE_ID),
                    (SELECT FIRST 1 tdp.TENANT_DB_PROFILE_ID
                     FROM TENANT_DB_PROFILE tdp
                     WHERE tdp.TENANT_ID = t.TENANT_ID
                     ORDER BY tdp.TENANT_DB_PROFILE_ID)
                )
            """;

        if (!string.IsNullOrEmpty(code))
            sql += $"\nWHERE TRIM(la.ACTIVATION_CODE) = @code AND UPPER(TRIM(la.MACHINE_FINGERPRINT)) = @fp {tenantMayActivate}";
        else
            sql += $"\nWHERE UPPER(TRIM(la.MACHINE_FINGERPRINT)) = @fp {tenantMayActivate}";

        await using var cmd = new FbCommand(sql, conn);
        if (!string.IsNullOrEmpty(code))
            AddVarcharParameter(cmd, "@code", code);
        AddVarcharParameter(cmd, "@fp", fingerprint);

        try
        {
            int licenseId;
            int? maxDeviceCount;

            await using (var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
            {
                if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                    return ActivationValidationResult.Fail(
                        "Activation not found or expired. Please activate your system.");

                licenseId = reader.GetInt32(1);
                maxDeviceCount = reader.IsDBNull(11) ? null : Convert.ToInt32(reader.GetValue(11), CultureInfo.InvariantCulture);

                var activationStatus = reader.IsDBNull(6) ? "" : reader.GetString(6);
                var licenseStatus = reader.IsDBNull(8) ? "" : reader.GetString(8);
                var expiredOn = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
                var startDate = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9);
                var endDate = reader.IsDBNull(10) ? (DateTime?)null : reader.GetDateTime(10);

                var now = reader.IsDBNull(23) ? DateTime.Now : reader.GetDateTime(23);
                var today = reader.IsDBNull(24) ? DateTime.Today : reader.GetDateTime(24).Date;

                if (expiredOn.HasValue && now >= expiredOn.Value)
                    return ActivationValidationResult.Fail(
                        $"Activation expired on {expiredOn.Value:yyyy-MM-dd HH:mm:ss}.");

                if (startDate.HasValue && today < startDate.Value.Date)
                    return ActivationValidationResult.Fail(
                        $"License starts on {startDate.Value:yyyy-MM-dd}.");

                if (endDate.HasValue && today >= endDate.Value.Date)
                    return ActivationValidationResult.Fail(
                        $"License expired on {endDate.Value:yyyy-MM-dd}.");

                if (!string.Equals(activationStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                    return ActivationValidationResult.Fail(
                        DescribeStatusFailure("Activation", activationStatus, expiredOn));

                if (!string.Equals(licenseStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                    return ActivationValidationResult.Fail(
                        DescribeStatusFailure("License", licenseStatus, endDate));

                var dbName = reader.IsDBNull(18) ? null : reader.GetString(18).Trim();
                var dbFileRef = reader.IsDBNull(19) ? null : reader.GetString(19).Trim();
                var dbPath = reader.IsDBNull(20) ? null : reader.GetString(20).Trim();
                // Prefer the full resolved path when available; DB_FILE_REF / DB_NAME are metadata / fallback.
                var dbTarget = FirstNonEmpty(dbPath, dbFileRef, dbName);

                ClientDatabaseConnectionInfo? clientDb = null;
                if (!string.IsNullOrEmpty(dbTarget))
                {
                    clientDb = new ClientDatabaseConnectionInfo
                    {
                        DatabasePath = dbTarget,
                        DataSource = reader.IsDBNull(16) ? null : reader.GetString(16).Trim(),
                        Port = ReadNullableInt32(reader, 17),
                        User = reader.IsDBNull(21) ? null : reader.GetString(21).Trim(),
                        Password = reader.IsDBNull(22) ? null : reader.GetString(22)
                    };
                }

                if (clientDb == null || string.IsNullOrWhiteSpace(clientDb.DatabasePath))
                    return ActivationValidationResult.Fail(
                        "No client database target for this license. Set TENANT_DB_PROFILE.DB_FILE_REF (or DB_NAME / DB_PATH_ENC), plus host/port, in the activation database.");

                var snap = new ActivationTenantSnapshot
                {
                    TenantCode = reader.IsDBNull(12) ? "" : reader.GetString(12).Trim(),
                    CompanyName = reader.IsDBNull(13) ? "" : reader.GetString(13).Trim(),
                    ProductCode = reader.IsDBNull(14) ? null : reader.GetString(14).Trim(),
                    ProductName = reader.IsDBNull(15) ? null : reader.GetString(15).Trim(),
                    ClientDatabase = clientDb
                };
                lock (_sync)
                    _activatedTenant = snap;
            }

            if (maxDeviceCount.HasValue && maxDeviceCount.Value > 0)
            {
                await using var countCmd = new FbCommand(
                    """
                    SELECT COUNT(*)
                    FROM LICENSE_ACTIVATION
                    WHERE LICENSE_ID = @lid
                      AND UPPER(TRIM(STATUS)) = 'ACTIVE'
                    """,
                    conn);
                countCmd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });
                var activeCountObj = await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                var activeCount = Convert.ToInt64(activeCountObj, CultureInfo.InvariantCulture);
                if (activeCount > maxDeviceCount.Value)
                {
                    lock (_sync)
                        _activatedTenant = null;
                    return ActivationValidationResult.Fail(
                        string.Format(CultureInfo.InvariantCulture,
                            "This license allows at most {0} active device(s); the activation database reports {1}.",
                            maxDeviceCount.Value, activeCount));
                }
            }

            return ActivationValidationResult.Ok();
        }
        catch (FbException ex)
        {
            _log.LogError(ex, "Activation lookup failed when executing Firebird query.");
            return ActivationValidationResult.Fail(
                "Activation lookup failed. Please verify the activation database configuration and try again.");
        }
    }

    private string BuildConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(_opt.ConnectionString))
            return _opt.ConnectionString!;

        // Empty strings in appsettings.json override POCO defaults; Firebird then reports credentials undefined.
        var user = string.IsNullOrWhiteSpace(_opt.User) ? "SYSDBA" : _opt.User.Trim();
        var password = string.IsNullOrWhiteSpace(_opt.Password) ? "masterkey" : _opt.Password;

        var b = new FbConnectionStringBuilder
        {
            DataSource = _opt.Server,
            Port = _opt.Port,
            Database = _opt.Database,
            UserID = user,
            Password = password,
            Charset = _opt.Charset,
            Dialect = _opt.Dialect
        };
        return b.ConnectionString;
    }

    private static int? ReadNullableInt32(DbDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
            return null;
        return Convert.ToInt32(reader.GetValue(ordinal), CultureInfo.InvariantCulture);
    }

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }
        return null;
    }

    private static string DescribeStatusFailure(string label, string? status, DateTime? relevantDate)
    {
        status = (status ?? "").Trim();
        if (string.Equals(status, "EXPIRED", StringComparison.OrdinalIgnoreCase) && relevantDate.HasValue)
            return $"{label} expired on {relevantDate.Value:yyyy-MM-dd}.";
        if (string.Equals(status, "PENDING", StringComparison.OrdinalIgnoreCase) && relevantDate.HasValue)
            return $"{label} starts on {relevantDate.Value:yyyy-MM-dd}.";
        if (string.IsNullOrWhiteSpace(status))
            return $"{label} is not active.";
        return $"{label} status is {status}.";
    }

    private static void AddVarcharParameter(FbCommand cmd, string name, string value)
    {
        var p = new FbParameter(name, FbDbType.VarChar, FirebirdVarcharMax)
        {
            Value = value
        };
        cmd.Parameters.Add(p);
    }
}
