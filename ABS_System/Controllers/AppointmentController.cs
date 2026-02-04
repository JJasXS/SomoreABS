using System;
using System.Collections.Generic;
using System.Linq;
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

        // =========================================================
        // LIST
        // =========================================================
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
                    CustomerCode = r.IsDBNull(1) ? "" : r.GetString(1).Trim(),
                    AgentCode = r.IsDBNull(2) ? "" : r.GetString(2).Trim(),
                    ApptStart = r.GetDateTime(3),
                    ApptEnd = r.IsDBNull(4) ? r.GetDateTime(3) : r.GetDateTime(4),
                    Title = r.IsDBNull(5) ? null : r.GetString(5).Trim(),
                    Status = r.IsDBNull(6) ? "NEW" : r.GetString(6).Trim()
                });
            }

            return View(list);
        }

        // =========================================================
        // CREATE (GET)
        // =========================================================
        [HttpGet]
        public IActionResult Create(DateTime? apptStart)
        {
            var start = apptStart ?? DateTime.Now;
            var end = start.AddHours(1);

            LoadAgentsAndCustomers(out var agents, out var customers);
            ViewBag.Agents = agents;
            ViewBag.Customers = customers;

            ViewBag.ServiceItems = LoadServiceItems();
            ViewBag.SelectedServiceCodes = new List<string>();

            return View(new Appointment
            {
                ApptStart = start,
                ApptEnd = end,
                Status = "NEW"
            });
        }

        // =========================================================
        // CREATE (POST)
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(Appointment m)
        {
            LoadAgentsAndCustomers(out var agents, out var customers);
            ViewBag.Agents = agents;
            ViewBag.Customers = customers;
            ViewBag.ServiceItems = LoadServiceItems();

            var selectedServiceCodes = ParseCsv(Request.Form["ServiceCodes"].ToString());
            ViewBag.SelectedServiceCodes = selectedServiceCodes;

            // ✅ Title rule: FIRST or FIRST...
            m.Title = BuildTitleFromServiceCodes(selectedServiceCodes);

            if (!ModelState.IsValid)
                return View(m);

            if (m.ApptEnd <= m.ApptStart)
            {
                ModelState.AddModelError("ApptEnd", "End time must be after start time.");
                return View(m);
            }

            if (HasOverlap(m.AgentCode, m.ApptStart, m.ApptEnd, excludeApptId: null))
            {
                ModelState.AddModelError("", "This agent already has a booking that overlaps with the selected time.");
                return View(m);
            }

            long newApptId;

            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO APPOINTMENT
(CUSTOMER_CODE, AGENT_CODE, APPT_START, APPT_END, TITLE, NOTES, STATUS, CREATED_DT, CREATED_BY)
VALUES
(@CUSTOMER_CODE, @AGENT_CODE, @APPT_START, @APPT_END, @TITLE, @NOTES, @STATUS, CURRENT_TIMESTAMP, @CREATED_BY)
RETURNING APPT_ID";

                cmd.Parameters.Add(FirebirdDb.P("@CUSTOMER_CODE", (m.CustomerCode ?? "").Trim(), FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@AGENT_CODE", (m.AgentCode ?? "").Trim(), FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@APPT_START", m.ApptStart, FbDbType.TimeStamp));
                cmd.Parameters.Add(FirebirdDb.P("@APPT_END", m.ApptEnd, FbDbType.TimeStamp));
                cmd.Parameters.Add(FirebirdDb.P("@TITLE", m.Title, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@NOTES", m.Notes, FbDbType.Text));
                cmd.Parameters.Add(FirebirdDb.P("@STATUS", string.IsNullOrWhiteSpace(m.Status) ? "NEW" : m.Status.Trim(), FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@CREATED_BY", User?.Identity?.Name ?? "SYSTEM", FbDbType.VarChar));

                newApptId = Convert.ToInt64(cmd.ExecuteScalar());
            }

            // Insert services into APPT_DTL
            if (selectedServiceCodes.Count > 0)
            {
                using var conn = _db.Open();
                foreach (var code in selectedServiceCodes)
                {
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"INSERT INTO APPT_DTL (APPT_ID, SERVICE_CODE) VALUES (@APPT_ID, @SERVICE_CODE)";
                    cmd.Parameters.Add(FirebirdDb.P("@APPT_ID", newApptId, FbDbType.BigInt));
                    cmd.Parameters.Add(FirebirdDb.P("@SERVICE_CODE", code.Trim(), FbDbType.VarChar));
                    cmd.ExecuteNonQuery();
                }
            }

            return RedirectToAction("Index", "Calendar", new { year = m.ApptStart.Year, month = m.ApptStart.Month });
        }

        // =========================================================
        // EDIT (GET)
        // =========================================================
        [HttpGet]
        public IActionResult Edit(long id)
        {
            LoadAgentsAndCustomers(out var agents, out var customers);
            ViewBag.Agents = agents;
            ViewBag.Customers = customers;

            ViewBag.ServiceItems = LoadServiceItems();

            Appointment model = null;

            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT APPT_ID, CUSTOMER_CODE, AGENT_CODE, APPT_START, APPT_END, TITLE, STATUS, NOTES
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
                        ApptEnd = r.IsDBNull(4) ? r.GetDateTime(3).AddHours(1) : r.GetDateTime(4),
                        Title = r.IsDBNull(5) ? "" : r.GetString(5).Trim(),
                        Status = r.IsDBNull(6) ? "NEW" : r.GetString(6).Trim(),
                        Notes = r.IsDBNull(7) ? null : r.GetString(7)
                    };
                }
            }

            if (model == null)
                return NotFound();

            // ✅ Load selected service codes for UI
            var selectedCodes = LoadSelectedServiceCodes(id);
            ViewBag.SelectedServiceCodes = selectedCodes;

            return View(model);
        }

        // =========================================================
        // EDIT (POST)
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(Appointment m)
        {
            LoadAgentsAndCustomers(out var agents, out var customers);
            ViewBag.Agents = agents;
            ViewBag.Customers = customers;
            ViewBag.ServiceItems = LoadServiceItems();

            var selectedServiceCodes = ParseCsv(Request.Form["ServiceCodes"].ToString());
            ViewBag.SelectedServiceCodes = selectedServiceCodes;

            // ✅ Title rule: FIRST or FIRST...
            m.Title = BuildTitleFromServiceCodes(selectedServiceCodes);

            if (!ModelState.IsValid)
                return View(m);

            if (m.ApptEnd <= m.ApptStart)
            {
                ModelState.AddModelError("ApptEnd", "End time must be after start time.");
                return View(m);
            }

            if (HasOverlap(m.AgentCode, m.ApptStart, m.ApptEnd, excludeApptId: m.ApptId))
            {
                ModelState.AddModelError("", "This agent already has a booking that overlaps with the selected time.");
                return View(m);
            }

            using (var conn = _db.Open())
            {
                // 1) Update APPOINTMENT (includes TITLE)
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = @"
UPDATE APPOINTMENT
SET
  CUSTOMER_CODE = @CUSTOMER_CODE,
  AGENT_CODE    = @AGENT_CODE,
  APPT_START    = @APPT_START,
  APPT_END      = @APPT_END,
  TITLE         = @TITLE,
  NOTES         = @NOTES,
  STATUS        = @STATUS
WHERE APPT_ID   = @APPT_ID";

                    cmd.Parameters.Add(FirebirdDb.P("@CUSTOMER_CODE", (m.CustomerCode ?? "").Trim(), FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@AGENT_CODE", (m.AgentCode ?? "").Trim(), FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@APPT_START", m.ApptStart, FbDbType.TimeStamp));
                    cmd.Parameters.Add(FirebirdDb.P("@APPT_END", m.ApptEnd, FbDbType.TimeStamp));
                    cmd.Parameters.Add(FirebirdDb.P("@TITLE", m.Title, FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@NOTES", m.Notes, FbDbType.Text));
                    cmd.Parameters.Add(FirebirdDb.P("@STATUS", string.IsNullOrWhiteSpace(m.Status) ? "NEW" : m.Status.Trim(), FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@APPT_ID", m.ApptId, FbDbType.BigInt));

                    cmd.ExecuteNonQuery();
                }

                // 2) Replace APPT_DTL rows
                using (var cmdDel = conn.CreateCommand())
                {
                    cmdDel.CommandText = "DELETE FROM APPT_DTL WHERE APPT_ID = @id";
                    cmdDel.Parameters.Add(FirebirdDb.P("@id", m.ApptId, FbDbType.BigInt));
                    cmdDel.ExecuteNonQuery();
                }

                if (selectedServiceCodes.Count > 0)
                {
                    foreach (var code in selectedServiceCodes)
                    {
                        using var cmdIns = conn.CreateCommand();
                        cmdIns.CommandText = @"INSERT INTO APPT_DTL (APPT_ID, SERVICE_CODE) VALUES (@APPT_ID, @SERVICE_CODE)";
                        cmdIns.Parameters.Add(FirebirdDb.P("@APPT_ID", m.ApptId, FbDbType.BigInt));
                        cmdIns.Parameters.Add(FirebirdDb.P("@SERVICE_CODE", code.Trim(), FbDbType.VarChar));
                        cmdIns.ExecuteNonQuery();
                    }
                }
            }

            return RedirectToAction("Index", "Calendar", new { year = m.ApptStart.Year, month = m.ApptStart.Month });
        }

        // =========================================================
        // DELETE (GET)
        // =========================================================
        [HttpGet]
        public IActionResult Delete(long id)
        {
            Appointment model = null;

            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
SELECT APPT_ID, CUSTOMER_CODE, AGENT_CODE, APPT_START, APPT_END, TITLE, STATUS
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
                        ApptEnd = r.IsDBNull(4) ? r.GetDateTime(3).AddHours(1) : r.GetDateTime(4),
                        Title = r.IsDBNull(5) ? null : r.GetString(5).Trim(),
                        Status = r.IsDBNull(6) ? "NEW" : r.GetString(6).Trim()
                    };
                }
            }

            if (model == null)
                return NotFound();

            return View(model);
        }

        // =========================================================
        // DELETE (POST)
        // =========================================================
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteConfirmed(long id)
        {
            using (var conn = _db.Open())
            {
                using (var cmdDtl = conn.CreateCommand())
                {
                    cmdDtl.CommandText = "DELETE FROM APPT_DTL WHERE APPT_ID = @id";
                    cmdDtl.Parameters.Add(FirebirdDb.P("@id", id, FbDbType.BigInt));
                    cmdDtl.ExecuteNonQuery();
                }

                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM APPOINTMENT WHERE APPT_ID = @id";
                    cmd.Parameters.Add(FirebirdDb.P("@id", id, FbDbType.BigInt));
                    cmd.ExecuteNonQuery();
                }
            }

            return RedirectToAction("Index", "Calendar");
        }

        // =========================================================
        // HELPERS
        // =========================================================
        private string BuildTitleFromServiceCodes(List<string> codes)
        {
            if (codes == null || codes.Count == 0) return null;

            var first = (codes[0] ?? "").Trim();
            if (string.IsNullOrWhiteSpace(first)) return null;

            return (codes.Count > 1) ? (first + "...") : first;
        }

        private List<string> LoadSelectedServiceCodes(long apptId)
        {
            var list = new List<string>();

            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT SERVICE_CODE FROM APPT_DTL WHERE APPT_ID = @id ORDER BY APPT_DTL_ID";
                cmd.Parameters.Add(FirebirdDb.P("@id", apptId, FbDbType.BigInt));

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var code = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                    if (!string.IsNullOrWhiteSpace(code))
                        list.Add(code);
                }
            }
            catch { }

            // de-dup but keep order
            var uniq = new List<string>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in list)
            {
                if (seen.Add(c)) uniq.Add(c);
            }

            return uniq;
        }

        private void LoadAgentsAndCustomers(out List<dynamic> agents, out List<dynamic> customers)
        {
            agents = new List<dynamic>();
            customers = new List<dynamic>();

            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();

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

        private List<YourApp.Models.ST_ITEM> LoadServiceItems()
        {
            var serviceItems = new List<YourApp.Models.ST_ITEM>();

            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT CODE, DESCRIPTION FROM ST_ITEM WHERE STOCKGROUP = 'SERVICE' ORDER BY DESCRIPTION";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    serviceItems.Add(new YourApp.Models.ST_ITEM
                    {
                        CODE = r.IsDBNull(0) ? "" : r.GetString(0).Trim(),
                        DESCRIPTION = r.IsDBNull(1) ? "" : r.GetString(1).Trim(),
                        STOCKGROUP = "SERVICE"
                    });
                }
            }
            catch { }

            return serviceItems;
        }

        private bool HasOverlap(string agentCode, DateTime start, DateTime end, long? excludeApptId)
        {
            var agent = (agentCode ?? "").Trim();
            if (string.IsNullOrEmpty(agent))
                return false;

            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
SELECT COUNT(*)
FROM APPOINTMENT
WHERE AGENT_CODE = @AGENT_CODE
  AND ((@APPT_START < APPT_END) AND (@APPT_END > APPT_START))
  AND (@EXCLUDE_ID IS NULL OR APPT_ID <> @EXCLUDE_ID)";

            cmd.Parameters.Add(FirebirdDb.P("@AGENT_CODE", agent, FbDbType.VarChar));
            cmd.Parameters.Add(FirebirdDb.P("@APPT_START", start, FbDbType.TimeStamp));
            cmd.Parameters.Add(FirebirdDb.P("@APPT_END", end, FbDbType.TimeStamp));
            cmd.Parameters.Add(FirebirdDb.P("@EXCLUDE_ID", excludeApptId, FbDbType.BigInt));

            var count = Convert.ToInt32(cmd.ExecuteScalar());
            return count > 0;
        }

        private List<string> ParseCsv(string csv)
        {
            return (csv ?? "")
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
