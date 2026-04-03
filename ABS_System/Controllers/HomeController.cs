using Microsoft.AspNetCore.Mvc;
using YourApp.Models;

namespace YourApp.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            // Pass TenantBrandingVm from ViewBag to the view model
            var branding = ViewBag.TenantBranding as YourApp.Models.TenantBrandingVm;
            return View(branding);
        }

        public IActionResult Error()
        {
            return View();
        }
    }
}
