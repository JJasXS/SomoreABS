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

    /// <summary>Firebird password. Prefer environment variable <c>Activation__Password</c> or User Secrets — do not commit real passwords.</summary>
    public string Password { get; set; } = "";
    public int Dialect { get; set; } = 3;
    public string Charset { get; set; } = "UTF8";

    /// <summary>Optional: full Firebird connection string. If set, Server/Port/Database/User/Password are ignored.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Activation code to match LICENSE_ACTIVATION.ACTIVATION_CODE (trimmed). Requires <see cref="MachineFingerprint"/> when set.</summary>
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
}
