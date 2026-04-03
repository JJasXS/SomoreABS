using Microsoft.Extensions.Options;
using YourApp.Services;

namespace YourApp.Middleware;

/// <summary>Blocks the app when activation validation failed (except the blocked page and static paths).</summary>
public sealed class ActivationGateMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ActivationOptions _opt;

    public ActivationGateMiddleware(RequestDelegate next, IOptions<ActivationOptions> options)
    {
        _next = next;
        _opt = options.Value;
    }

    public async Task InvokeAsync(HttpContext context, IActivationValidationService activation)
    {
        if (!_opt.Enabled)
        {
            await _next(context);
            return;
        }

        // Revalidate every request (ActivationValidationService re-queries ACTIVATION.FDB; no stale cache).
        var result = await activation.ValidateAsync(context.RequestAborted).ConfigureAwait(false);
        if (result.Success)
        {
            await _next(context);
            return;
        }

        var path = context.Request.Path;
        if (path.StartsWithSegments("/Activation", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        // Let framework/static pipeline handle these prefixes (static files usually short-circuit earlier)
        if (path.StartsWithSegments("/css", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/js", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/lib", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/images", StringComparison.OrdinalIgnoreCase) ||
            path.StartsWithSegments("/favicon", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context);
            return;
        }

        context.Response.Redirect("/Activation/Blocked");
    }
}
