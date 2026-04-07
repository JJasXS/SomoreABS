using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using YourApp.Models;

namespace YourApp.Filters
{
    /// <summary>
    /// Fixed site branding — no TENANT table or multi-tenant routing.
    /// </summary>
    public class TenantBrandingFilter : IActionFilter
    {
        public static TenantBrandingVm DefaultBranding { get; } = new TenantBrandingVm
        {
            TenantCode = "",
            TenantName = " SOMORE Hair Growth Head Spa Scalp Care ",
            HeaderLogoUrl = "~/images/somore_logo1.png",
            HeaderText1 = "Appointment Management System",
            HeaderText2 = "Make appointment bookings here!",
            FooterText1 = "Managed By ProAcc System Consulting."
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
