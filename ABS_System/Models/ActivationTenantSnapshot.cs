namespace YourApp.Models;

/// <summary>Tenant/product context from ACTIVATION.FDB after a successful LICENSE_ACTIVATION lookup.</summary>
public sealed class ActivationTenantSnapshot
{
    public string TenantCode { get; init; } = "";
    public string CompanyName { get; init; } = "";
    public string? ProductCode { get; init; }
    public string? ProductName { get; init; }
}
