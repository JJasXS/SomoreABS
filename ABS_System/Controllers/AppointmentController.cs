using System;
using System.Collections.Generic;
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

        // =========================
        // LIST
        // =========================
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
                    CustomerCode = r.IsDBNull(1) ? "" : r.GetString(1).Trim(),
                    AgentCode = r.IsDBNull(2) ? "" : r.GetString(2).Trim(),
                    ApptStart = r.GetDateTime(3),
                    Title = r.IsDBNull(4) ? null : r.GetString(4),
                    Status = r.IsDBNull(5) ? "NEW" : r.GetString(5)
                });
            }

            return View(list);
        }

        // =========================
        // CREATE (GET)
        // =========================
        [HttpGet]
        public IActionResult Create(DateTime? apptStart)
        {
            var start = apptStart ?? DateTime.Now;

            LoadAgentsAndCustomers(out var agents, out var customers);
            ViewBag.Agents = agents;
            ViewBag.Customers = customers;

            return View(new Appointment
            {
                ApptStart = start,
                ApptEnd = start,
                Status = "NEW"
            });
        }

        // =========================
        // CREATE (POST)
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(Appointment m)
        {
            // always re-load dropdown data if returning View(m)
            LoadAgentsAndCustomers(out var agents, out var customers);
            ViewBag.Agents = agents;
            ViewBag.Customers = customers;

            if (!ModelState.IsValid)
                return View(m);

            // Validate end time after start time
            if (m.ApptEnd <= m.ApptStart)
            {
                ModelState.AddModelError("ApptEnd", "End time must be after start time.");
                return View(m);
            }

            // Validate no overlap for same agent
            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT COUNT(*) FROM APPOINTMENT
WHERE AGENT_CODE = @AGENT_CODE
AND ((@APPT_START < APPT_END) AND (@APPT_END > APPT_START))";
                cmd.Parameters.Add(FirebirdDb.P("@AGENT_CODE", (m.AgentCode ?? "").Trim(), FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@APPT_START", m.ApptStart, FbDbType.TimeStamp));
                cmd.Parameters.Add(FirebirdDb.P("@APPT_END", m.ApptEnd, FbDbType.TimeStamp));
                var overlapCount = Convert.ToInt32(cmd.ExecuteScalar());
                if (overlapCount > 0)
                {
                    ModelState.AddModelError("", "This agent already has a booking that overlaps with the selected time.");
                    return View(m);
                }
            }

            // Insert new appointment
            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO APPOINTMENT
(CUSTOMER_CODE, AGENT_CODE, APPT_START, APPT_END, TITLE, NOTES, STATUS, CREATED_DT, CREATED_BY)
VALUES
(@CUSTOMER_CODE, @AGENT_CODE, @APPT_START, @APPT_END, @TITLE, @NOTES, @STATUS, CURRENT_TIMESTAMP, @CREATED_BY)";

                cmd.Parameters.Add(FirebirdDb.P("@CUSTOMER_CODE", (m.CustomerCode ?? "").Trim(), FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@AGENT_CODE", (m.AgentCode ?? "").Trim(), FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@APPT_START", m.ApptStart, FbDbType.TimeStamp));
                cmd.Parameters.Add(FirebirdDb.P("@APPT_END", m.ApptEnd, FbDbType.TimeStamp));
                cmd.Parameters.Add(FirebirdDb.P("@TITLE", m.Title, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@NOTES", m.Notes, FbDbType.Text));
                cmd.Parameters.Add(FirebirdDb.P("@STATUS", string.IsNullOrWhiteSpace(m.Status) ? "NEW" : m.Status.Trim(), FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@CREATED_BY", User?.Identity?.Name ?? "SYSTEM", FbDbType.VarChar));

                cmd.ExecuteNonQuery();
            }

            return RedirectToAction("Index", "Calendar", new { year = m.ApptStart.Year, month = m.ApptStart.Month });
        }

        // =========================
        // EDIT (GET)
        // =========================
        [HttpGet]
        public IActionResult Edit(long id)
        {
            Appointment model = null;

            LoadAgentsAndCustomers(out var agents, out var customers);
            ViewBag.Agents = agents;
            ViewBag.Customers = customers;

            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT APPT_ID, CUSTOMER_CODE, AGENT_CODE, APPT_START, TITLE, STATUS, NOTES
FROM APPOINTMENT
WHERE APPT_ID = @id";

                cmd.Parameters.Add(FirebirdDb.P("@id", id, FbDbType.BigInt));

                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    model = new Appointment
                    {
                        ApptId = r.GetInt64(0),
                        CustomerCode = r.IsDBNull(1) ? "" : r.GetString(1).Trim(),
                        AgentCode = r.IsDBNull(2) ? "" : r.GetString(2).Trim(),
                        ApptStart = r.GetDateTime(3),
                        Title = r.IsDBNull(4) ? null : r.GetString(4),
                        Status = r.IsDBNull(5) ? "NEW" : r.GetString(5),
                        Notes = r.IsDBNull(6) ? null : r.GetString(6)
                    };
                }
            }

            if (model == null)
                return NotFound();

            return View(model);
        }

        // =========================
        // EDIT (POST)
        // =========================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(Appointment m)
        {
            LoadAgentsAndCustomers(out var agents, out var customers);
            ViewBag.Agents = agents;
            ViewBag.Customers = customers;

            if (!ModelState.IsValid)
                return View(m);

            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
UPDATE APPOINTMENT
SET
  CUSTOMER_CODE = @CUSTOMER_CODE,
  AGENT_CODE    = @AGENT_CODE,
  APPT_START    = @APPT_START,
  TITLE         = @TITLE,
  NOTES         = @NOTES,
  STATUS        = @STATUS
WHERE APPT_ID   = @APPT_ID";

                cmd.Parameters.Add(FirebirdDb.P("@CUSTOMER_CODE", (m.CustomerCode ?? "").Trim(), FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@AGENT_CODE", (m.AgentCode ?? "").Trim(), FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@APPT_START", m.ApptStart, FbDbType.TimeStamp));
                cmd.Parameters.Add(FirebirdDb.P("@TITLE", m.Title, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@NOTES", m.Notes, FbDbType.Text));
                cmd.Parameters.Add(FirebirdDb.P("@STATUS", string.IsNullOrWhiteSpace(m.Status) ? "NEW" : m.Status.Trim(), FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@APPT_ID", m.ApptId, FbDbType.BigInt));

                cmd.ExecuteNonQuery();
            }

            return RedirectToAction("Index", "Calendar", new { year = m.ApptStart.Year, month = m.ApptStart.Month });
        }

        // =========================
        // DELETE (GET) - confirm page
        // =========================
        [HttpGet]
        public IActionResult Delete(long id)
        {
            Appointment model = null;

            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT APPT_ID, CUSTOMER_CODE, AGENT_CODE, APPT_START, TITLE, STATUS
FROM APPOINTMENT
WHERE APPT_ID = @id";

                cmd.Parameters.Add(FirebirdDb.P("@id", id, FbDbType.BigInt));

                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    model = new Appointment
                    {
                        ApptId = r.GetInt64(0),
                        CustomerCode = r.IsDBNull(1) ? "" : r.GetString(1).Trim(),
                        AgentCode = r.IsDBNull(2) ? "" : r.GetString(2).Trim(),
                        ApptStart = r.GetDateTime(3),
                        Title = r.IsDBNull(4) ? null : r.GetString(4),
                        Status = r.IsDBNull(5) ? "NEW" : r.GetString(5)
                    };
                }
            }

            if (model == null)
                return NotFound();

            return View(model);
        }

        // =========================
        // DELETE (POST) - confirmed
        // =========================
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteConfirmed(long id)
        {
            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "DELETE FROM APPOINTMENT WHERE APPT_ID = @id";
                cmd.Parameters.Add(FirebirdDb.P("@id", id, FbDbType.BigInt));
                cmd.ExecuteNonQuery();
            }

            return RedirectToAction("Index", "Calendar");
        }

        // =========================
        // HELPERS
        // =========================
        private void LoadAgentsAndCustomers(out List<dynamic> agents, out List<dynamic> customers)
        {
            agents = new List<dynamic>();
            customers = new List<dynamic>();

            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();

            // Agents
            cmd.CommandText = "SELECT CODE, DESCRIPTION FROM AGENT ORDER BY DESCRIPTION";
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    agents.Add(new
                    {
                        Code = r.IsDBNull(0) ? "" : r.GetString(0).Trim(),
                        Description = r.IsDBNull(1) ? "" : r.GetString(1).Trim()
                    });
                }
            }

            // Customers
            cmd.CommandText = "SELECT CODE, COMPANYNAME FROM AR_CUSTOMER ORDER BY COMPANYNAME";
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    customers.Add(new
                    {
                        Code = r.IsDBNull(0) ? "" : r.GetString(0).Trim(),
                        Name = r.IsDBNull(1) ? "" : r.GetString(1).Trim()
                    });
                }
            }
        }
    }
}
