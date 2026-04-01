using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Options;

namespace YourApp.Services;

public sealed class ActivationValidationService : IActivationValidationService
{
    /// <summary>Firebird 3 max VARCHAR length; explicit size avoids "string right truncation" when binding long codes.</summary>
    private const int FirebirdVarcharMax = 32765;

    private readonly ActivationOptions _opt;
    private readonly ILogger<ActivationValidationService> _log;
    private readonly object _sync = new();
    private ActivationValidationResult? _cached;
    private bool _validated;

    public ActivationValidationService(IOptions<ActivationOptions> options, ILogger<ActivationValidationService> log)
    {
        _opt = options.Value;
        _log = log;
    }

    public bool IsActivationValid =>
        !_opt.Enabled || (_validated && _cached?.Success == true);

    public string? LastFailureMessage =>
        _validated && _cached?.Success == false ? _cached.Message : null;

    public async Task<ActivationValidationResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (!_opt.Enabled)
        {
            var ok = ActivationValidationResult.Ok("Activation gate disabled in configuration.");
            lock (_sync)
            {
                _cached = ok;
                _validated = true;
            }
            return ok;
        }

        lock (_sync)
        {
            if (_validated && _cached != null)
                return _cached;
        }

        var code = (_opt.ActivationCode ?? "").Trim();
        var fp = (_opt.MachineFingerprint ?? "").Trim();
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
            }
            return ok;
        }

        var code = (activationCode ?? "").Trim();
        var fp = (_opt.MachineFingerprint ?? "").Trim();
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

    private async Task<ActivationValidationResult> ValidateCoreAsync(string code, string fingerprint, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(code) && string.IsNullOrEmpty(fingerprint))
            return ActivationValidationResult.Fail(
                "Activation is not configured: set Activation:ActivationCode or Activation:MachineFingerprint.");

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
                TRIM(t.TENANT_CODE),
                TRIM(t.COMPANY_NAME),
                TRIM(p.PRODUCT_CODE),
                TRIM(p.PRODUCT_NAME)
            FROM LICENSE_ACTIVATION la
            JOIN LICENSE l ON l.LICENSE_ID = la.LICENSE_ID
            JOIN TENANT t ON t.TENANT_ID = l.TENANT_ID
            JOIN PRODUCT p ON p.PRODUCT_ID = l.PRODUCT_ID
            """;

        var whereParts = new List<string>(capacity: 2);
        if (!string.IsNullOrEmpty(code))
            whereParts.Add("TRIM(la.ACTIVATION_CODE) = @code");
        if (!string.IsNullOrEmpty(fingerprint))
            whereParts.Add("TRIM(la.MACHINE_FINGERPRINT) = @fp");

        sql += "\nWHERE " + string.Join("\n   OR ", whereParts);

        await using var cmd = new FbCommand(sql, conn);
        if (!string.IsNullOrEmpty(code))
            AddVarcharParameter(cmd, "@code", code);
        if (!string.IsNullOrEmpty(fingerprint))
            AddVarcharParameter(cmd, "@fp", fingerprint);

        try
        {
            await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
                return ActivationValidationResult.Fail(
                    "Activation not found or expired. Please activate your system.");

            var activationStatus = reader.IsDBNull(6) ? "" : reader.GetString(6);
            var licenseStatus = reader.IsDBNull(8) ? "" : reader.GetString(8);
            var expiredOn = reader.IsDBNull(5) ? (DateTime?)null : reader.GetDateTime(5);
            var startDate = reader.IsDBNull(9) ? (DateTime?)null : reader.GetDateTime(9);
            var endDate = reader.IsDBNull(10) ? (DateTime?)null : reader.GetDateTime(10);

            var now = DateTime.UtcNow;
            var today = DateTime.UtcNow.Date;

            if (!string.Equals(activationStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                return ActivationValidationResult.Fail(
                    "Activation not found or expired. Please activate your system.");

            if (!string.Equals(licenseStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
                return ActivationValidationResult.Fail(
                    "Activation not found or expired. Please activate your system.");

            if (expiredOn.HasValue && now > expiredOn.Value)
                return ActivationValidationResult.Fail(
                    "Activation not found or expired. Please activate your system.");

            if (startDate.HasValue && today < startDate.Value.Date)
                return ActivationValidationResult.Fail(
                    "Activation not found or expired. Please activate your system.");

            if (endDate.HasValue && today > endDate.Value.Date)
                return ActivationValidationResult.Fail(
                    "Activation not found or expired. Please activate your system.");

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

        var b = new FbConnectionStringBuilder
        {
            DataSource = _opt.Server,
            Port = _opt.Port,
            Database = _opt.Database,
            UserID = _opt.User,
            Password = _opt.Password,
            Charset = _opt.Charset,
            Dialect = _opt.Dialect
        };
        return b.ConnectionString;
    }

    private static void AddVarcharParameter(FbCommand cmd, string name, string value)
    {
        // Default FbParameter(string, object) uses a tiny buffer; long activation codes then fail with string right truncation.
        var p = new FbParameter(name, FbDbType.VarChar, FirebirdVarcharMax)
        {
            Value = value
        };
        cmd.Parameters.Add(p);
    }
}
