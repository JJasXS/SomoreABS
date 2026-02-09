using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using FirebirdSql.Data.FirebirdClient;
using YourApp.Data;
using YourApp.Models;

namespace YourApp.Controllers
{
    public partial class AppointmentController : Controller
    {
        // =========================================================
        // ✅ LIST
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
        // ✅ CREATE (GET)
        // =========================================================
        [HttpGet]
        public IActionResult Create(DateTime? apptStart)
        {
            var email = (User?.Identity?.Name ?? "").Trim();
            var branchNo = GetBranchNoByEmail(email);

            var start = apptStart ?? DateTime.Now;

            LoadAgentsAndCustomers(branchNo, out var agents, out var customers);

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
        // ✅ CREATE (POST)
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(Appointment m)
        {
            var email = (User?.Identity?.Name ?? "").Trim();
            var branchNo = GetBranchNoByEmail(email);
            LoadAgentsAndCustomers(branchNo, out var agents, out var customers);

            ViewBag.Agents = agents;
            ViewBag.Customers = customers;
            ViewBag.ServiceItems = LoadServiceItems();

            m.CustomerCode = (m.CustomerCode ?? "").Trim();
            m.AgentCode = (m.AgentCode ?? "").Trim();
            m.Title = (m.Title ?? "").Trim();
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
            using (var tx = conn.BeginTransaction())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
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

                foreach (var code in selectedServiceCodes)
                {
                    using var cmdDtl = conn.CreateCommand();
                    cmdDtl.Transaction = tx;
                    cmdDtl.CommandText = @"INSERT INTO APPT_DTL (APPT_ID, SERVICE_CODE) VALUES (@APPT_ID, @SERVICE_CODE)";
                    cmdDtl.Parameters.Add(FirebirdDb.P("@APPT_ID", newApptId, FbDbType.BigInt));
                    cmdDtl.Parameters.Add(FirebirdDb.P("@SERVICE_CODE", code.Trim(), FbDbType.VarChar));
                    cmdDtl.ExecuteNonQuery();
                }

                // Update UDF_CLAIMED and UDF_PREV_CLAIMED in SL_SODTL for each service in APPT_DTL
                // Get customer code for this appointment
                string customerCode = m.CustomerCode;
                int totalAffected = 0;
                foreach (var svc in selectedServiceCodes)
                {
                    int qty = 0;
                    using (var cmdQty = conn.CreateCommand())
                    {
                        cmdQty.Transaction = tx;
                        cmdQty.CommandText = @"SELECT COUNT(*) FROM APPT_DTL WHERE APPT_ID = @APPTID AND SERVICE_CODE = @SVC";
                        cmdQty.Parameters.Add(FirebirdDb.P("@APPTID", newApptId, FbDbType.BigInt));
                        cmdQty.Parameters.Add(FirebirdDb.P("@SVC", svc, FbDbType.VarChar));
                        qty = Convert.ToInt32(cmdQty.ExecuteScalar());
                    }
                    int prevClaimed = 0;
                    using (var cmdPrev = conn.CreateCommand())
                    {
                        cmdPrev.Transaction = tx;
                        cmdPrev.CommandText = @"SELECT UDF_CLAIMED FROM SL_SODTL d WHERE d.ITEMCODE = @SVC AND d.DOCKEY IN (SELECT s.DOCKEY FROM SL_SO s WHERE s.CODE = @CUST) ROWS 1";
                        cmdPrev.Parameters.Add(FirebirdDb.P("@SVC", svc, FbDbType.VarChar));
                        cmdPrev.Parameters.Add(FirebirdDb.P("@CUST", customerCode, FbDbType.VarChar));
                        var v = cmdPrev.ExecuteScalar();
                        prevClaimed = (v == null || v == DBNull.Value) ? 0 : Convert.ToInt32(v);
                    }
                    using (var cmdSo = conn.CreateCommand())
                    {
                        cmdSo.Transaction = tx;
                        cmdSo.CommandText = @"
UPDATE SL_SODTL d
SET UDF_PREV_CLAIMED = COALESCE(UDF_PREV_CLAIMED,0) + @PREV,
    UDF_CLAIMED = @QTY
WHERE d.ITEMCODE = @SVC
  AND d.DOCKEY IN (
      SELECT s.DOCKEY FROM SL_SO s
      WHERE s.CODE = @CUST
  )";
                        cmdSo.Parameters.Add(FirebirdDb.P("@PREV", prevClaimed, FbDbType.Integer));
                        cmdSo.Parameters.Add(FirebirdDb.P("@QTY", qty, FbDbType.Integer));
                        cmdSo.Parameters.Add(FirebirdDb.P("@SVC", svc, FbDbType.VarChar));
                        cmdSo.Parameters.Add(FirebirdDb.P("@CUST", customerCode, FbDbType.VarChar));
                        int affected = cmdSo.ExecuteNonQuery();
                        totalAffected += affected;
                    }
                }
                tx.Commit();
            }

            return RedirectToAction("Index", "Calendar", new { year = m.ApptStart.Year, month = m.ApptStart.Month });
        }

        // =========================================================
        // ✅ EDIT (GET)
        // =========================================================
        [HttpGet]
        public IActionResult Edit(long id)
        {
            var email = (User?.Identity?.Name ?? "").Trim();
            var branchNo = GetBranchNoByEmail(email);
            LoadAgentsAndCustomers(branchNo, out var agents, out var customers);

            ViewBag.Agents = agents;
            ViewBag.Customers = customers;
            ViewBag.ServiceItems = LoadServiceItems();

            Appointment? model = null;

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
            ViewBag.HasSignature = HasSignature(id);
            ViewBag.SignatureUrl = Url.Action("SignatureImage", "Appointment", new { id, v = DateTime.UtcNow.Ticks });

            return View(model);
        }

        // =========================================================
        // ✅ EDIT (POST)
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(Appointment m)
        {
            var email = (User?.Identity?.Name ?? "").Trim();
            var branchNo = GetBranchNoByEmail(email);
            LoadAgentsAndCustomers(branchNo, out var agents, out var customers);

            ViewBag.Agents = agents;
            ViewBag.Customers = customers;
            ViewBag.ServiceItems = LoadServiceItems();

            var selectedServiceCodes = ParseCsv(Request.Form["ServiceCodes"].ToString());
            ViewBag.SelectedServiceCodes = selectedServiceCodes;

            m.CustomerCode = (m.CustomerCode ?? "").Trim();
            m.AgentCode = (m.AgentCode ?? "").Trim();
            m.Title = (m.Title ?? "").Trim();
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
            using (var tx = conn.BeginTransaction())
            {
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
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

                using (var cmdDel = conn.CreateCommand())
                {
                    cmdDel.Transaction = tx;
                    cmdDel.CommandText = "DELETE FROM APPT_DTL WHERE APPT_ID = @id";
                    cmdDel.Parameters.Add(FirebirdDb.P("@id", m.ApptId, FbDbType.BigInt));
                    cmdDel.ExecuteNonQuery();
                }

                foreach (var code in selectedServiceCodes)
                {
                    using var cmdIns = conn.CreateCommand();
                    cmdIns.Transaction = tx;
                    cmdIns.CommandText = "INSERT INTO APPT_DTL (APPT_ID, SERVICE_CODE) VALUES (@APPT_ID, @SERVICE_CODE)";
                    cmdIns.Parameters.Add(FirebirdDb.P("@APPT_ID", m.ApptId, FbDbType.BigInt));
                    cmdIns.Parameters.Add(FirebirdDb.P("@SERVICE_CODE", code.Trim(), FbDbType.VarChar));
                    cmdIns.ExecuteNonQuery();
                }

                tx.Commit();
            }

            return RedirectToAction("Index", "Calendar", new { year = m.ApptStart.Year, month = m.ApptStart.Month });
        }

        // =========================================================
        // ❌ DELETE (GET/POST) DISABLED - we use AJAX delete only
        // =========================================================
        [HttpGet]
        public IActionResult Delete(long id) => NotFound();

        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteConfirmed(long id) => NotFound();
    }
}
