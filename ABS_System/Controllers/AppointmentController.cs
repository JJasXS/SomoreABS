using Microsoft.AspNetCore.Mvc;
using FirebirdSql.Data.FirebirdClient;
using YourApp.Data;
using YourApp.Models;

namespace YourApp.Controllers
{
    public class AppointmentController : Controller
    {
        private readonly FirebirdDb _db;

        public AppointmentController(FirebirdDb db)
        {
            _db = db;
        }

        // List
        public IActionResult Index()
        {
            var list = new List<Appointment>();

            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT APPT_ID, CUSTOMER_CODE, AGENT_CODE, APPT_START, TITLE, STATUS
FROM APPOINTMENT
ORDER BY APPT_START DESC";

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new Appointment
                {
                    ApptId = r.GetInt64(0),
                    CustomerCode = r.GetString(1).Trim(),
                    AgentCode = r.GetString(2).Trim(),
                    ApptStart = r.GetDateTime(3),
                    Title = r.IsDBNull(4) ? null : r.GetString(4),
                    Status = r.IsDBNull(5) ? "NEW" : r.GetString(5)
                });
            }

            return View(list);
        }

        // Create GET
        public IActionResult Create(DateTime? apptStart)
        {
            var start = apptStart ?? DateTime.Now;
            // Fetch agents for dropdown
            var agents = new List<dynamic>();
            var customers = new List<dynamic>();
            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                // Load agents
                cmd.CommandText = "SELECT CODE, DESCRIPTION FROM AGENT ORDER BY DESCRIPTION";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        agents.Add(new { Code = r.GetString(0).Trim(), Description = r.GetString(1).Trim() });
                    }
                }
                // Load customers
                cmd.CommandText = "SELECT CODE, COMPANYNAME FROM AR_CUSTOMER ORDER BY COMPANYNAME";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        customers.Add(new { Code = r.GetString(0).Trim(), Name = r.GetString(1).Trim() });
                    }
                }
            }
            ViewBag.Agents = agents;
            ViewBag.Customers = customers;
            return View(new Appointment
            {
                ApptStart = start
            });
        }

        // Create POST
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(Appointment m)
        {
            // Repopulate agent and customer lists for redisplay on error
            var agents = new List<dynamic>();
            var customers = new List<dynamic>();
            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                // Load agents
                cmd.CommandText = "SELECT CODE, DESCRIPTION FROM AGENT ORDER BY DESCRIPTION";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        agents.Add(new { Code = r.GetString(0).Trim(), Description = r.GetString(1).Trim() });
                    }
                }
                // Load customers
                cmd.CommandText = "SELECT CODE, COMPANYNAME FROM AR_CUSTOMER ORDER BY COMPANYNAME ASC";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        customers.Add(new { Code = r.GetString(0).Trim(), Name = r.GetString(1).Trim() });
                    }
                }
            }
            ViewBag.Agents = agents;
            ViewBag.Customers = customers;

            if (!ModelState.IsValid)
                return View(m);

            using var conn2 = _db.Open();
            using var cmd2 = conn2.CreateCommand();

            cmd2.CommandText = @"
INSERT INTO APPOINTMENT
(CUSTOMER_CODE, AGENT_CODE, APPT_START, TITLE, NOTES, STATUS, CREATED_DT, CREATED_BY)
VALUES
(@CUSTOMER_CODE, @AGENT_CODE, @APPT_START, @TITLE, @NOTES, @STATUS, CURRENT_TIMESTAMP, @CREATED_BY)";

            cmd2.Parameters.Add(FirebirdDb.P("@CUSTOMER_CODE", m.CustomerCode.Trim(), FbDbType.VarChar));
            cmd2.Parameters.Add(FirebirdDb.P("@AGENT_CODE", m.AgentCode.Trim(), FbDbType.VarChar));
            cmd2.Parameters.Add(FirebirdDb.P("@APPT_START", m.ApptStart, FbDbType.TimeStamp));
            cmd2.Parameters.Add(FirebirdDb.P("@TITLE", m.Title, FbDbType.VarChar));
            cmd2.Parameters.Add(FirebirdDb.P("@NOTES", m.Notes, FbDbType.Text));
            cmd2.Parameters.Add(FirebirdDb.P("@STATUS", string.IsNullOrWhiteSpace(m.Status) ? "NEW" : m.Status, FbDbType.VarChar));
            cmd2.Parameters.Add(FirebirdDb.P("@CREATED_BY", User?.Identity?.Name ?? "SYSTEM", FbDbType.VarChar));

            cmd2.ExecuteNonQuery();

            // After creation, redirect to Calendar/Index for the month/year of the appointment
            return RedirectToAction("Index", "Calendar", new { year = m.ApptStart.Year, month = m.ApptStart.Month });
        }
    }
}
