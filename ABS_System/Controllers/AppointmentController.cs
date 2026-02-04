using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using FirebirdSql.Data.FirebirdClient;
using YourApp.Data;
using YourApp.Models;

namespace YourApp.Controllers
{
    public class AppointmentController : Controller
    {
        private readonly FirebirdDb _db;
        private const int DEFAULT_DURATION_MINUTES = 60;

        public AppointmentController(FirebirdDb db)
        {
            _db = db;
        }

        // =========================================================
        // QUICK STATUS UPDATE (AJAX)
        // POST: /Appointment/SetStatus
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SetStatus(long id, string status)
        {
            status = (status ?? "").Trim().ToUpperInvariant();

            var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "BOOKED",
                "FULFILLED"
            };

            if (!allowed.Contains(status))
                return Json(new { ok = false, message = "Invalid status." });

            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = @"
UPDATE APPOINTMENT
SET STATUS = @STATUS,
    LAST_UPD_DT = CURRENT_TIMESTAMP,
    LAST_UPD_BY = @BY
WHERE APPT_ID = @ID";

                cmd.Parameters.Add(FirebirdDb.P("@STATUS", status, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@BY", User?.Identity?.Name ?? "SYSTEM", FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@ID", id, FbDbType.BigInt));

                var rows = cmd.ExecuteNonQuery();
                if (rows <= 0)
                    return Json(new { ok = false, message = "Appointment not found." });

                return Json(new { ok = true, status = status });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, message = "Failed to update status.", detail = ex.Message });
            }
        }

        // =========================================================
        // SIGNATURE (GET) - show signature pad
        // GET: /Appointment/Sign/5
        // =========================================================
        [HttpGet]
        public IActionResult Sign(long id)
        {
            // (Optional) you can load appt info to show on the page
            ViewBag.ApptId = id;
            ViewBag.HasSignature = HasSignature(id);
            return View();
        }

        // =========================================================
        // SIGNATURE SAVE (POST) - Option B: UPSERT
        // POST: /Appointment/SignSave
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SignSave(long apptId, string? signedBy, string? remarks, string signatureDataUrl)
        {
            if (apptId <= 0)
            {
                TempData["Err"] = "Invalid appointment.";
                return RedirectToAction("Sign", new { id = apptId });
            }

            signedBy = (signedBy ?? "").Trim();
            remarks = (remarks ?? "").Trim();

            if (string.IsNullOrWhiteSpace(signatureDataUrl))
            {
                TempData["Err"] = "Signature is required.";
                return RedirectToAction("Sign", new { id = apptId });
            }

            // Expect: data:image/png;base64,XXXX
            var m = Regex.Match(signatureDataUrl, @"^data:image\/png;base64,(.+)$");
            if (!m.Success)
            {
                TempData["Err"] = "Signature format invalid. Must be PNG.";
                return RedirectToAction("Sign", new { id = apptId });
            }

            byte[] pngBytes;
            try
            {
                pngBytes = Convert.FromBase64String(m.Groups[1].Value);
            }
            catch
            {
                TempData["Err"] = "Invalid signature data.";
                return RedirectToAction("Sign", new { id = apptId });
            }

            try
            {
                using var conn = _db.Open();

                // 1) Check if signature row exists (1-1 enforced by UX_APPT_SIGNATURE_APPT)
                bool exists;
                using (var cmdChk = conn.CreateCommand())
                {
                    cmdChk.CommandText = "SELECT COUNT(*) FROM APPT_SIGNATURE WHERE APPT_ID = @ID";
                    cmdChk.Parameters.Add(FirebirdDb.P("@ID", apptId, FbDbType.BigInt));
                    exists = Convert.ToInt32(cmdChk.ExecuteScalar()) > 0;
                }

                // 2) UPSERT: UPDATE if exists else INSERT
                if (exists)
                {
                    using var cmdUp = conn.CreateCommand();
                    cmdUp.CommandText = @"
UPDATE APPT_SIGNATURE
SET SIGNED_DT = CURRENT_TIMESTAMP,
    SIGNED_BY = @BY,
    REMARKS = @REM,
    SIGNATURE_PNG = @PNG
WHERE APPT_ID = @ID";

                    cmdUp.Parameters.Add(FirebirdDb.P("@BY", string.IsNullOrWhiteSpace(signedBy) ? null : signedBy, FbDbType.VarChar));
                    cmdUp.Parameters.Add(FirebirdDb.P("@REM", string.IsNullOrWhiteSpace(remarks) ? null : remarks, FbDbType.VarChar));

                    var pPng = new FbParameter("@PNG", FbDbType.Binary) { Value = pngBytes };
                    cmdUp.Parameters.Add(pPng);

                    cmdUp.Parameters.Add(FirebirdDb.P("@ID", apptId, FbDbType.BigInt));

                    cmdUp.ExecuteNonQuery();
                }
                else
                {
                    using var cmdIns = conn.CreateCommand();
                    cmdIns.CommandText = @"
INSERT INTO APPT_SIGNATURE (APPT_ID, SIGNED_DT, SIGNED_BY, REMARKS, SIGNATURE_PNG)
VALUES (@ID, CURRENT_TIMESTAMP, @BY, @REM, @PNG)";

                    cmdIns.Parameters.Add(FirebirdDb.P("@ID", apptId, FbDbType.BigInt));
                    cmdIns.Parameters.Add(FirebirdDb.P("@BY", string.IsNullOrWhiteSpace(signedBy) ? null : signedBy, FbDbType.VarChar));
                    cmdIns.Parameters.Add(FirebirdDb.P("@REM", string.IsNullOrWhiteSpace(remarks) ? null : remarks, FbDbType.VarChar));

                    var pPng = new FbParameter("@PNG", FbDbType.Binary) { Value = pngBytes };
                    cmdIns.Parameters.Add(pPng);

                    cmdIns.ExecuteNonQuery();
                }

                // 3) Mark appointment as fulfilled (proof done)
                using (var cmdAppt = conn.CreateCommand())
                {
                    cmdAppt.CommandText = @"
UPDATE APPOINTMENT
SET STATUS = 'FULFILLED',
    LAST_UPD_DT = CURRENT_TIMESTAMP,
    LAST_UPD_BY = @BY
WHERE APPT_ID = @ID";

                    cmdAppt.Parameters.Add(FirebirdDb.P("@BY", User?.Identity?.Name ?? "SYSTEM", FbDbType.VarChar));
                    cmdAppt.Parameters.Add(FirebirdDb.P("@ID", apptId, FbDbType.BigInt));
                    cmdAppt.ExecuteNonQuery();
                }

                TempData["Ok"] = "Signature saved. Appointment marked as FULFILLED.";
                return RedirectToAction("Edit", new { id = apptId });
            }
            catch (Exception ex)
            {
                TempData["Err"] = "Failed to save signature: " + ex.Message;
                return RedirectToAction("Sign", new { id = apptId });
            }
        }

        // =========================================================
        // SIGNATURE IMAGE (GET) - show png in <img src="">
        // GET: /Appointment/SignatureImage/5
        // =========================================================
        [HttpGet]
        public IActionResult SignatureImage(long id)
        {
            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = @"
SELECT SIGNATURE_PNG
FROM APPT_SIGNATURE
WHERE APPT_ID = @ID";

                cmd.Parameters.Add(FirebirdDb.P("@ID", id, FbDbType.BigInt));

                using var r = cmd.ExecuteReader();
                if (!r.Read() || r.IsDBNull(0))
                    return NotFound();

                // Read BLOB safely (works even if provider doesn't return byte[])
                byte[] bytes;
                using (var ms = new System.IO.MemoryStream())
                {
                    long read = 0;
                    long offset = 0;
                    var buffer = new byte[8192];

                    while ((read = r.GetBytes(0, offset, buffer, 0, buffer.Length)) > 0)
                    {
                        ms.Write(buffer, 0, (int)read);
                        offset += read;
                    }
                    bytes = ms.ToArray();
                }

                return File(bytes, "image/png");
            }
            catch
            {
                return NotFound();
            }
        }

        private bool HasSignature(long apptId)
        {
            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM APPT_SIGNATURE WHERE APPT_ID = @ID";
                cmd.Parameters.Add(FirebirdDb.P("@ID", apptId, FbDbType.BigInt));
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            catch
            {
                return false;
            }
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
                    Title = r.IsDBNull(4) ? null : r.GetString(4).Trim(),
                    Status = r.IsDBNull(5) ? "BOOKED" : r.GetString(5).Trim()
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

            LoadAgentsAndCustomers(out var agents, out var customers);
            ViewBag.Agents = agents;
            ViewBag.Customers = customers;
            ViewBag.ServiceItems = LoadServiceItems();

            return View(new Appointment
            {
                ApptStart = start,
                Status = "BOOKED"
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

            m.CustomerCode = (m.CustomerCode ?? "").Trim();
            m.AgentCode = (m.AgentCode ?? "").Trim();
            m.Title = (m.Title ?? "").Trim();

            // force default on create
            m.Status = "BOOKED";

            var selectedServiceCodes = ParseCsv(Request.Form["ServiceCodes"].ToString());

            if (!ModelState.IsValid)
                return View(m);

            if (selectedServiceCodes.Count == 0)
            {
                ModelState.AddModelError("", "Please select at least one service.");
                return View(m);
            }

            var start = m.ApptStart;
            var end = m.ApptStart.AddMinutes(DEFAULT_DURATION_MINUTES);

            if (HasAgentOverlap(m.AgentCode, start, end, excludeApptId: null))
            {
                ModelState.AddModelError("", "Not allowed: this agent already has a booking that overlaps this time.");
                return View(m);
            }

            if (HasCustomerOverlap(m.CustomerCode, start, end, excludeApptId: null))
            {
                ModelState.AddModelError("", "Not allowed: this customer already has a booking that overlaps this time (even with a different agent).");
                return View(m);
            }

            long newApptId;
            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"
INSERT INTO APPOINTMENT
(CUSTOMER_CODE, AGENT_CODE, APPT_START, TITLE, NOTES, STATUS, CREATED_DT, CREATED_BY)
VALUES
(@CUSTOMER_CODE, @AGENT_CODE, @APPT_START, @TITLE, @NOTES, @STATUS, CURRENT_TIMESTAMP, @CREATED_BY)
RETURNING APPT_ID";

                cmd.Parameters.Add(FirebirdDb.P("@CUSTOMER_CODE", m.CustomerCode, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@AGENT_CODE", m.AgentCode, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@APPT_START", m.ApptStart, FbDbType.TimeStamp));
                cmd.Parameters.Add(FirebirdDb.P("@TITLE", string.IsNullOrWhiteSpace(m.Title) ? null : m.Title, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@NOTES", m.Notes, FbDbType.Text));
                cmd.Parameters.Add(FirebirdDb.P("@STATUS", m.Status, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@CREATED_BY", User?.Identity?.Name ?? "SYSTEM", FbDbType.VarChar));

                newApptId = Convert.ToInt64(cmd.ExecuteScalar());
            }

            using (var conn = _db.Open())
            {
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
                        Title = r.IsDBNull(4) ? "" : r.GetString(4).Trim(),
                        Status = r.IsDBNull(5) ? "BOOKED" : r.GetString(5).Trim(),
                        Notes = r.IsDBNull(6) ? null : r.GetString(6)
                    };
                }
            }

            if (model == null)
                return NotFound();

            ViewBag.SelectedServiceCodes = LoadSelectedServiceCodes(id);

            // show signature section in edit page
            ViewBag.HasSignature = HasSignature(id);
            ViewBag.SignatureUrl = Url.Action("SignatureImage", "Appointment", new { id });

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

            m.CustomerCode = (m.CustomerCode ?? "").Trim();
            m.AgentCode = (m.AgentCode ?? "").Trim();
            m.Title = (m.Title ?? "").Trim();

            // allow changing status via edit page (or keep booked)
            m.Status = string.IsNullOrWhiteSpace(m.Status) ? "BOOKED" : m.Status.Trim();

            if (!ModelState.IsValid)
                return View(m);

            if (selectedServiceCodes.Count == 0)
            {
                ModelState.AddModelError("", "Please select at least one service.");
                return View(m);
            }

            var start = m.ApptStart;
            var end = m.ApptStart.AddMinutes(DEFAULT_DURATION_MINUTES);

            if (HasAgentOverlap(m.AgentCode, start, end, excludeApptId: m.ApptId))
            {
                ModelState.AddModelError("", "Not allowed: this agent already has a booking that overlaps this time.");
                return View(m);
            }

            if (HasCustomerOverlap(m.CustomerCode, start, end, excludeApptId: m.ApptId))
            {
                ModelState.AddModelError("", "Not allowed: this customer already has a booking that overlaps this time (even with a different agent).");
                return View(m);
            }

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
  STATUS        = @STATUS,
  LAST_UPD_DT   = CURRENT_TIMESTAMP,
  LAST_UPD_BY   = @BY
WHERE APPT_ID   = @APPT_ID";

                cmd.Parameters.Add(FirebirdDb.P("@CUSTOMER_CODE", m.CustomerCode, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@AGENT_CODE", m.AgentCode, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@APPT_START", m.ApptStart, FbDbType.TimeStamp));
                cmd.Parameters.Add(FirebirdDb.P("@TITLE", string.IsNullOrWhiteSpace(m.Title) ? null : m.Title, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@NOTES", m.Notes, FbDbType.Text));
                cmd.Parameters.Add(FirebirdDb.P("@STATUS", m.Status, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@BY", User?.Identity?.Name ?? "SYSTEM", FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@APPT_ID", m.ApptId, FbDbType.BigInt));

                cmd.ExecuteNonQuery();
            }

            using (var conn = _db.Open())
            {
                using (var cmdDel = conn.CreateCommand())
                {
                    cmdDel.CommandText = "DELETE FROM APPT_DTL WHERE APPT_ID = @id";
                    cmdDel.Parameters.Add(FirebirdDb.P("@id", m.ApptId, FbDbType.BigInt));
                    cmdDel.ExecuteNonQuery();
                }

                foreach (var code in selectedServiceCodes)
                {
                    using var cmdIns = conn.CreateCommand();
                    cmdIns.CommandText = "INSERT INTO APPT_DTL (APPT_ID, SERVICE_CODE) VALUES (@APPT_ID, @SERVICE_CODE)";
                    cmdIns.Parameters.Add(FirebirdDb.P("@APPT_ID", m.ApptId, FbDbType.BigInt));
                    cmdIns.Parameters.Add(FirebirdDb.P("@SERVICE_CODE", code.Trim(), FbDbType.VarChar));
                    cmdIns.ExecuteNonQuery();
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
                        Title = r.IsDBNull(4) ? null : r.GetString(4).Trim(),
                        Status = r.IsDBNull(5) ? "BOOKED" : r.GetString(5).Trim()
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
                // delete signature first (child table)
                using (var cmdSig = conn.CreateCommand())
                {
                    cmdSig.CommandText = "DELETE FROM APPT_SIGNATURE WHERE APPT_ID = @id";
                    cmdSig.Parameters.Add(FirebirdDb.P("@id", id, FbDbType.BigInt));
                    cmdSig.ExecuteNonQuery();
                }

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
            catch
            {
                // silent
            }

            return serviceItems;
        }

        private List<string> LoadSelectedServiceCodes(long apptId)
        {
            var list = new List<string>();

            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT SERVICE_CODE FROM APPT_DTL WHERE APPT_ID = @id ORDER BY SERVICE_CODE";
            cmd.Parameters.Add(FirebirdDb.P("@id", apptId, FbDbType.BigInt));

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!r.IsDBNull(0))
                    list.Add(r.GetString(0).Trim());
            }

            return list;
        }

        private bool HasAgentOverlap(string agentCode, DateTime start, DateTime end, long? excludeApptId)
        {
            var agent = (agentCode ?? "").Trim();
            if (string.IsNullOrEmpty(agent))
                return false;

            return HasOverlap_Generic("AGENT_CODE", agent, start, end, excludeApptId);
        }

        private bool HasCustomerOverlap(string customerCode, DateTime start, DateTime end, long? excludeApptId)
        {
            var cust = (customerCode ?? "").Trim();
            if (string.IsNullOrEmpty(cust))
                return false;

            return HasOverlap_Generic("CUSTOMER_CODE", cust, start, end, excludeApptId);
        }

        // Overlap checker (tries APPT_END if exists; else fixed duration by APPT_START)
        private bool HasOverlap_Generic(string columnName, string codeValue, DateTime start, DateTime end, long? excludeApptId)
        {
            using var conn = _db.Open();

            // 1) Try using APPT_END (if exists)
            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
SELECT COUNT(*)
FROM APPOINTMENT
WHERE {columnName} = @CODE
  AND ((@S < APPT_END) AND (@E > APPT_START))
  AND (@EX IS NULL OR APPT_ID <> @EX)";

                cmd.Parameters.Add(FirebirdDb.P("@CODE", codeValue, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@S", start, FbDbType.TimeStamp));
                cmd.Parameters.Add(FirebirdDb.P("@E", end, FbDbType.TimeStamp));
                cmd.Parameters.Add(FirebirdDb.P("@EX", excludeApptId, FbDbType.BigInt));

                var count = Convert.ToInt32(cmd.ExecuteScalar());
                return count > 0;
            }
            catch
            {
                // ignore -> fallback below
            }

            // 2) fallback: fixed duration window
            using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = $@"
SELECT APPT_START
FROM APPOINTMENT
WHERE {columnName} = @CODE
  AND (@EX IS NULL OR APPT_ID <> @EX)";

                cmd2.Parameters.Add(FirebirdDb.P("@CODE", codeValue, FbDbType.VarChar));
                cmd2.Parameters.Add(FirebirdDb.P("@EX", excludeApptId, FbDbType.BigInt));

                using var r = cmd2.ExecuteReader();
                while (r.Read())
                {
                    var existingStart = r.GetDateTime(0);
                    var existingEnd = existingStart.AddMinutes(DEFAULT_DURATION_MINUTES);

                    if (start < existingEnd && end > existingStart)
                        return true;
                }
            }

            return false;
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
