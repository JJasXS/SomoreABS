using Microsoft.AspNetCore.Authorization;
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
    public IActionResult Blocked()
    {
        ViewBag.ShowActivationForm = _opt.Enabled;
        ViewBag.Message = _activation.LastFailureMessage
                          ?? "Activation not found or expired. Please activate your system.";
        return View();
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit([FromForm] string? activationCode, CancellationToken cancellationToken)
    {
        if (!_opt.Enabled)
            return RedirectToAction("Index", "Home");

        var result = await _activation.ValidateSubmittedCodeAsync(activationCode, cancellationToken).ConfigureAwait(false);
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
                ViewBag.Message =
                    "Activation succeeded but database setup failed. Check logs and the client Firebird path in TENANT_DB_PROFILE.";
                return View("Blocked");
            }

            return RedirectToAction("Index", "Home");
        }

        ViewBag.ShowActivationForm = true;
        ViewBag.Message = result.Message;
        return View("Blocked");
    }
}
