namespace YourApp.Services;

/// <summary>Resolves the main (client) Firebird connection string: from TENANT_DB_PROFILE when activation is on, else config.</summary>
public interface IClientFirebirdConnectionProvider
{
    /// <summary>
    /// Returns a ready Firebird connection string, or throws if the app cannot connect to the client database yet.
    /// </summary>
    string GetConnectionString();
}
