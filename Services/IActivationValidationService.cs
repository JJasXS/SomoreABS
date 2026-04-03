using YourApp.Models;

namespace YourApp.Services;

public interface IActivationValidationService
{
    /// <summary>True when gate is disabled or last validation succeeded.</summary>
    bool IsActivationValid { get; }

    /// <summary>Tenant/product from the activation DB after the last successful validation; null if none.</summary>
    ActivationTenantSnapshot? ActivatedTenant { get; }

    /// <summary>Human-readable reason when invalid (or empty if valid / disabled).</summary>
    string? LastFailureMessage { get; }

    /// <summary>Runs validation against ACTIVATION.FDB (re-reads the database; no long-lived stale cache).</summary>
    Task<ActivationValidationResult> ValidateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates using a code entered in the UI (uses Activation:MachineFingerprint from configuration).
    /// On success, updates the last validation result so the activation gate allows access.
    /// </summary>
    Task<ActivationValidationResult> ValidateSubmittedCodeAsync(
        string? activationCode,
        string? deviceFingerprint,
        string? deviceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the machine fingerprint used for activation (config, SQL-persisted, or auto-computed). Empty if unavailable.
    /// </summary>
    Task<string> GetMachineFingerprintForDisplayAsync(CancellationToken cancellationToken = default);
}
