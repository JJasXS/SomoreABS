namespace YourApp.Services;

/// <summary>Fallback defaults for the client Firebird DB when TENANT_DB_PROFILE omits host/port/credentials.</summary>
public sealed class ClientFirebirdOptions
{
    public const string SectionName = "Firebird";

    public string Server { get; set; } = "localhost";
    public int Port { get; set; } = 3050;

    /// <summary>Used only when Activation is disabled (local dev) if ConnectionStrings:Firebird is empty.</summary>
    public string Database { get; set; } = "";

    public string User { get; set; } = "SYSDBA";
    public string Password { get; set; } = "masterkey";
    public int Dialect { get; set; } = 3;
    public string Charset { get; set; } = "UTF8";
}
