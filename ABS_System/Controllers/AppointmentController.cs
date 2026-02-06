using Microsoft.AspNetCore.Mvc;
using YourApp.Data;

namespace YourApp.Controllers
{
    public partial class AppointmentController : Controller
    {
        private readonly FirebirdDb _db;
        private const int DEFAULT_DURATION_MINUTES = 60;

        public AppointmentController(FirebirdDb db)
        {
            _db = db;
        }
    }
}
