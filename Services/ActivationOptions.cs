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

    /// <summary>Firebird SYSDBA password for ACTIVATION.FDB. Default matches typical local Firebird installs; override in config or <c>Activation__Password</c>.</summary>
    public string Password { get; set; } = "masterkey";
    public int Dialect { get; set; } = 3;
    public string Charset { get; set; } = "UTF8";

    /// <summary>Optional: full Firebird connection string. If set, Server/Port/Database/User/Password are ignored.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Optional override written to every new seat's <c>ACTIVATION_CODE</c>. If empty, seat registration uses the first existing
    /// <c>LICENSE_ACTIVATION.ACTIVATION_CODE</c> for that license, or generates a new code for the first device only (same as Activation app).
    /// </summary>
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

    /// <summary>
    /// When true (default), the fingerprint is written to SQL Server table <c>LocalDeploymentInfo</c> (singleton row) on first use
    /// and reused across restarts—queryable for <c>LICENSE_ACTIVATION.MACHINE_FINGERPRINT</c> without using the CLI.
    /// </summary>
    public bool PersistMachineFingerprintInSqlServer { get; set; } = true;

    /// <summary>
    /// When set, enables seat licensing: each activated device is a row keyed by <c>LICENSE_ID</c> + local <c>DEVICE_ID</c>
    /// (see <see cref="DeviceIdCookieName"/>). <c>LICENSE.MAX_DEVICE_COUNT</c> limits active seats (seat mode ignores
    /// <c>LICENSE.MAX_USER_COUNT</c> for this cap). Not legacy mode.
    /// </summary>
    public int? LicenseId { get; set; }

    /// <summary>
    /// When <see cref="LicenseId"/> is set, allows automatic insert of a new <c>LICENSE_ACTIVATION</c> seat for this
    /// local <c>DEVICE_ID</c> when under <c>LICENSE.MAX_DEVICE_COUNT</c>.
    /// </summary>
    public bool AutoRegisterNewDevices { get; set; } = true;

    /// <summary>
    /// When <see cref="LicenseId"/> is set and this is true, each seat is keyed by <c>MACHINE_FINGERPRINT</c> alone:
    /// <c>DEVICE_ID</c> in the database is the first 64 characters of the fingerprint (same as legacy fallback).
    /// Use when browser-generated Device IDs are unreliable (sync, collisions). Trade-off: two users on the same PC/browser profile share one seat.
    /// </summary>
    public bool SeatIdentityUsesMachineFingerprint { get; set; }

    /// <summary>
    /// Cookie name for <c>MACHINE_FINGERPRINT</c> sent by the client (seat mode). May match other users if the host shares one fingerprint.
    /// </summary>
    public string DeviceFingerprintCookieName { get; set; } = "ABS_DeviceFingerprint";

    /// <summary>
    /// Cookie name for the required local desktop / workstation <c>DEVICE_ID</c> (distinct from <see cref="DeviceFingerprintCookieName"/>).
    /// Seat identity for counting; set by the Blocked page script and on successful activation.
    /// </summary>
    public string DeviceIdCookieName { get; set; } = "ABS_DeviceLogicalId";

    /// <summary>
    /// HttpOnly cookie set on <c>/Activation/Blocked</c> (seat mode): random per browser profile on this machine.
    /// Not synced across desktops; bundled with <c>localStorage</c> device id so synced storage from another PC is rejected.
    /// </summary>
    public string SeatClientNonceCookieName { get; set; } = "ABS_SeatClientNonce";

    /// <summary>
    /// Seat mode: if lookup by <c>DEVICE_ID</c> fails but the request includes an activation code and the HttpOnly
    /// fingerprint + device id cookies match the submitted form values (same browser session as a prior success),
    /// find the active row by <c>LICENSE_ID</c> + <c>ACTIVATION_CODE</c>, update <c>MACHINE_FINGERPRINT</c>/<c>DEVICE_ID</c>
    /// to match this browser, then validate. Allows re-binding when fingerprint/device identity changes without editing Firebird by hand.
    /// </summary>
    public bool SeatTrustBrowserCookiesWithActivationCode { get; set; } = true;
}
