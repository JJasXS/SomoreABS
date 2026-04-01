namespace YourApp.Models;

/// <summary>Tenant/product context from ACTIVATION.FDB after a successful LICENSE_ACTIVATION lookup.</summary>
public sealed class ActivationTenantSnapshot
{
    public string TenantCode { get; init; } = "";
    public string CompanyName { get; init; } = "";
    public string? ProductCode { get; init; }
    public string? ProductName { get; init; }

    /// <summary>
    /// From TENANT_DB_PROFILE: LICENSE.TENANT_DB_PROFILE_ID if set, else tenant default ACTIVE profile.
    /// </summary>
    public ClientDatabaseConnectionInfo? ClientDatabase { get; init; }
}
