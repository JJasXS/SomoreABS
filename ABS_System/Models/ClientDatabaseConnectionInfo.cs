namespace YourApp.Models;

/// <summary>
/// Client accounting/booking Firebird target from TENANT_DB_PROFILE in ACTIVATION.FDB
/// (resolved after a successful LICENSE_ACTIVATION lookup).
/// </summary>
public sealed class ClientDatabaseConnectionInfo
{
    public string? DatabasePath { get; init; }
    public string? DataSource { get; init; }
    public int? Port { get; init; }
    public string? User { get; init; }
    public string? Password { get; init; }
}
