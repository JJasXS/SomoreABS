using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using YourApp.Models;
using YourApp.Services;

namespace YourApp.Filters
{
    /// <summary>
    /// Applies fixed site branding (header/footer). No multi-tenant DB or routing.
    /// </summary>
    public class TenantBrandingFilter : IActionFilter
    {
        public static TenantBrandingVm DefaultBranding { get; } = new TenantBrandingVm
        {
            TenantCode = "",
            TenantName = " SOMORE Hair Growth Head Spa Scalp Care ",
            HeaderText1 = "Appointment Management System",
            FooterText1 = "Thank you for using our system."
        };

        public void OnActionExecuting(ActionExecutingContext context)
        {
            if (context.Controller is Controller controller)
            {
                controller.ViewBag.TenantBranding = DefaultBranding;
            }
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }
    }
}
