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
        // SQL (keep all SQL in one place)
        // =========================================================
        private const string SQL_APPT_LIST = @"
SELECT APPT_ID, CUSTOMER_CODE, AGENT_CODE, APPT_START, TITLE, STATUS
FROM APPOINTMENT
ORDER BY APPT_START DESC";

        private const string SQL_APPT_GET = @"
SELECT APPT_ID, CUSTOMER_CODE, AGENT_CODE, APPT_START, TITLE, STATUS, NOTES
FROM APPOINTMENT
WHERE APPT_ID = @id";

        private const string SQL_APPT_INSERT_RETURN_ID = @"
INSERT INTO APPOINTMENT
(CUSTOMER_CODE, AGENT_CODE, APPT_START, TITLE, NOTES, STATUS, CREATED_DT, CREATED_BY)
VALUES
(@CUSTOMER_CODE, @AGENT_CODE, @APPT_START, @TITLE, @NOTES, @STATUS, CURRENT_TIMESTAMP, @CREATED_BY)
RETURNING APPT_ID";

        private const string SQL_APPT_UPDATE = @"
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

        private const string SQL_APPTDTL_DELETE = @"DELETE FROM APPT_DTL WHERE APPT_ID = @id";

        private const string SQL_APPTDTL_INSERT = @"
INSERT INTO APPT_DTL (APPT_ID, SERVICE_CODE)
VALUES (@APPT_ID, @SERVICE_CODE)";

        private const string SQL_SODTL_FIRST = @"
SELECT
  QTY,
  COALESCE(UDF_CLAIMED,0),
  COALESCE(UDF_PREV_CLAIMED,0)
FROM SL_SODTL
WHERE ITEMCODE = @SVC
  AND DOCKEY IN (SELECT DOCKEY FROM SL_SO WHERE CODE = @CUST)
ROWS 1";

        private const string SQL_LOG_INSERT = @"
INSERT INTO APPOINTMENT_LOG
(APPT_ID, ACTION_TYPE, ACTION_TIME, USERNAME, DETAILS, SO_QTY, CLAIMED, PREV_CLAIMED, CURR_CLAIMED, SERVICE_CODE)
VALUES
(@APPTID, 'ADDED', CURRENT_TIMESTAMP, @USER, @DETAILS, @SOQTY, @CLAIMED, @PREV, @CURR, @SVC)";

        private const string SQL_LOG_LIST = @"
SELECT ACTION_TYPE, ACTION_TIME, USERNAME, DETAILS, SO_QTY, CLAIMED, PREV_CLAIMED, CURR_CLAIMED, SERVICE_CODE
FROM APPOINTMENT_LOG
WHERE APPT_ID = @APPTID
ORDER BY ACTION_TIME DESC";

        // =========================================================
        // ✅ LIST
        // =========================================================
        public IActionResult Index()
        {
            var list = new List<Appointment>();

            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = SQL_APPT_LIST;

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
            var branchNo = GetBranchNoByEmail(email); // GetBranchNoByEmail already uses UDF_EMAIL/UDF_BRANCH

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
            // ✅ Always reload dropdown data so page can re-render on validation error
            var email = (User?.Identity?.Name ?? "").Trim();
            var branchNo = GetBranchNoByEmail(email); // GetBranchNoByEmail already uses UDF_EMAIL/UDF_BRANCH
            LoadAgentsAndCustomers(branchNo, out var agents, out var customers);

            ViewBag.Agents = agents;
            ViewBag.Customers = customers;
            ViewBag.ServiceItems = LoadServiceItems();

            // ✅ sanitize
            m.CustomerCode = (m.CustomerCode ?? "").Trim();
            m.AgentCode = (m.AgentCode ?? "").Trim();
            m.Title = (m.Title ?? "").Trim();
            m.Status = "BOOKED";

            // ✅ services
            var selectedServiceCodes = ParseCsv(Request.Form["ServiceCodes"].ToString());

            if (!ModelState.IsValid)
                return View(m);

            if (selectedServiceCodes.Count == 0)
            {
                ModelState.AddModelError("", "Please select at least one service.");
                return View(m);
            }

            // You calculated these earlier; keep in case you re-enable overlap checks
            var start = m.ApptStart;
            var end = m.ApptStart.AddMinutes(DEFAULT_DURATION_MINUTES);

            // =========================
            // Optional overlap checks (currently disabled by your comment blocks)
            // =========================
            /*
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
            */

            long newApptId = 0;
            string customerCode = m.CustomerCode;

            using (var conn = _db.Open())
            using (var tx = conn.BeginTransaction())
            {
                // 1) Insert APPOINTMENT + return APPT_ID
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = SQL_APPT_INSERT_RETURN_ID;

                    cmd.Parameters.Add(FirebirdDb.P("@CUSTOMER_CODE", m.CustomerCode, FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@AGENT_CODE", m.AgentCode, FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@APPT_START", m.ApptStart, FbDbType.TimeStamp));
                    cmd.Parameters.Add(FirebirdDb.P("@TITLE", string.IsNullOrWhiteSpace(m.Title) ? null : m.Title, FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@NOTES", m.Notes, FbDbType.Text));
                    cmd.Parameters.Add(FirebirdDb.P("@STATUS", m.Status, FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@CREATED_BY", (User?.Identity?.Name ?? "SYSTEM").Trim(), FbDbType.VarChar));

                    newApptId = Convert.ToInt64(cmd.ExecuteScalar());
                }

                // 2) Insert APPT_DTL per service code + write audit log snapshot
                foreach (var rawCode in selectedServiceCodes)
                {
                    var code = (rawCode ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(code)) continue;

                    // 2a) APPT_DTL
                    using (var cmdDtl = conn.CreateCommand())
                    {
                        cmdDtl.Transaction = tx;
                        cmdDtl.CommandText = SQL_APPTDTL_INSERT;
                        cmdDtl.Parameters.Add(FirebirdDb.P("@APPT_ID", newApptId, FbDbType.BigInt));
                        cmdDtl.Parameters.Add(FirebirdDb.P("@SERVICE_CODE", code, FbDbType.VarChar));
                        cmdDtl.ExecuteNonQuery();
                    }

                    // 2b) Update SL_SODTL claim columns
                    int qty = 0;
                    using (var cmdQty = conn.CreateCommand())
                    {
                        cmdQty.Transaction = tx;
                        cmdQty.CommandText = "SELECT COUNT(*) FROM APPT_DTL WHERE APPT_ID = @APPTID AND SERVICE_CODE = @SVC";
                        cmdQty.Parameters.Add(FirebirdDb.P("@APPTID", newApptId, FbDbType.BigInt));
                        cmdQty.Parameters.Add(FirebirdDb.P("@SVC", code, FbDbType.VarChar));
                        qty = Convert.ToInt32(cmdQty.ExecuteScalar());
                    }
                    int prevClaimed = 0;
                    using (var cmdPrev = conn.CreateCommand())
                    {
                        cmdPrev.Transaction = tx;
                        cmdPrev.CommandText = "SELECT UDF_CLAIMED FROM SL_SODTL d WHERE d.ITEMCODE = @SVC AND d.DOCKEY IN (SELECT s.DOCKEY FROM SL_SO s WHERE s.CODE = @CUST) ROWS 1";
                        cmdPrev.Parameters.Add(FirebirdDb.P("@SVC", code, FbDbType.VarChar));
                        cmdPrev.Parameters.Add(FirebirdDb.P("@CUST", customerCode, FbDbType.VarChar));
                        var v = cmdPrev.ExecuteScalar();
                        prevClaimed = (v == null || v == DBNull.Value) ? 0 : Convert.ToInt32(v);
                    }
                    using (var cmdSo = conn.CreateCommand())
                    {
                        cmdSo.Transaction = tx;
                        cmdSo.CommandText = @"UPDATE SL_SODTL d SET UDF_PREV_CLAIMED = COALESCE(UDF_PREV_CLAIMED,0) + @PREV, UDF_CLAIMED = @QTY WHERE d.ITEMCODE = @SVC AND d.DOCKEY IN (SELECT s.DOCKEY FROM SL_SO s WHERE s.CODE = @CUST)";
                        cmdSo.Parameters.Add(FirebirdDb.P("@PREV", prevClaimed, FbDbType.Integer));
                        cmdSo.Parameters.Add(FirebirdDb.P("@QTY", qty, FbDbType.Integer));
                        cmdSo.Parameters.Add(FirebirdDb.P("@SVC", code, FbDbType.VarChar));
                        cmdSo.Parameters.Add(FirebirdDb.P("@CUST", customerCode, FbDbType.VarChar));
                        cmdSo.ExecuteNonQuery();
                    }

                    // 2c) Read SO snapshot for log
                    int soQty = 0, claimed = 0, prevClaimedLog = 0, currClaimed = 0;
                    using (var cmdSodtl = conn.CreateCommand())
                    {
                        cmdSodtl.Transaction = tx;
                        cmdSodtl.CommandText = SQL_SODTL_FIRST;
                        cmdSodtl.Parameters.Add(FirebirdDb.P("@SVC", code, FbDbType.VarChar));
                        cmdSodtl.Parameters.Add(FirebirdDb.P("@CUST", customerCode, FbDbType.VarChar));

                        using var rSodtl = cmdSodtl.ExecuteReader();
                        if (rSodtl.Read())
                        {
                            soQty = rSodtl.IsDBNull(0) ? 0 : rSodtl.GetInt32(0);
                            claimed = rSodtl.IsDBNull(1) ? 0 : rSodtl.GetInt32(1);
                            prevClaimedLog = rSodtl.IsDBNull(2) ? 0 : rSodtl.GetInt32(2);
                            currClaimed = claimed + prevClaimedLog;
                        }
                    }

                    // 2d) Log CREATE snapshot
                    using (var cmdLog = conn.CreateCommand())
                    {
                        cmdLog.Transaction = tx;
                        cmdLog.CommandText = SQL_LOG_INSERT;

                        cmdLog.Parameters.Add(FirebirdDb.P("@APPTID", newApptId, FbDbType.BigInt));
                        cmdLog.Parameters.Add(FirebirdDb.P("@USER", (User?.Identity?.Name ?? "SYSTEM").Trim(), FbDbType.VarChar));
                        cmdLog.Parameters.Add(FirebirdDb.P("@DETAILS", "Appointment created.", FbDbType.VarChar));
                        cmdLog.Parameters.Add(FirebirdDb.P("@SOQTY", soQty, FbDbType.Integer));
                        cmdLog.Parameters.Add(FirebirdDb.P("@CLAIMED", claimed, FbDbType.Integer));
                        cmdLog.Parameters.Add(FirebirdDb.P("@PREV", prevClaimedLog, FbDbType.Integer));
                        cmdLog.Parameters.Add(FirebirdDb.P("@CURR", currClaimed, FbDbType.Integer));
                        cmdLog.Parameters.Add(FirebirdDb.P("@SVC", code, FbDbType.VarChar));

                        cmdLog.ExecuteNonQuery();
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
                cmd.CommandText = SQL_APPT_GET;
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
                // 1) Update APPOINTMENT
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = SQL_APPT_UPDATE;

                    cmd.Parameters.Add(FirebirdDb.P("@CUSTOMER_CODE", m.CustomerCode, FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@AGENT_CODE", m.AgentCode, FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@APPT_START", m.ApptStart, FbDbType.TimeStamp));
                    cmd.Parameters.Add(FirebirdDb.P("@TITLE", string.IsNullOrWhiteSpace(m.Title) ? null : m.Title, FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@NOTES", m.Notes, FbDbType.Text));
                    cmd.Parameters.Add(FirebirdDb.P("@STATUS", m.Status, FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@BY", (User?.Identity?.Name ?? "SYSTEM").Trim(), FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@APPT_ID", m.ApptId, FbDbType.BigInt));

                    cmd.ExecuteNonQuery();
                }

                // 2) Replace APPT_DTL rows
                using (var cmdDel = conn.CreateCommand())
                {
                    cmdDel.Transaction = tx;
                    cmdDel.CommandText = SQL_APPTDTL_DELETE;
                    cmdDel.Parameters.Add(FirebirdDb.P("@id", m.ApptId, FbDbType.BigInt));
                    cmdDel.ExecuteNonQuery();
                }

                foreach (var rawCode in selectedServiceCodes)
                {
                    var code = (rawCode ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(code)) continue;

                    using var cmdIns = conn.CreateCommand();
                    cmdIns.Transaction = tx;
                    cmdIns.CommandText = SQL_APPTDTL_INSERT;
                    cmdIns.Parameters.Add(FirebirdDb.P("@APPT_ID", m.ApptId, FbDbType.BigInt));
                    cmdIns.Parameters.Add(FirebirdDb.P("@SERVICE_CODE", code, FbDbType.VarChar));
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
