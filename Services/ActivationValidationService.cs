using System.Data.Common;
using System.Globalization;
using System.Threading;
using Microsoft.AspNetCore.Http;
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
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly object _sync = new();
    private readonly SemaphoreSlim _localDeploymentSchemaGate = new(1, 1);
    private int _localDeploymentTableEnsured;
    private ActivationValidationResult? _cached;
    private bool _validated;
    private ActivationTenantSnapshot? _activatedTenant;

    public ActivationValidationService(
        IOptions<ActivationOptions> options,
        ILogger<ActivationValidationService> log,
        IDbContextFactory<AppDbContext> dbFactory,
        IHttpContextAccessor httpContextAccessor)
    {
        _opt = options.Value;
        _log = log;
        _dbFactory = dbFactory;
        _httpContextAccessor = httpContextAccessor;
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
        var seatDid = await ResolveSeatDeviceIdForValidationAsync(null, fp, cancellationToken).ConfigureAwait(false);

        var result = await ValidateCoreAsync(code, fp, seatDid, cancellationToken).ConfigureAwait(false);

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

    public async Task<ActivationValidationResult> ValidateSubmittedCodeAsync(
        string? activationCode,
        string? deviceFingerprint,
        string? deviceId,
        CancellationToken cancellationToken = default)
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

        var code = _opt.LicenseId.HasValue
            ? (string.IsNullOrWhiteSpace(activationCode) ? (_opt.ActivationCode ?? "") : activationCode).Trim()
            : (activationCode ?? "").Trim();
        var fpOverride = string.IsNullOrWhiteSpace(deviceFingerprint)
            ? null
            : deviceFingerprint.Trim().ToUpperInvariant();
        var fp = fpOverride ?? await ResolveMachineFingerprintAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(fp))
            return ActivationValidationResult.Fail("Enter your activation code or device fingerprint.");

        var seatDeviceId = await ResolveSeatDeviceIdForValidationAsync(deviceId, fp, cancellationToken)
            .ConfigureAwait(false);
        var result = await ValidateCoreAsync(code, fp, seatDeviceId, cancellationToken).ConfigureAwait(false);

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

    /// <inheritdoc />
    public string GetSuggestedDeviceIdForManualLaRow(string? machineFingerprint)
    {
        if (_opt.LicenseId.HasValue)
        {
            if (_opt.SeatIdentityUsesMachineFingerprint && !_opt.SeatSwapFingerprintAndDeviceIdColumns)
                return DeriveSeatDeviceIdFromFingerprint(machineFingerprint);
            return "";
        }

        return NormalizeLegacyDeviceIdFallback((machineFingerprint ?? "").Trim().ToUpperInvariant());
    }

    /// <inheritdoc />
    public string DeriveSeatDeviceIdFromFingerprint(string? machineFingerprint)
    {
        var fp = (machineFingerprint ?? "").Trim().ToUpperInvariant();
        return string.IsNullOrEmpty(fp) ? "" : NormalizeLegacyDeviceIdFallback(fp);
    }

    /// <inheritdoc />
    public async Task<string> GetPreviewNextLogicalDeviceIdAsync(CancellationToken cancellationToken = default)
    {
        if (!_opt.LicenseId.HasValue)
            return "";

        if (_opt.SeatIdentityUsesMachineFingerprint && !_opt.SeatSwapFingerprintAndDeviceIdColumns)
            return "";

        string cs;
        try
        {
            cs = BuildConnectionString();
        }
        catch
        {
            return "";
        }

        await using var conn = new FbConnection(cs);
        try
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return "";
        }

        try
        {
            return await FormatNextLogicalDeviceIdAsync(
                conn,
                tx: null,
                _opt.LicenseId.Value,
                _opt.SeatSwapFingerprintAndDeviceIdColumns,
                cancellationToken).ConfigureAwait(false);
        }
        catch (FbException ex)
        {
            _log.LogDebug(ex, "Could not preview next logical DEVICE_ID.");
            return "";
        }
    }

    /// <inheritdoc />
    public async Task<string> GetSeatDeviceIdForDisplayAsync(CancellationToken cancellationToken = default)
    {
        if (!_opt.LicenseId.HasValue)
            return "";

        if (_opt.SeatIdentityUsesMachineFingerprint)
            return DeriveSeatDeviceIdFromFingerprint(await ResolveMachineFingerprintAsync(cancellationToken).ConfigureAwait(false));

        var fromCookie = await ResolveSeatDeviceIdFromCookieAsync(cancellationToken).ConfigureAwait(false);
        return fromCookie ?? "";
    }

    /// <inheritdoc />
    public async Task<string?> GetRegisteredDeviceIdForMachineFingerprintAsync(
        string? machineFingerprintOverride,
        string? deviceIdOverride,
        CancellationToken cancellationToken = default)
    {
        var fp = string.IsNullOrWhiteSpace(machineFingerprintOverride)
            ? await ResolveMachineFingerprintAsync(cancellationToken).ConfigureAwait(false)
            : machineFingerprintOverride.Trim().ToUpperInvariant();
        fp = (fp ?? "").Trim().ToUpperInvariant();

        string? did = null;
        if (_opt.LicenseId.HasValue)
        {
            if (_opt.SeatIdentityUsesMachineFingerprint)
            {
                did = DeriveSeatDeviceIdFromFingerprint(fp);
            }
            else
            {
                did = string.IsNullOrWhiteSpace(deviceIdOverride)
                    ? await ResolveSeatDeviceIdFromCookieAsync(cancellationToken).ConfigureAwait(false)
                    : NormalizeSeatDeviceIdForLookup(deviceIdOverride);
            }

            if (string.IsNullOrEmpty(did))
                return null;
        }
        else if (string.IsNullOrEmpty(fp))
        {
            return null;
        }

        string cs;
        try
        {
            cs = BuildConnectionString();
        }
        catch
        {
            return null;
        }

        await using var conn = new FbConnection(cs);
        try
        {
            await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }

        var sql = _opt.LicenseId.HasValue
            ? _opt.SeatSwapFingerprintAndDeviceIdColumns
                ? """
                  SELECT FIRST 1 TRIM(la.MACHINE_FINGERPRINT)
                  FROM LICENSE_ACTIVATION la
                  WHERE la.LICENSE_ID = @lid
                    AND UPPER(TRIM(la.MACHINE_FINGERPRINT)) = @did
                    AND UPPER(TRIM(la.DEVICE_ID)) = @fp
                  """
                : """
                  SELECT FIRST 1 TRIM(la.DEVICE_ID)
                  FROM LICENSE_ACTIVATION la
                  WHERE la.LICENSE_ID = @lid
                    AND UPPER(TRIM(la.DEVICE_ID)) = @did
                  """
            : """
              SELECT FIRST 1 TRIM(la.DEVICE_ID)
              FROM LICENSE_ACTIVATION la
              WHERE UPPER(TRIM(la.MACHINE_FINGERPRINT)) = @fp
              ORDER BY la.LICENSE_ACTIVATION_ID
              """;

        try
        {
            await using var cmd = new FbCommand(sql, conn);
            if (_opt.LicenseId.HasValue)
            {
                cmd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = _opt.LicenseId.Value });
                AddVarcharParameter(cmd, "@did", did!);
                if (_opt.SeatSwapFingerprintAndDeviceIdColumns)
                    AddVarcharParameter(cmd, "@fp", FingerprintForLaDeviceIdColumn(fp));
            }
            else
                AddVarcharParameter(cmd, "@fp", fp);

            var o = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (o == null || o == DBNull.Value)
                return null;
            var s = Convert.ToString(o, CultureInfo.InvariantCulture)?.Trim();
            return string.IsNullOrEmpty(s) ? null : s;
        }
        catch (FbException ex)
        {
            _log.LogDebug(ex, "Could not read DEVICE_ID from LICENSE_ACTIVATION.");
            return null;
        }
    }

    private static string? NormalizeSeatDeviceIdForLookup(string? s) =>
        NormalizeUserProvidedSeatDeviceId(s);

    /// <summary>Form value wins; otherwise cookie (seat mode).</summary>
    private async Task<string?> ResolveSeatDeviceIdForValidationAsync(
        string? formDeviceId,
        string resolvedFingerprint,
        CancellationToken cancellationToken)
    {
        if (_opt.LicenseId.HasValue && _opt.SeatIdentityUsesMachineFingerprint && !_opt.SeatSwapFingerprintAndDeviceIdColumns)
        {
            var fp = (resolvedFingerprint ?? "").Trim().ToUpperInvariant();
            if (string.IsNullOrEmpty(fp))
                return null;
            return NormalizeLegacyDeviceIdFallback(fp);
        }

        var fromForm = NormalizeSeatDeviceIdForLookup(formDeviceId);
        if (!string.IsNullOrEmpty(fromForm))
            return fromForm;

        return await ResolveSeatDeviceIdFromCookieAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<string?> ResolveSeatDeviceIdFromCookieAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var http = _httpContextAccessor.HttpContext;
        if (http is null)
            return Task.FromResult<string?>(null);

        var cookieName = (_opt.DeviceIdCookieName ?? "ABS_DeviceLogicalId").Trim();
        if (string.IsNullOrEmpty(cookieName))
            return Task.FromResult<string?>(null);

        var cookieVal = http.Request.Cookies[cookieName];
        return Task.FromResult(NormalizeSeatDeviceIdForLookup(cookieVal));
    }

    private async Task<string> ResolveMachineFingerprintAsync(CancellationToken cancellationToken)
    {
        // Prefer per-client fingerprint cookie when present (seat mode; value may still duplicate across users on one server).
        var http = _httpContextAccessor.HttpContext;
        if (http is not null)
        {
            var cookieName = (_opt.DeviceFingerprintCookieName ?? "ABS_DeviceFingerprint").Trim();
            var cookieVal = string.IsNullOrWhiteSpace(cookieName) ? null : http.Request.Cookies[cookieName];
            if (!string.IsNullOrWhiteSpace(cookieVal))
                return cookieVal.Trim().ToUpperInvariant();
        }

        var manual = (_opt.MachineFingerprint ?? "").Trim().ToUpperInvariant();
        if (!string.IsNullOrEmpty(manual))
            return manual;

        // Seat enforcement mode: require a client-provided fingerprint (cookie / form submission).
        // In this mode we intentionally do NOT auto-compute a server fingerprint, otherwise all devices
        // would share the same "machine" and would not be counted independently.
        if (_opt.LicenseId.HasValue)
            return "";

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

    private async Task<ActivationValidationResult> ValidateCoreAsync(
        string code,
        string fingerprint,
        string? seatDeviceId,
        CancellationToken cancellationToken)
    {
        if (_opt.LicenseId.HasValue)
            return await ValidateCoreByLicenseSeatModelAsync(
                code,
                fingerprint,
                seatDeviceId,
                cancellationToken).ConfigureAwait(false);

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
            SELECT FIRST 1
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

        var fpNorm = fingerprint.Trim().ToUpperInvariant();
        var didNorm = string.IsNullOrWhiteSpace(seatDeviceId) ? null : seatDeviceId.Trim().ToUpperInvariant();

        /* Legacy: hash vs UUID may sit in MACHINE_FINGERPRINT or DEVICE_ID (swapped). Both submitted values must appear on the row (each matches one column); order/columns can differ. */
        string identityMatch;
        if (string.IsNullOrEmpty(didNorm))
        {
            identityMatch = """
                  (
                    UPPER(TRIM(la.MACHINE_FINGERPRINT)) = @fp
                    OR UPPER(TRIM(la.DEVICE_ID)) = @fp
                  )
                """;
        }
        else
        {
            identityMatch = """
                  (
                    (
                      UPPER(TRIM(la.MACHINE_FINGERPRINT)) = @fp
                      OR UPPER(TRIM(la.DEVICE_ID)) = @fp
                    )
                    AND
                    (
                      UPPER(TRIM(la.MACHINE_FINGERPRINT)) = @did
                      OR UPPER(TRIM(la.DEVICE_ID)) = @did
                    )
                  )
                """;
        }

        if (!string.IsNullOrEmpty(code))
            sql += $"\nWHERE TRIM(la.ACTIVATION_CODE) = @code AND {identityMatch} {tenantMayActivate}";
        else
            sql += $"\nWHERE {identityMatch} {tenantMayActivate}";

        await using var cmd = new FbCommand(sql, conn);
        if (!string.IsNullOrEmpty(code))
            AddVarcharParameter(cmd, "@code", code);
        AddVarcharParameter(cmd, "@fp", fpNorm);
        if (!string.IsNullOrEmpty(didNorm))
            AddVarcharParameter(cmd, "@did", didNorm!);

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

    private static async Task<string?> LoadTrimmedLicenseKeyFromLicenseAsync(
        FbConnection conn,
        int licenseId,
        CancellationToken cancellationToken)
    {
        await using var cmd = new FbCommand(
            """
            SELECT TRIM(l.LICENSE_KEY)
            FROM LICENSE l
            WHERE l.LICENSE_ID = @lid
            """,
            conn);
        cmd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });
        var o = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (o == null || o == DBNull.Value)
            return null;
        var s = Convert.ToString(o, CultureInfo.InvariantCulture)?.Trim();
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>
    /// Seat mode gate: true if some <c>LICENSE_ACTIVATION</c> row for <paramref name="licenseId"/> has
    /// <c>MACHINE_FINGERPRINT</c> equal to <paramref name="normalizedDeviceId"/> (trim, upper).
    /// </summary>
    private static async Task<bool> DeviceIdExistsAsMachineFingerprintForLicenseAsync(
        FbConnection conn,
        int licenseId,
        string normalizedDeviceId,
        CancellationToken cancellationToken)
    {
        await using var cmd = new FbCommand(
            """
            SELECT COUNT(*)
            FROM LICENSE_ACTIVATION la
            WHERE la.LICENSE_ID = @lid
              AND UPPER(TRIM(la.MACHINE_FINGERPRINT)) = @did
            """,
            conn);
        cmd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });
        AddVarcharParameter(cmd, "@did", normalizedDeviceId);
        var obj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(obj, CultureInfo.InvariantCulture) > 0;
    }

    private async Task<ActivationValidationResult> ValidateCoreByLicenseSeatModelAsync(
        string? activationCode,
        string fingerprint,
        string? seatDeviceId,
        CancellationToken cancellationToken)
    {
        var licenseId = _opt.LicenseId!.Value;

        fingerprint = (fingerprint ?? "").Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(fingerprint))
            return ActivationValidationResult.Fail(
                "Machine fingerprint is required for seat enforcement. Provide the client value for LICENSE_ACTIVATION.MACHINE_FINGERPRINT (it may match other users if everyone shares the same host fingerprint).");

        var did = NormalizeSeatDeviceIdForLookup(seatDeviceId);
        if (string.IsNullOrEmpty(did))
            return ActivationValidationResult.Fail(
                _opt.SeatIdentityUsesMachineFingerprint
                    ? "Machine fingerprint is required for seat licensing when Activation:SeatIdentityUsesMachineFingerprint is true. Submit the Blocked page with the fingerprint field filled."
                    : "Device ID is required for seat licensing. It must be the unique local desktop or workstation identity for this machine (generated or read on the client — not optional). Send it from the Blocked page, your desktop app, or match LICENSE_ACTIVATION.DEVICE_ID in the activation database.");

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
            return ActivationValidationResult.Fail($"Cannot connect to activation database: {ex.Message}");
        }

        if (_opt.RequireSeatLicenseKey)
        {
            var dbKey = await LoadTrimmedLicenseKeyFromLicenseAsync(conn, licenseId, cancellationToken).ConfigureAwait(false);
            if (string.IsNullOrEmpty(dbKey))
            {
                return ActivationValidationResult.Fail(
                    "License key validation is required but LICENSE.LICENSE_KEY is empty for this license in the activation database.");
            }

            var submittedKey = (string.IsNullOrWhiteSpace(activationCode) ? _opt.ActivationCode : activationCode)?.Trim() ?? "";
            if (string.IsNullOrEmpty(submittedKey))
            {
                return ActivationValidationResult.Fail(
                    "Enter the license key (activation code) that matches LICENSE.LICENSE_KEY for this license, or set Activation:ActivationCode in configuration.");
            }

            if (!string.Equals(submittedKey, dbKey, StringComparison.OrdinalIgnoreCase))
            {
                return ActivationValidationResult.Fail(
                    "The license key does not match LICENSE.LICENSE_KEY for this license in the activation database.");
            }
        }

        // Submitted Device ID must already appear as MACHINE_FINGERPRINT on some row for this license (unless auto-register will create a new row).
        if (_opt.RequireDeviceIdRegisteredAsMachineFingerprint && !_opt.AutoRegisterNewDevices)
        {
            var registeredAsFp = await DeviceIdExistsAsMachineFingerprintForLicenseAsync(
                conn,
                licenseId,
                did,
                cancellationToken).ConfigureAwait(false);
            if (!registeredAsFp)
            {
                return ActivationValidationResult.Fail(
                    "This Device ID is not registered: no LICENSE_ACTIVATION row for this license has MACHINE_FINGERPRINT matching the submitted Device ID. Contact your administrator or ensure the device is provisioned before activating.");
            }
        }

        const string tenantMayActivate =
            "AND (t.STATUS IS NULL OR UPPER(TRIM(t.STATUS)) = 'ACTIVE')";

        var lookupSql = """
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
            """ + (_opt.SeatSwapFingerprintAndDeviceIdColumns
                ? """
            WHERE la.LICENSE_ID = @lid
              AND UPPER(TRIM(la.MACHINE_FINGERPRINT)) = @did
              AND UPPER(TRIM(la.DEVICE_ID)) = @fp
              AND UPPER(TRIM(la.STATUS)) = 'ACTIVE'
            """
                : """
            WHERE la.LICENSE_ID = @lid
              AND UPPER(TRIM(la.DEVICE_ID)) = @did
              AND UPPER(TRIM(la.STATUS)) = 'ACTIVE'
            """);

        try
        {
            // 1) Active seat: default maps form Device ID → la.DEVICE_ID and fingerprint → la.MACHINE_FINGERPRINT; swapped mode reverses those columns (see Activation:SeatSwapFingerprintAndDeviceIdColumns).
            var validated = await TryValidateSeatLookupAsync(
                conn,
                lookupSql + $" {tenantMayActivate}",
                licenseId,
                did,
                fingerprint,
                _opt.SeatSwapFingerprintAndDeviceIdColumns,
                cancellationToken).ConfigureAwait(false);

            if (validated.Success)
                return validated;

            // Only auto-register when there is no active row for this license + local device identity.
            if (!string.Equals(validated.Message, "Activation not found or expired.", StringComparison.OrdinalIgnoreCase))
                return validated;

            // 2) Re-bind: same browser (HttpOnly cookies match form) + activation code matches an active row → update FP/DEVICE_ID then validate.
            var codeBind = await TrySeatBindByActivationCodeAndMatchingCookiesAsync(
                conn,
                licenseId,
                activationCode,
                fingerprint,
                did,
                lookupSql,
                tenantMayActivate,
                cancellationToken).ConfigureAwait(false);
            if (codeBind is not null)
                return codeBind;

            // 3) If not found, try to register (idempotently) when enabled
            if (!_opt.AutoRegisterNewDevices)
                return ActivationValidationResult.Fail(validated.Message);

            var registerResult = await TryRegisterSeatDeviceAsync(
                conn,
                licenseId,
                fingerprint,
                did,
                cancellationToken).ConfigureAwait(false);

            if (!registerResult.Success)
                return registerResult;

            // 4) Lookup again after registration to populate tenant snapshot + enforce license rules
            return await TryValidateSeatLookupAsync(
                conn,
                lookupSql + $" {tenantMayActivate}",
                licenseId,
                did,
                fingerprint,
                _opt.SeatSwapFingerprintAndDeviceIdColumns,
                cancellationToken).ConfigureAwait(false);
        }
        catch (FbException ex)
        {
            _log.LogError(ex, "Seat enforcement lookup failed when executing Firebird query.");
            return ActivationValidationResult.Fail(
                "Activation lookup failed. Please verify the activation database configuration and try again.");
        }
    }

    /// <summary>
    /// HttpOnly cookies from a prior successful activation on this browser must match the submitted fingerprint and device id.
    /// </summary>
    private bool SeatBrowserCookiesMatchSubmitted(string fingerprint, string deviceId)
    {
        var http = _httpContextAccessor.HttpContext;
        if (http is null)
            return false;

        var fpName = (_opt.DeviceFingerprintCookieName ?? "ABS_DeviceFingerprint").Trim();
        var didName = (_opt.DeviceIdCookieName ?? "ABS_DeviceLogicalId").Trim();
        if (string.IsNullOrEmpty(fpName) || string.IsNullOrEmpty(didName))
            return false;

        var cFp = http.Request.Cookies[fpName];
        var cDid = http.Request.Cookies[didName];
        if (string.IsNullOrWhiteSpace(cFp) || string.IsNullOrWhiteSpace(cDid))
            return false;

        var fpN = fingerprint.Trim().ToUpperInvariant();
        var didN = deviceId.Trim().ToUpperInvariant();
        return string.Equals(cFp.Trim().ToUpperInvariant(), fpN, StringComparison.Ordinal)
               && string.Equals(cDid.Trim().ToUpperInvariant(), didN, StringComparison.Ordinal);
    }

    /// <summary>
    /// When primary DEVICE_ID lookup fails: find active row by license + activation code, align FP/DEVICE_ID to this browser, re-validate.
    /// Returns null when this path does not apply (feature off, missing code, or cookies do not match form).
    /// </summary>
    private async Task<ActivationValidationResult?> TrySeatBindByActivationCodeAndMatchingCookiesAsync(
        FbConnection conn,
        int licenseId,
        string? activationCode,
        string fingerprint,
        string deviceId,
        string lookupSql,
        string tenantMayActivate,
        CancellationToken cancellationToken)
    {
        if (!_opt.SeatTrustBrowserCookiesWithActivationCode)
            return null;
        if (string.IsNullOrWhiteSpace(activationCode))
            return null;
        if (!SeatBrowserCookiesMatchSubmitted(fingerprint, deviceId))
            return null;

        var code = activationCode.Trim();

        int? laId = null;
        await using (var findCmd = new FbCommand(
            """
            SELECT FIRST 1 la.LICENSE_ACTIVATION_ID
            FROM LICENSE_ACTIVATION la
            WHERE la.LICENSE_ID = @lid
              AND TRIM(la.ACTIVATION_CODE) = @code
              AND UPPER(TRIM(la.STATUS)) = 'ACTIVE'
            """,
            conn))
        {
            findCmd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });
            AddVarcharParameter(findCmd, "@code", code);
            var o = await findCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (o != null && o != DBNull.Value)
                laId = Convert.ToInt32(o, CultureInfo.InvariantCulture);
        }

        if (!laId.HasValue)
        {
            return ActivationValidationResult.Fail(
                "Activation code does not match an active seat for this license. Clear cookies and use the registered Device ID, or check the code in the activation database.");
        }

        var fpStore = FingerprintForLaDeviceIdColumn(fingerprint);
        var swap = _opt.SeatSwapFingerprintAndDeviceIdColumns;
        await using (var upd = new FbCommand(
            swap
                ? """
                  UPDATE LICENSE_ACTIVATION
                  SET MACHINE_FINGERPRINT = @did,
                      DEVICE_ID = @fp
                  WHERE LICENSE_ACTIVATION_ID = @laId
                    AND LICENSE_ID = @lid
                  """
                : """
                  UPDATE LICENSE_ACTIVATION
                  SET MACHINE_FINGERPRINT = @fp,
                      DEVICE_ID = @did
                  WHERE LICENSE_ACTIVATION_ID = @laId
                    AND LICENSE_ID = @lid
                  """,
            conn))
        {
            if (swap)
            {
                AddVarcharParameter(upd, "@did", deviceId);
                AddVarcharParameter(upd, "@fp", fpStore);
            }
            else
            {
                AddVarcharParameter(upd, "@fp", fingerprint);
                AddVarcharParameter(upd, "@did", deviceId);
            }

            upd.Parameters.Add(new FbParameter("@laId", FbDbType.Integer) { Value = laId.Value });
            upd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });
            var n = await upd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            if (n == 0)
                return ActivationValidationResult.Fail("Could not update activation row for this browser.");
        }

        _log.LogInformation(
            "Seat mode: matched activation code + HttpOnly cookies; updated MACHINE_FINGERPRINT/DEVICE_ID for LICENSE_ACTIVATION_ID {LaId}.",
            laId.Value);

        return await TryValidateSeatLookupAsync(
            conn,
            lookupSql + $" {tenantMayActivate}",
            licenseId,
            deviceId,
            fingerprint,
            swap,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<ActivationValidationResult> TryValidateSeatLookupAsync(
        FbConnection conn,
        string sql,
        int licenseId,
        string deviceId,
        string fingerprint,
        bool seatSwapFingerprintAndDeviceIdColumns,
        CancellationToken cancellationToken)
    {
        await using var cmd = new FbCommand(sql, conn);
        cmd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });
        AddVarcharParameter(cmd, "@did", deviceId);
        if (seatSwapFingerprintAndDeviceIdColumns)
            AddVarcharParameter(cmd, "@fp", FingerprintForLaDeviceIdColumn(fingerprint));

        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            return ActivationValidationResult.Fail("Activation not found or expired.");

        var activationStatus = reader.IsDBNull(6) ? "" : reader.GetString(6);
        if (!string.Equals(activationStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return ActivationValidationResult.Fail(
                DescribeStatusFailure("Activation", activationStatus, reader.IsDBNull(5) ? null : reader.GetDateTime(5)));

        var licenseStatus = reader.IsDBNull(8) ? "" : reader.GetString(8);
        if (!string.Equals(licenseStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
            return ActivationValidationResult.Fail(
                DescribeStatusFailure("License", licenseStatus, reader.IsDBNull(10) ? null : reader.GetDateTime(10)));

        var expiredOn = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
        var startDate = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9);
        var endDate = reader.IsDBNull(10) ? (DateTime?)null : reader.GetDateTime(10);

        var now = reader.IsDBNull(23) ? DateTime.Now : reader.GetDateTime(23);
        var today = reader.IsDBNull(24) ? DateTime.Today : reader.GetDateTime(24).Date;

        if (expiredOn.HasValue && now >= expiredOn.Value)
        {
            lock (_sync)
                _activatedTenant = null;
            return ActivationValidationResult.Fail($"Activation expired on {expiredOn.Value:yyyy-MM-dd HH:mm:ss}.");
        }

        if (startDate.HasValue && today < startDate.Value.Date)
            return ActivationValidationResult.Fail($"License starts on {startDate.Value:yyyy-MM-dd}.");

        if (endDate.HasValue && today >= endDate.Value.Date)
            return ActivationValidationResult.Fail($"License expired on {endDate.Value:yyyy-MM-dd}.");

        int? maxDeviceCountSeat = reader.IsDBNull(11)
            ? null
            : Convert.ToInt32(reader.GetValue(11), CultureInfo.InvariantCulture);

        var dbName = reader.IsDBNull(18) ? null : reader.GetString(18).Trim();
        var dbFileRef = reader.IsDBNull(19) ? null : reader.GetString(19).Trim();
        var dbPath = reader.IsDBNull(20) ? null : reader.GetString(20).Trim();
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

        var seatCap = maxDeviceCountSeat is > 0 ? maxDeviceCountSeat : null;
        if (seatCap.HasValue)
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

            if (activeCount > seatCap.Value)
            {
                lock (_sync)
                    _activatedTenant = null;
                return ActivationValidationResult.Fail(
                    string.Format(CultureInfo.InvariantCulture,
                        "This license allows at most {0} active device seat(s) (LICENSE.MAX_DEVICE_COUNT); the activation database reports {1}.",
                        seatCap.Value, activeCount));
            }
        }

        return ActivationValidationResult.Ok();
    }

    /// <summary>
    /// Seat cap policy: deactivate the oldest active row so a new device can register when <see cref="ActivationOptions.SeatEvictOldestWhenAtDeviceCap"/> is true.
    /// </summary>
    private static async Task<bool> TryDeactivateOldestActiveSeatAsync(
        FbConnection conn,
        int licenseId,
        FbTransaction tx,
        CancellationToken cancellationToken)
    {
        int? laId = null;
        await using (var sel = new FbCommand(
            """
            SELECT FIRST 1 la.LICENSE_ACTIVATION_ID
            FROM LICENSE_ACTIVATION la
            WHERE la.LICENSE_ID = @lid
              AND UPPER(TRIM(la.STATUS)) = 'ACTIVE'
            ORDER BY la.ACTIVATED_AT ASC
            """,
            conn,
            tx))
        {
            sel.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });
            var o = await sel.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            if (o != null && o != DBNull.Value)
                laId = Convert.ToInt32(o, CultureInfo.InvariantCulture);
        }

        if (!laId.HasValue)
            return false;

        await using var upd = new FbCommand(
            """
            UPDATE LICENSE_ACTIVATION
            SET STATUS = 'INACTIVE'
            WHERE LICENSE_ACTIVATION_ID = @laId
              AND LICENSE_ID = @lid
            """,
            conn,
            tx);
        upd.Parameters.Add(new FbParameter("@laId", FbDbType.Integer) { Value = laId.Value });
        upd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });
        var n = await upd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return n > 0;
    }

    private async Task<ActivationValidationResult> TryRegisterSeatDeviceAsync(
        FbConnection conn,
        int licenseId,
        string fingerprint,
        string did,
        CancellationToken cancellationToken)
    {
        using var tx = conn.BeginTransaction();

        // 1) Lock LICENSE first (FOR UPDATE) so concurrent seat registrations serialize: the second caller
        // will see the first seat's ACTIVATION_CODE instead of both random-generating different codes.
        string? licenseType;
        DateTime? licenseStartDate;
        DateTime? licenseEndDate;
        int? maxDeviceCount;

        await using (var policyCmd = new FbCommand(
            """
            SELECT
                TRIM(l.LICENSE_TYPE),
                l.START_DATE,
                l.END_DATE,
                l.MAX_DEVICE_COUNT
            FROM LICENSE l
            WHERE l.LICENSE_ID = @lid
            FOR UPDATE
            """,
            conn,
            tx))
        {
            policyCmd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });
            await using var reader = await policyCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                tx.Rollback();
                return ActivationValidationResult.Fail("License not found for seat enforcement.");
            }

            licenseType = reader.IsDBNull(0) ? "" : reader.GetString(0).Trim();
            licenseStartDate = reader.IsDBNull(1) ? null : reader.GetDateTime(1);
            licenseEndDate = reader.IsDBNull(2) ? null : reader.GetDateTime(2);
            maxDeviceCount = reader.IsDBNull(3) ? null : Convert.ToInt32(reader.GetValue(3), CultureInfo.InvariantCulture);
        }

        // 2) Count current active seats
        await using var countCmd = new FbCommand(
            """
            SELECT COUNT(*)
            FROM LICENSE_ACTIVATION
            WHERE LICENSE_ID = @lid
              AND UPPER(TRIM(STATUS)) = 'ACTIVE'
            """,
            conn,
            tx);
        countCmd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });

        var activeCountObj = await countCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var activeCount = Convert.ToInt64(activeCountObj, CultureInfo.InvariantCulture);

        var registerCap = maxDeviceCount is > 0 ? maxDeviceCount : null;
        if (registerCap.HasValue && activeCount >= registerCap.Value)
        {
            if (_opt.SeatEvictOldestWhenAtDeviceCap)
            {
                var safety = 0;
                while (activeCount >= registerCap.Value && safety++ < 64)
                {
                    var evicted = await TryDeactivateOldestActiveSeatAsync(
                        conn,
                        licenseId,
                        tx,
                        cancellationToken).ConfigureAwait(false);
                    if (!evicted)
                        break;
                    activeCount--;
                }
            }

            if (registerCap.HasValue && activeCount >= registerCap.Value)
            {
                tx.Rollback();
                return ActivationValidationResult.Fail(
                    string.Format(CultureInfo.InvariantCulture,
                        "This license allows at most {0} active device seat(s) (LICENSE.MAX_DEVICE_COUNT); the activation database already has {1}. Add seats or deactivate a device before activating another.",
                        registerCap.Value, activeCount));
            }
        }

        // 3) Compute EXPIRED_ON using the same rules as the LAAS licensing service.
        var activatedAtUtc = DateTime.UtcNow;
        var expiredOn = ComputeExpiredOn(activatedAtUtc, licenseType ?? "", licenseStartDate, licenseEndDate);

        // 4) One seat per license + identity: default keys DEVICE_ID; swapped mode keys MACHINE_FINGERPRINT (Device ID) + DEVICE_ID (fingerprint prefix).
        var fpForDeviceIdCol = FingerprintForLaDeviceIdColumn(fingerprint);
        var swapCols = _opt.SeatSwapFingerprintAndDeviceIdColumns;

        int? existingLaId = null;
        if (swapCols)
        {
            await using (var compositeCmd = new FbCommand(
                """
                SELECT FIRST 1 la.LICENSE_ACTIVATION_ID
                FROM LICENSE_ACTIVATION la
                WHERE la.LICENSE_ID = @lid
                  AND UPPER(TRIM(la.MACHINE_FINGERPRINT)) = @did
                  AND UPPER(TRIM(la.DEVICE_ID)) = @fp
                """,
                conn,
                tx))
            {
                compositeCmd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });
                AddVarcharParameter(compositeCmd, "@did", did);
                AddVarcharParameter(compositeCmd, "@fp", fpForDeviceIdCol);

                var existingObj = await compositeCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (existingObj != null && existingObj != DBNull.Value)
                    existingLaId = Convert.ToInt32(existingObj, CultureInfo.InvariantCulture);
            }
        }
        else
        {
            await using (var compositeCmd = new FbCommand(
                """
                SELECT FIRST 1 la.LICENSE_ACTIVATION_ID
                FROM LICENSE_ACTIVATION la
                WHERE la.LICENSE_ID = @lid
                  AND UPPER(TRIM(la.DEVICE_ID)) = @did
                """,
                conn,
                tx))
            {
                compositeCmd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });
                AddVarcharParameter(compositeCmd, "@did", did);

                var existingObj = await compositeCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
                if (existingObj != null && existingObj != DBNull.Value)
                    existingLaId = Convert.ToInt32(existingObj, CultureInfo.InvariantCulture);
            }
        }

        var deviceIdColValue = swapCols ? fpForDeviceIdCol : did;
        var machineFingerprintColValue = swapCols ? did : fingerprint;

        // Multi-seat: config override → reuse first seat's ACTIVATION_CODE → random only for the first device.
        var activationCode = await ResolveSharedActivationCodeForSeatAsync(
            conn,
            tx,
            licenseId,
            activatedAtUtc,
            cancellationToken).ConfigureAwait(false);

        const string remarks = "";
        if (existingLaId.HasValue)
        {
            await using var updateCmd = new FbCommand(
                """
                UPDATE LICENSE_ACTIVATION
                SET
                    DEVICE_ID = @deviceId,
                    ACTIVATION_CODE = @actCode,
                    MACHINE_FINGERPRINT = @fp,
                    ACTIVATED_AT = CURRENT_TIMESTAMP,
                    EXPIRED_ON = @expiredOn,
                    STATUS = 'ACTIVE',
                    REMARKS = @remarks
                WHERE LICENSE_ACTIVATION_ID = @laId
                """,
                conn,
                tx);
            updateCmd.Parameters.Add(new FbParameter("@deviceId", FbDbType.VarChar, FirebirdVarcharMax) { Value = deviceIdColValue });
            updateCmd.Parameters.Add(new FbParameter("@actCode", FbDbType.VarChar, FirebirdVarcharMax) { Value = activationCode });
            updateCmd.Parameters.Add(new FbParameter("@fp", FbDbType.VarChar, FirebirdVarcharMax) { Value = machineFingerprintColValue });
            updateCmd.Parameters.Add(new FbParameter("@expiredOn", FbDbType.TimeStamp) { Value = expiredOn });
            updateCmd.Parameters.Add(new FbParameter("@remarks", FbDbType.VarChar, FirebirdVarcharMax) { Value = remarks });
            updateCmd.Parameters.Add(new FbParameter("@laId", FbDbType.Integer) { Value = existingLaId.Value });

            await updateCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await using var insertCmd = new FbCommand(
                """
                INSERT INTO LICENSE_ACTIVATION (
                    LICENSE_ID,
                    DEVICE_ID,
                    ACTIVATION_CODE,
                    MACHINE_FINGERPRINT,
                    ACTIVATED_AT,
                    EXPIRED_ON,
                    STATUS,
                    REMARKS,
                    CREATED_AT,
                    CREATED_BY
                )
                VALUES (
                    @lid,
                    @deviceId,
                    @actCode,
                    @fp,
                    CURRENT_TIMESTAMP,
                    @expiredOn,
                    'ACTIVE',
                    @remarks,
                    CURRENT_TIMESTAMP,
                    'ABS_SYSTEM'
                )
                """,
                conn,
                tx);
            insertCmd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });
            insertCmd.Parameters.Add(new FbParameter("@deviceId", FbDbType.VarChar, FirebirdVarcharMax) { Value = deviceIdColValue });
            insertCmd.Parameters.Add(new FbParameter("@actCode", FbDbType.VarChar, FirebirdVarcharMax) { Value = activationCode });
            insertCmd.Parameters.Add(new FbParameter("@fp", FbDbType.VarChar, FirebirdVarcharMax) { Value = machineFingerprintColValue });
            insertCmd.Parameters.Add(new FbParameter("@expiredOn", FbDbType.TimeStamp) { Value = expiredOn });
            insertCmd.Parameters.Add(new FbParameter("@remarks", FbDbType.VarChar, FirebirdVarcharMax) { Value = remarks });

            await insertCmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        tx.Commit();
        return ActivationValidationResult.Ok();
    }

    /// <summary>LAAS <c>DEVICE_ID</c> is VARCHAR(64).</summary>
    private static string NormalizeLegacyDeviceIdFallback(string fingerprint)
    {
        if (string.IsNullOrEmpty(fingerprint))
            return "";
        return fingerprint.Length <= 64 ? fingerprint : fingerprint[..64];
    }

    /// <summary>Machine fingerprint value as stored in <c>LICENSE_ACTIVATION.DEVICE_ID</c> when columns are swapped (64 chars max).</summary>
    private static string FingerprintForLaDeviceIdColumn(string fingerprint) =>
        TrimSeatDeviceIdColumn((fingerprint ?? "").Trim().ToUpperInvariant());

    private static string TrimSeatDeviceIdColumn(string s)
    {
        s = s.Trim();
        return s.Length <= 64 ? s : s[..64];
    }

    /// <summary>Non-empty logical <c>DEVICE_ID</c> (trim, upper, max 64); null if absent.</summary>
    private static string? NormalizeUserProvidedSeatDeviceId(string? seatDeviceId)
    {
        var s = TrimSeatDeviceIdColumn((seatDeviceId ?? "").Trim().ToUpperInvariant());
        return string.IsNullOrEmpty(s) ? null : s;
    }

    /// <summary>Next <c>DEV-####</c> after the highest existing <c>DEV-</c> suffix for this license (within transaction when <paramref name="tx"/> is set).</summary>
    private static async Task<string> FormatNextLogicalDeviceIdAsync(
        FbConnection conn,
        FbTransaction? tx,
        int licenseId,
        bool devPrefixStoredInMachineFingerprintColumn,
        CancellationToken cancellationToken)
    {
        var col = devPrefixStoredInMachineFingerprintColumn ? "la.MACHINE_FINGERPRINT" : "la.DEVICE_ID";
        await using var cmd = new FbCommand(
            $"""
            SELECT COALESCE(MAX(
                CAST(TRIM(SUBSTRING(TRIM({col}) FROM 5 FOR 20)) AS INTEGER)
            ), 0)
            FROM LICENSE_ACTIVATION la
            WHERE la.LICENSE_ID = @lid
              AND UPPER(TRIM({col})) STARTING WITH 'DEV-'
              AND TRIM(SUBSTRING(TRIM({col}) FROM 5 FOR 20)) SIMILAR TO '[0-9]+'
            """,
            conn,
            tx);
        cmd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });
        var maxObj = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        var maxSuffix = Convert.ToInt32(maxObj, CultureInfo.InvariantCulture);
        var next = maxSuffix + 1;
        if (next < 1)
            next = 1;
        var suffix = next <= 9999
            ? next.ToString("D4", CultureInfo.InvariantCulture)
            : next.ToString(CultureInfo.InvariantCulture);
        return string.Concat("DEV-", suffix);
    }

    /// <summary>
    /// First device: random <see cref="ActivationCodeGenerator.Generate"/>; further devices: same as earliest
    /// non-empty <c>LICENSE_ACTIVATION.ACTIVATION_CODE</c> for this license (does not use <c>LICENSE.LICENSE_KEY</c>).
    /// Optional <see cref="ActivationOptions.ActivationCode"/> overrides for all seats.
    /// </summary>
    private async Task<string> ResolveSharedActivationCodeForSeatAsync(
        FbConnection conn,
        FbTransaction tx,
        int licenseId,
        DateTime activatedAtUtc,
        CancellationToken cancellationToken)
    {
        var fromConfig = (_opt.ActivationCode ?? "").Trim();
        if (!string.IsNullOrEmpty(fromConfig))
            return fromConfig;

        await using var firstCodeCmd = new FbCommand(
            """
            SELECT FIRST 1 TRIM(la.ACTIVATION_CODE)
            FROM LICENSE_ACTIVATION la
            WHERE la.LICENSE_ID = @lid
              AND la.ACTIVATION_CODE IS NOT NULL
              AND TRIM(la.ACTIVATION_CODE) <> ''
            ORDER BY la.LICENSE_ACTIVATION_ID
            """,
            conn,
            tx);
        firstCodeCmd.Parameters.Add(new FbParameter("@lid", FbDbType.Integer) { Value = licenseId });

        var existingObj = await firstCodeCmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (existingObj != null && existingObj != DBNull.Value)
        {
            var existing = Convert.ToString(existingObj, CultureInfo.InvariantCulture)?.Trim();
            if (!string.IsNullOrEmpty(existing))
                return existing;
        }

        return ActivationCodeGenerator.Generate(licenseId, activatedAtUtc);
    }

    private static DateTime ComputeExpiredOn(
        DateTime activatedAtUtc,
        string licenseType,
        DateTime? licenseStartDate,
        DateTime? licenseEndDate)
    {
        var activatedDate = activatedAtUtc.Date;
        var ltype = (licenseType ?? "").Trim().ToUpperInvariant();

        DateTime expDate;
        if (ltype == "TRIAL")
            expDate = activatedDate.AddDays(14);
        else if (ltype == "3 MONTHS")
            expDate = activatedDate.AddMonths(3);
        else if (ltype == "6 MONTHS")
            expDate = activatedDate.AddMonths(6);
        else if (ltype == "ANNUAL")
            expDate = activatedDate.AddYears(1);
        else if (ltype == "PERPETUAL")
            expDate = new DateTime(2099, 12, 31);
        else if (licenseEndDate.HasValue && licenseStartDate.HasValue)
        {
            var baseDays = Math.Max(0, (licenseEndDate.Value.Date - licenseStartDate.Value.Date).Days);
            expDate = activatedDate.AddDays(baseDays);
        }
        else if (licenseEndDate.HasValue)
        {
            expDate = licenseEndDate.Value.Date;
        }
        else
        {
            expDate = activatedDate.AddDays(365);
        }

        return new DateTime(expDate.Year, expDate.Month, expDate.Day, 23, 59, 59, DateTimeKind.Utc);
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
