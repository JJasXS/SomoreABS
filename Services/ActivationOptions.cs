namespace YourApp.Services;

/// <summary>Configuration for the external Activation Management Firebird database (LICENSE_ACTIVATION).</summary>
public class ActivationOptions
{
    public const string SectionName = "Activation";

    /// <summary>When false, the activation gate is skipped (e.g. local development).</summary>
    public bool Enabled { get; set; } = true;

    public string Server { get; set; } = "localhost";
    public int Port { get; set; } = 3050;

    /// <summary>Path to ACTIVATION.FDB (or full connection if you override via ConnectionString).</summary>
    public string Database { get; set; } =
        @"C:\eStream\SQLAccounting\DB\ACTIVATION.FDB";

    public string User { get; set; } = "SYSDBA";

    /// <summary>Firebird SYSDBA password for ACTIVATION.FDB. Default matches typical local Firebird installs; override in config or <c>Activation__Password</c>.</summary>
    public string Password { get; set; } = "masterkey";
    public int Dialect { get; set; } = 3;
    public string Charset { get; set; } = "UTF8";

    /// <summary>Optional: full Firebird connection string. If set, Server/Port/Database/User/Password are ignored.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Optional override written to every new seat's <c>ACTIVATION_CODE</c>. If empty, seat registration uses the first existing
    /// <c>LICENSE_ACTIVATION.ACTIVATION_CODE</c> for that license, or generates a new code for the first device only (same as Activation app).
    /// </summary>
    public string? ActivationCode { get; set; }

    /// <summary>
    /// Machine identity for this deployment (must match LICENSE_ACTIVATION.MACHINE_FINGERPRINT when validating by code).
    /// Fingerprint-only activation is supported when <see cref="ActivationCode"/> is empty.
    /// </summary>
    public string? MachineFingerprint { get; set; }

    /// <summary>
    /// When true and <see cref="MachineFingerprint"/> is empty, compute SHA-256 fingerprint using the same rules as
    /// the Activation LAAS Python SDK (<c>licensing_sdk.fingerprint</c>). Recommended for deployments activated via the API.
    /// </summary>
    public bool UseAutoMachineFingerprint { get; set; } = true;

    /// <summary>
    /// When true (default), the fingerprint is written to SQL Server table <c>LocalDeploymentInfo</c> (singleton row) on first use
    /// and reused across restarts—queryable for <c>LICENSE_ACTIVATION.MACHINE_FINGERPRINT</c> without using the CLI.
    /// </summary>
    public bool PersistMachineFingerprintInSqlServer { get; set; } = true;

    /// <summary>
    /// Optional: when set, ABS_System can auto-register new client devices (based on a per-client fingerprint cookie)
    /// into <c>LICENSE_ACTIVATION</c> under this specific <c>LICENSE_ID</c>.
    /// </summary>
    public int? LicenseId { get; set; }

    /// <summary>
    /// When <see cref="LicenseId"/> is set, enables automatic insertion of a new <c>LICENSE_ACTIVATION</c> row
    /// for the current device fingerprint (if under <c>LICENSE.MAX_USER_COUNT</c>).
    /// </summary>
    public bool AutoRegisterNewDevices { get; set; } = true;

    /// <summary>
    /// Cookie name that stores the per-device fingerprint used for seat counting. Only used when <see cref="LicenseId"/> is set.
    /// </summary>
    public string DeviceFingerprintCookieName { get; set; } = "ABS_DeviceFingerprint";
}
