using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using YourApp.Services;

namespace YourApp.Controllers;

[AllowAnonymous]
public class ActivationController : Controller
{
    private readonly IActivationValidationService _activation;
    private readonly ActivationOptions _opt;

    public ActivationController(
        IActivationValidationService activation,
        IOptions<ActivationOptions> activationOptions)
    {
        _activation = activation;
        _opt = activationOptions.Value;
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
            return RedirectToAction("Index", "Home");

        ViewBag.ShowActivationForm = true;
        ViewBag.Message = result.Message;
        return View("Blocked");
    }
}
