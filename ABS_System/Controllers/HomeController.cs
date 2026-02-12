using Microsoft.AspNetCore.Mvc;
using YourApp.Models;

namespace YourApp.Controllers
{
    public class HomeController : Controller
    {
        public IActionResult Index()
        {
            // You can add logic here to load branding or other info if needed
            return View();
        }

        public IActionResult Error()
        {
            return View();
        }
    }
}
