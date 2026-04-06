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
    /// Validates using the blocked-page form. Legacy mode: activation code and/or machine fingerprint from configuration.
    /// Seat mode: requires client <paramref name="deviceFingerprint"/>; <paramref name="deviceId"/> (or cookie) unless
    /// <c>Activation:SeatIdentityUsesMachineFingerprint</c> is true (then <c>DEVICE_ID</c> is derived from the fingerprint).
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

    /// <summary>
    /// Seat mode: if an active row exists for <c>LICENSE_ID</c> + local <c>DEVICE_ID</c>, returns <c>DEVICE_ID</c>.
    /// Pass overrides when the UI/cookies differ from <see cref="GetSeatDeviceIdForDisplayAsync"/> (fingerprint override is used only in legacy mode).
    /// Legacy mode: first row matching <c>MACHINE_FINGERPRINT</c> only.
    /// </summary>
    Task<string?> GetRegisteredDeviceIdForMachineFingerprintAsync(
        string? machineFingerprintOverride,
        string? deviceIdOverride,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Seat mode: logical device id from the <c>Activation:DeviceIdCookieName</c> cookie (or empty). Not used in legacy mode.
    /// When <c>Activation:SeatIdentityUsesMachineFingerprint</c> is true, derives from the current machine fingerprint instead.
    /// </summary>
    Task<string> GetSeatDeviceIdForDisplayAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Seat mode: <c>LICENSE_ACTIVATION.DEVICE_ID</c> value derived from a machine fingerprint (first 64 chars, upper-case).
    /// </summary>
    string DeriveSeatDeviceIdFromFingerprint(string? machineFingerprint);

    /// <summary>
    /// Legacy (no <c>Activation:LicenseId</c>): when <c>DEVICE_ID</c> is left blank, manual LA rows often use the first 64 chars of the fingerprint.
    /// Seat mode: returns empty — use <see cref="GetPreviewNextLogicalDeviceIdAsync"/> for the next <c>DEV-####</c> label.
    /// </summary>
    string GetSuggestedDeviceIdForManualLaRow(string? machineFingerprint);

    /// <summary>
    /// Seat mode only: next suggested <c>DEV-####</c> for manual Firebird inserts (read-only preview). The live app still requires a concrete <c>DEVICE_ID</c> that matches the client.
    /// </summary>
    Task<string> GetPreviewNextLogicalDeviceIdAsync(CancellationToken cancellationToken = default);
}
