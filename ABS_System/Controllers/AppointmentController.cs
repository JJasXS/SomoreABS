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
SELECT APPT_ID, CUSTOMER_CODE, AGENT_CODE, APPT_START, APPT_END, TITLE, STATUS
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
                    ApptEnd = r.IsDBNull(4) ? null : r.GetDateTime(4),
                    Title = r.IsDBNull(5) ? null : r.GetString(5),
                    Status = r.IsDBNull(6) ? "NEW" : r.GetString(6)
                });
            }

            return View(list);
        }

        // Create GET
        public IActionResult Create()
        {
            return View(new Appointment
            {
                ApptStart = DateTime.Now,
                ApptEnd = DateTime.Now.AddMinutes(30)
            });
        }

        // Create POST
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(Appointment m)
        {
            if (!ModelState.IsValid)
                return View(m);

            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
INSERT INTO APPOINTMENT
(CUSTOMER_CODE, AGENT_CODE, APPT_START, APPT_END, TITLE, NOTES, STATUS, CREATED_DT, CREATED_BY)
VALUES
(@CUSTOMER_CODE, @AGENT_CODE, @APPT_START, @APPT_END, @TITLE, @NOTES, @STATUS, CURRENT_TIMESTAMP, @CREATED_BY)";

            cmd.Parameters.Add(FirebirdDb.P("@CUSTOMER_CODE", m.CustomerCode.Trim(), FbDbType.VarChar));
            cmd.Parameters.Add(FirebirdDb.P("@AGENT_CODE", m.AgentCode.Trim(), FbDbType.VarChar));
            cmd.Parameters.Add(FirebirdDb.P("@APPT_START", m.ApptStart, FbDbType.TimeStamp));
            cmd.Parameters.Add(FirebirdDb.P("@APPT_END", m.ApptEnd, FbDbType.TimeStamp));
            cmd.Parameters.Add(FirebirdDb.P("@TITLE", m.Title, FbDbType.VarChar));
            cmd.Parameters.Add(FirebirdDb.P("@NOTES", m.Notes, FbDbType.Text));
            cmd.Parameters.Add(FirebirdDb.P("@STATUS", string.IsNullOrWhiteSpace(m.Status) ? "NEW" : m.Status, FbDbType.VarChar));
            cmd.Parameters.Add(FirebirdDb.P("@CREATED_BY", User?.Identity?.Name ?? "SYSTEM", FbDbType.VarChar));

            cmd.ExecuteNonQuery();

            return RedirectToAction(nameof(Index));
        }
    }
}
