namespace YourApp.Services;

public interface IActivationValidationService
{
    /// <summary>True when gate is disabled or last validation succeeded.</summary>
    bool IsActivationValid { get; }

    /// <summary>Human-readable reason when invalid (or empty if valid / disabled).</summary>
    string? LastFailureMessage { get; }

    /// <summary>Runs validation against ACTIVATION.FDB and caches the result for the process lifetime.</summary>
    Task<ActivationValidationResult> ValidateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Validates using a code entered in the UI (does not require Activation:ActivationCode in config).
    /// On success, updates the cached result so the activation gate allows access.
    /// </summary>
    Task<ActivationValidationResult> ValidateSubmittedCodeAsync(string? activationCode, CancellationToken cancellationToken = default);
}
