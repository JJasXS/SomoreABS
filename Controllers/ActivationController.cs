using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YourApp.Data;
using YourApp.Services;

namespace YourApp.Controllers;

[AllowAnonymous]
public class ActivationController : Controller
{
    private readonly IActivationValidationService _activation;
    private readonly ActivationOptions _opt;
    private readonly DbInitializer _dbInit;
    private readonly ILogger<ActivationController> _log;

    public ActivationController(
        IActivationValidationService activation,
        IOptions<ActivationOptions> activationOptions,
        DbInitializer dbInit,
        ILogger<ActivationController> log)
    {
        _activation = activation;
        _opt = activationOptions.Value;
        _dbInit = dbInit;
        _log = log;
    }

    /// <summary>Shown when the system is not activated or validation failed.</summary>
    [HttpGet]
    public async Task<IActionResult> Blocked(CancellationToken cancellationToken)
    {
        ViewBag.ShowActivationForm = _opt.Enabled;
        ViewBag.Message = _activation.LastFailureMessage
                          ?? "Activation not found or expired. Please activate your system.";
        ViewBag.SeatEnforcement = _opt.LicenseId.HasValue;
        ViewBag.ConfiguredLicenseId = _opt.LicenseId;
        ViewBag.SeatIdentityUsesMachineFingerprint = _opt.LicenseId.HasValue && _opt.SeatIdentityUsesMachineFingerprint;
        ViewBag.SeatSwapFingerprintAndDeviceIdColumns = _opt.LicenseId.HasValue && _opt.SeatSwapFingerprintAndDeviceIdColumns;
        ViewBag.SeatRequireLicenseKey = _opt.LicenseId.HasValue && _opt.RequireSeatLicenseKey;
        ViewBag.DeviceIdCookieName = (_opt.DeviceIdCookieName ?? "ABS_DeviceLogicalId").Trim();
        var fp = await _activation
            .GetMachineFingerprintForDisplayAsync(cancellationToken)
            .ConfigureAwait(false);
        ViewBag.MachineFingerprintHex = fp;
        ViewBag.SeatDeviceIdCookie = await _activation.GetSeatDeviceIdForDisplayAsync(cancellationToken).ConfigureAwait(false);
        /* Seat: DEVICE_ID is only from the Blocked form script — never loaded from the activation DB for display. */
        ViewBag.ResolvedDeviceIdFromDb = _opt.LicenseId.HasValue
            ? null
            : await _activation
                .GetRegisteredDeviceIdForMachineFingerprintAsync(null, null, cancellationToken)
                .ConfigureAwait(false);
        ViewBag.SuggestedDeviceIdForLa = _activation.GetSuggestedDeviceIdForManualLaRow(fp);
        ViewBag.PreviewNextLogicalDeviceId = _opt.LicenseId.HasValue
            ? await _activation.GetPreviewNextLogicalDeviceIdAsync(cancellationToken).ConfigureAwait(false)
            : "";
        ViewBag.SeatClientNonce = _opt.LicenseId.HasValue ? EnsureSeatClientNonceCookie() : "";
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(
        [FromForm] string? activationCode,
        [FromForm] string? deviceFingerprint,
        [FromForm] string? deviceId,
        [FromForm] string? seatClientNonce,
        CancellationToken cancellationToken)
    {
        if (!_opt.Enabled)
            return RedirectToAction("Index", "Home");

        if (_opt.LicenseId.HasValue && !ValidateSeatClientNonceFormMatchesCookie(seatClientNonce))
        {
            await PopulateBlockedViewForSeatErrorAsync(
                "This activation session is out of date or came from another browser. Refresh the page and use the Device ID shown here.",
                activationCode,
                deviceFingerprint,
                deviceId,
                cancellationToken).ConfigureAwait(false);
            return View("Blocked");
        }

        var result = await _activation
            .ValidateSubmittedCodeAsync(activationCode, deviceFingerprint, deviceId, cancellationToken)
            .ConfigureAwait(false);
        if (result.Success)
        {
            try
            {
                _dbInit.EnsureAllStartupSchemas();
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Firebird schema init after activation failed.");
                ViewBag.ShowActivationForm = true;
                ViewBag.SeatEnforcement = _opt.LicenseId.HasValue;
                ViewBag.ConfiguredLicenseId = _opt.LicenseId;
                ViewBag.SeatIdentityUsesMachineFingerprint = _opt.LicenseId.HasValue && _opt.SeatIdentityUsesMachineFingerprint;
                ViewBag.SeatSwapFingerprintAndDeviceIdColumns = _opt.LicenseId.HasValue && _opt.SeatSwapFingerprintAndDeviceIdColumns;
                ViewBag.SeatRequireLicenseKey = _opt.LicenseId.HasValue && _opt.RequireSeatLicenseKey;
                ViewBag.DeviceIdCookieName = (_opt.DeviceIdCookieName ?? "ABS_DeviceLogicalId").Trim();
                ViewBag.Message =
                    "Activation succeeded but database setup failed. Check logs and the client Firebird path in TENANT_DB_PROFILE.";
                var fpDisp = await _activation
                    .GetMachineFingerprintForDisplayAsync(cancellationToken)
                    .ConfigureAwait(false);
                ViewBag.MachineFingerprintHex = fpDisp;
                ViewBag.SeatDeviceIdCookie = await _activation.GetSeatDeviceIdForDisplayAsync(cancellationToken).ConfigureAwait(false);
                ViewBag.ResolvedDeviceIdFromDb = _opt.LicenseId.HasValue
                    ? null
                    : await _activation
                        .GetRegisteredDeviceIdForMachineFingerprintAsync(deviceFingerprint, deviceId, cancellationToken)
                        .ConfigureAwait(false);
                ViewBag.SuggestedDeviceIdForLa = _activation.GetSuggestedDeviceIdForManualLaRow(
                    string.IsNullOrWhiteSpace(deviceFingerprint) ? fpDisp : deviceFingerprint);
                ViewBag.PreviewNextLogicalDeviceId = _opt.LicenseId.HasValue
                    ? await _activation.GetPreviewNextLogicalDeviceIdAsync(cancellationToken).ConfigureAwait(false)
                    : "";
                ViewBag.FormActivationCode = activationCode;
                ViewBag.FormDeviceFingerprint = deviceFingerprint;
                ViewBag.FormDeviceId = deviceId;
                ViewBag.ActivationFormPostBack = true;
                ViewBag.SeatClientNonce = _opt.LicenseId.HasValue ? EnsureSeatClientNonceCookie() : "";
                return View("Blocked");
            }

            AppendActivationIdentityCookies(deviceFingerprint, deviceId);

            return RedirectToAction("Index", "Home");
        }

        ViewBag.ShowActivationForm = true;
        ViewBag.SeatEnforcement = _opt.LicenseId.HasValue;
        ViewBag.ConfiguredLicenseId = _opt.LicenseId;
        ViewBag.SeatIdentityUsesMachineFingerprint = _opt.LicenseId.HasValue && _opt.SeatIdentityUsesMachineFingerprint;
        ViewBag.SeatSwapFingerprintAndDeviceIdColumns = _opt.LicenseId.HasValue && _opt.SeatSwapFingerprintAndDeviceIdColumns;
        ViewBag.SeatRequireLicenseKey = _opt.LicenseId.HasValue && _opt.RequireSeatLicenseKey;
        ViewBag.DeviceIdCookieName = (_opt.DeviceIdCookieName ?? "ABS_DeviceLogicalId").Trim();
        ViewBag.Message = result.Message;
        var fpDisp2 = await _activation
            .GetMachineFingerprintForDisplayAsync(cancellationToken)
            .ConfigureAwait(false);
        ViewBag.MachineFingerprintHex = fpDisp2;
        ViewBag.SeatDeviceIdCookie = await _activation.GetSeatDeviceIdForDisplayAsync(cancellationToken).ConfigureAwait(false);
        ViewBag.ResolvedDeviceIdFromDb = _opt.LicenseId.HasValue
            ? null
            : await _activation
                .GetRegisteredDeviceIdForMachineFingerprintAsync(deviceFingerprint, deviceId, cancellationToken)
                .ConfigureAwait(false);
        ViewBag.SuggestedDeviceIdForLa = _activation.GetSuggestedDeviceIdForManualLaRow(
            string.IsNullOrWhiteSpace(deviceFingerprint) ? fpDisp2 : deviceFingerprint);
        ViewBag.PreviewNextLogicalDeviceId = _opt.LicenseId.HasValue
            ? await _activation.GetPreviewNextLogicalDeviceIdAsync(cancellationToken).ConfigureAwait(false)
            : "";
        ViewBag.FormActivationCode = activationCode;
        ViewBag.FormDeviceFingerprint = deviceFingerprint;
        ViewBag.FormDeviceId = deviceId;
        ViewBag.ActivationFormPostBack = true;
        ViewBag.SeatClientNonce = _opt.LicenseId.HasValue ? EnsureSeatClientNonceCookie() : "";
        return View("Blocked");
    }

    /// <summary>
    /// Sets HttpOnly fingerprint / device-id cookies from the Blocked page script (same values as a successful <see cref="Submit"/>).
    /// JavaScript cannot set HttpOnly cookies, so the browser POSTs here after generating fingerprint + device id so <c>ValidateAsync</c>
    /// can run against the activation database on the next navigation without requiring an extra Activate click.
    /// Activation rules are still enforced by Firebird on each request; this only mirrors client identity into cookies.
    /// </summary>
    [HttpPost]
    [ValidateAntiForgeryToken]
    public IActionResult SyncClientCookies(
        [FromForm] string? deviceFingerprint,
        [FromForm] string? deviceId,
        [FromForm] string? seatClientNonce)
    {
        if (!_opt.Enabled)
            return Json(new { ok = true });

        if (_opt.LicenseId.HasValue && !ValidateSeatClientNonceFormMatchesCookie(seatClientNonce))
            return BadRequest(new { ok = false, error = "invalid_nonce" });

        AppendActivationIdentityCookies(deviceFingerprint, deviceId);
        return Json(new { ok = true });
    }

    private void AppendActivationIdentityCookies(string? deviceFingerprint, string? deviceId)
    {
        if (!string.IsNullOrWhiteSpace(deviceFingerprint))
        {
            var fp = deviceFingerprint.Trim().ToUpperInvariant();
            var cookieName = (_opt.DeviceFingerprintCookieName ?? "ABS_DeviceFingerprint").Trim();
            if (!string.IsNullOrWhiteSpace(cookieName) && fp.Length > 0)
            {
                /* Secure must follow the request scheme or browsers drop cookies on http://localhost (device id never sticks). */
                Response.Cookies.Append(cookieName, fp, new CookieOptions
                {
                    HttpOnly = true,
                    Secure = Request.IsHttps,
                    SameSite = SameSiteMode.Strict,
                    Path = "/",
                    Expires = DateTimeOffset.UtcNow.AddDays(365)
                });
            }
        }

        /* Seat gate: persist DEVICE_ID (submitted or derived from fingerprint) so later requests validate with cookies. */
        if (!_opt.LicenseId.HasValue)
            return;

        var didCookie = (_opt.DeviceIdCookieName ?? "ABS_DeviceLogicalId").Trim();
        string? normalizedDid = null;
        if (_opt.SeatIdentityUsesMachineFingerprint && !_opt.SeatSwapFingerprintAndDeviceIdColumns
            && !string.IsNullOrWhiteSpace(deviceFingerprint))
            normalizedDid = _activation.DeriveSeatDeviceIdFromFingerprint(deviceFingerprint.Trim().ToUpperInvariant());
        else if (!string.IsNullOrWhiteSpace(deviceId))
            normalizedDid = deviceId.Trim().ToUpperInvariant();

        if (!string.IsNullOrEmpty(didCookie) && !string.IsNullOrWhiteSpace(normalizedDid))
        {
            Response.Cookies.Append(didCookie, normalizedDid, new CookieOptions
            {
                HttpOnly = true,
                Secure = Request.IsHttps,
                SameSite = SameSiteMode.Strict,
                Path = "/",
                Expires = DateTimeOffset.UtcNow.AddDays(365)
            });
        }
    }

    /// <summary>HttpOnly nonce per browser on this machine — not synced with other desktops like localStorage can be.</summary>
    private string EnsureSeatClientNonceCookie()
    {
        var name = (_opt.SeatClientNonceCookieName ?? "ABS_SeatClientNonce").Trim();
        if (string.IsNullOrEmpty(name))
            return "";

        var existing = Request.Cookies[name];
        if (!string.IsNullOrWhiteSpace(existing) && existing.Length >= 8 && existing.Length <= 128)
            return existing.Trim();

        var nonce = Guid.NewGuid().ToString("N");
        Response.Cookies.Append(name, nonce, new CookieOptions
        {
            HttpOnly = true,
            Secure = Request.IsHttps,
            SameSite = SameSiteMode.Strict,
            Path = "/",
            Expires = DateTimeOffset.UtcNow.AddDays(365)
        });
        return nonce;
    }

    private bool ValidateSeatClientNonceFormMatchesCookie(string? formNonce)
    {
        var name = (_opt.SeatClientNonceCookieName ?? "ABS_SeatClientNonce").Trim();
        if (string.IsNullOrEmpty(name))
            return true;

        var cookieVal = Request.Cookies[name];
        if (string.IsNullOrWhiteSpace(cookieVal))
            return false;

        return string.Equals((formNonce ?? "").Trim(), cookieVal.Trim(), StringComparison.Ordinal);
    }

    private async Task PopulateBlockedViewForSeatErrorAsync(
        string message,
        string? activationCode,
        string? deviceFingerprint,
        string? deviceId,
        CancellationToken cancellationToken)
    {
        ViewBag.ShowActivationForm = true;
        ViewBag.SeatEnforcement = _opt.LicenseId.HasValue;
        ViewBag.ConfiguredLicenseId = _opt.LicenseId;
        ViewBag.SeatIdentityUsesMachineFingerprint = _opt.LicenseId.HasValue && _opt.SeatIdentityUsesMachineFingerprint;
        ViewBag.SeatSwapFingerprintAndDeviceIdColumns = _opt.LicenseId.HasValue && _opt.SeatSwapFingerprintAndDeviceIdColumns;
        ViewBag.SeatRequireLicenseKey = _opt.LicenseId.HasValue && _opt.RequireSeatLicenseKey;
        ViewBag.DeviceIdCookieName = (_opt.DeviceIdCookieName ?? "ABS_DeviceLogicalId").Trim();
        ViewBag.Message = message;
        var fpDisp2 = await _activation
            .GetMachineFingerprintForDisplayAsync(cancellationToken)
            .ConfigureAwait(false);
        ViewBag.MachineFingerprintHex = fpDisp2;
        ViewBag.SeatDeviceIdCookie = await _activation.GetSeatDeviceIdForDisplayAsync(cancellationToken).ConfigureAwait(false);
        ViewBag.ResolvedDeviceIdFromDb = _opt.LicenseId.HasValue
            ? null
            : await _activation
                .GetRegisteredDeviceIdForMachineFingerprintAsync(deviceFingerprint, deviceId, cancellationToken)
                .ConfigureAwait(false);
        ViewBag.SuggestedDeviceIdForLa = _activation.GetSuggestedDeviceIdForManualLaRow(
            string.IsNullOrWhiteSpace(deviceFingerprint) ? fpDisp2 : deviceFingerprint);
        ViewBag.PreviewNextLogicalDeviceId = _opt.LicenseId.HasValue
            ? await _activation.GetPreviewNextLogicalDeviceIdAsync(cancellationToken).ConfigureAwait(false)
            : "";
        ViewBag.FormActivationCode = activationCode;
        ViewBag.FormDeviceFingerprint = deviceFingerprint;
        ViewBag.FormDeviceId = deviceId;
        ViewBag.ActivationFormPostBack = true;
        ViewBag.SeatClientNonce = _opt.LicenseId.HasValue ? EnsureSeatClientNonceCookie() : "";
    }
}
