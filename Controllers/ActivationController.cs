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
        ViewBag.MachineFingerprintHex = await _activation
            .GetMachineFingerprintForDisplayAsync(cancellationToken)
            .ConfigureAwait(false);
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(
        [FromForm] string? activationCode,
        [FromForm] string? deviceFingerprint,
        [FromForm] string? deviceId,
        CancellationToken cancellationToken)
    {
        if (!_opt.Enabled)
            return RedirectToAction("Index", "Home");

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
                ViewBag.Message =
                    "Activation succeeded but database setup failed. Check logs and the client Firebird path in TENANT_DB_PROFILE.";
                ViewBag.MachineFingerprintHex = await _activation
                    .GetMachineFingerprintForDisplayAsync(cancellationToken)
                    .ConfigureAwait(false);
                return View("Blocked");
            }

            if (!string.IsNullOrWhiteSpace(deviceFingerprint))
            {
                var fp = deviceFingerprint.Trim().ToUpperInvariant();
                var cookieName = (_opt.DeviceFingerprintCookieName ?? "ABS_DeviceFingerprint").Trim();
                if (!string.IsNullOrWhiteSpace(cookieName) && fp.Length > 0)
                {
                    Response.Cookies.Append(cookieName, fp, new CookieOptions
                    {
                        HttpOnly = true,
                        Secure = true,
                        SameSite = SameSiteMode.Strict,
                        Path = "/",
                        Expires = DateTimeOffset.UtcNow.AddDays(365)
                    });
                }
            }

            return RedirectToAction("Index", "Home");
        }

        ViewBag.ShowActivationForm = true;
        ViewBag.SeatEnforcement = _opt.LicenseId.HasValue;
        ViewBag.Message = result.Message;
        ViewBag.MachineFingerprintHex = await _activation
            .GetMachineFingerprintForDisplayAsync(cancellationToken)
            .ConfigureAwait(false);
        return View("Blocked");
    }
}
