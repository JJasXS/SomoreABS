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
    public string Password { get; set; } = "masterkey";
    public int Dialect { get; set; } = 3;
    public string Charset { get; set; } = "UTF8";

    /// <summary>Optional: full Firebird connection string. If set, Server/Port/Database/User/Password are ignored.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>Activation code to match LICENSE_ACTIVATION.ACTIVATION_CODE (trimmed).</summary>
    public string? ActivationCode { get; set; }

    /// <summary>Optional: match LICENSE_ACTIVATION.MACHINE_FINGERPRINT if ActivationCode is not used.</summary>
    public string? MachineFingerprint { get; set; }
}
