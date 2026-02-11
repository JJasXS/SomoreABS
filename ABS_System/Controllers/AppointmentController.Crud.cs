using System;
using System.Collections.Generic;
using System.Linq;
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
  COALESCE(QTY,0) AS QTY,
  COALESCE(UDF_CLAIMED,0) AS CLAIMED,
  COALESCE(UDF_PREV_CLAIMED,0) AS PREV_CLAIMED
FROM SL_SODTL
WHERE ITEMCODE = @SVC
  AND DOCKEY IN (SELECT DOCKEY FROM SL_SO WHERE CODE = @CUST)
ROWS 1";

        private const string SQL_LOG_INSERT = @"
INSERT INTO APPOINTMENT_LOG
(APPT_ID, ACTION_TYPE, ACTION_TIME, USERNAME, DETAILS, SO_QTY, CLAIMED, PREV_CLAIMED, CURR_CLAIMED, SERVICE_CODE)
VALUES
(@APPTID, @ACTION, CURRENT_TIMESTAMP, @USER, @DETAILS, @SOQTY, @CLAIMED, @PREV, @CURR, @SVC)";

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
            // reload dropdowns
            var email = (User?.Identity?.Name ?? "").Trim();
            var branchNo = GetBranchNoByEmail(email);
            LoadAgentsAndCustomers(branchNo, out var agents, out var customers);

            ViewBag.Agents = agents;
            ViewBag.Customers = customers;
            ViewBag.ServiceItems = LoadServiceItems();

            // sanitize
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

            long newApptId;

            using (var conn = _db.Open())
            using (var tx = conn.BeginTransaction())
            {
                // 1) insert APPOINTMENT -> APPT_ID
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

                // 2) insert APPT_DTL + update claims + log per service
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

                    // 2b) read current claimed BEFORE update
                    int prevClaimed;
                    using (var cmdPrev = conn.CreateCommand())
                    {
                        cmdPrev.Transaction = tx;
                        cmdPrev.CommandText = @"
SELECT COALESCE(UDF_CLAIMED,0)
FROM SL_SODTL d
WHERE d.ITEMCODE = @SVC
  AND d.DOCKEY IN (SELECT s.DOCKEY FROM SL_SO s WHERE s.CODE = @CUST)
ROWS 1";
                        cmdPrev.Parameters.Add(FirebirdDb.P("@SVC", code, FbDbType.VarChar));
                        cmdPrev.Parameters.Add(FirebirdDb.P("@CUST", m.CustomerCode, FbDbType.VarChar));
                        prevClaimed = Convert.ToInt32(cmdPrev.ExecuteScalar() ?? 0);
                    }

                    // 2c) update claimed (+1) and store prev snapshot
                    using (var cmdSo = conn.CreateCommand())
                    {
                        cmdSo.Transaction = tx;
                        cmdSo.CommandText = @"
UPDATE SL_SODTL d
SET
  UDF_PREV_CLAIMED = @PREV,
  UDF_CLAIMED      = COALESCE(UDF_CLAIMED,0) + 1
WHERE d.ITEMCODE = @SVC
  AND d.DOCKEY IN (SELECT s.DOCKEY FROM SL_SO s WHERE s.CODE = @CUST)";
                        cmdSo.Parameters.Add(FirebirdDb.P("@PREV", prevClaimed, FbDbType.Integer));
                        cmdSo.Parameters.Add(FirebirdDb.P("@SVC", code, FbDbType.VarChar));
                        cmdSo.Parameters.Add(FirebirdDb.P("@CUST", m.CustomerCode, FbDbType.VarChar));
                        cmdSo.ExecuteNonQuery();
                    }

                    // 2d) snapshot for log
                    int soQty = 0, claimed = 0, prevClaimedLog = 0, currClaimed = 0;
                    using (var cmdSodtl = conn.CreateCommand())
                    {
                        cmdSodtl.Transaction = tx;
                        cmdSodtl.CommandText = SQL_SODTL_FIRST;
                        cmdSodtl.Parameters.Add(FirebirdDb.P("@SVC", code, FbDbType.VarChar));
                        cmdSodtl.Parameters.Add(FirebirdDb.P("@CUST", m.CustomerCode, FbDbType.VarChar));

                        using var rSodtl = cmdSodtl.ExecuteReader();
                        if (rSodtl.Read())
                        {
                            soQty = rSodtl.IsDBNull(0) ? 0 : rSodtl.GetInt32(0);
                            claimed = rSodtl.IsDBNull(1) ? 0 : rSodtl.GetInt32(1);
                            prevClaimedLog = rSodtl.IsDBNull(2) ? 0 : rSodtl.GetInt32(2);
                            currClaimed = claimed; // after update, claimed already includes +1
                        }
                    }

                    // 2e) log
                    using (var cmdLog = conn.CreateCommand())
                    {
                        cmdLog.Transaction = tx;
                        cmdLog.CommandText = SQL_LOG_INSERT;

                        cmdLog.Parameters.Add(FirebirdDb.P("@APPTID", newApptId, FbDbType.BigInt));
                        cmdLog.Parameters.Add(FirebirdDb.P("@ACTION", "ADDED", FbDbType.VarChar));
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

            var originalServiceCodes = LoadSelectedServiceCodes(id);
            // Decrement claim for original services so they appear in dropdown
            using (var conn = _db.Open())
            using (var tx = conn.BeginTransaction())
            {
                foreach (var code in originalServiceCodes)
                {
                    using var cmdDec = conn.CreateCommand();
                    cmdDec.Transaction = tx;
                    cmdDec.CommandText = @"UPDATE SL_SODTL d SET UDF_CLAIMED = COALESCE(UDF_CLAIMED,0) - 1 WHERE d.ITEMCODE = @SVC AND d.DOCKEY IN (SELECT s.DOCKEY FROM SL_SO s WHERE s.CODE = @CUST)";
                    cmdDec.Parameters.Add(FirebirdDb.P("@SVC", code, FbDbType.VarChar));
                    cmdDec.Parameters.Add(FirebirdDb.P("@CUST", model.CustomerCode, FbDbType.VarChar));
                    cmdDec.ExecuteNonQuery();
                }
                tx.Commit();
            }
            TempData[$"EditClaim_{id}"] = string.Join(",", originalServiceCodes);
            TempData[$"EditCustomer_{id}"] = model.CustomerCode;
            ViewBag.SelectedServiceCodes = originalServiceCodes;
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
            // reload dropdowns
            var email = (User?.Identity?.Name ?? "").Trim();
            var branchNo = GetBranchNoByEmail(email);
            LoadAgentsAndCustomers(branchNo, out var agents, out var customers);

            ViewBag.Agents = agents;
            ViewBag.Customers = customers;
            ViewBag.ServiceItems = LoadServiceItems();


            // selected services from form
            var selectedServiceCodes = ParseCsv(Request.Form["ServiceCodes"].ToString());
            ViewBag.SelectedServiceCodes = selectedServiceCodes;

            // sanitize
            m.CustomerCode = (m.CustomerCode ?? "").Trim();
            m.AgentCode = (m.AgentCode ?? "").Trim();
            m.Title = (m.Title ?? "").Trim();
            m.Status = string.IsNullOrWhiteSpace(m.Status) ? "BOOKED" : m.Status.Trim();

            // Restore claim for original services if edit is cancelled or unchanged
            var tempKey = $"EditClaim_{m.ApptId}";
            var tempCustKey = $"EditCustomer_{m.ApptId}";
            var origCodes = (TempData[tempKey] as string)?.Split(',').Where(x => !string.IsNullOrWhiteSpace(x)).ToList() ?? new List<string>();
            var origCustomer = TempData[tempCustKey] as string ?? m.CustomerCode;
            var unchanged = origCodes.Count == selectedServiceCodes.Count && !origCodes.Except(selectedServiceCodes).Any();
            if (Request.Form["cancel"] == "true" || unchanged)
            {
                using (var conn = _db.Open())
                using (var tx = conn.BeginTransaction())
                {
                    foreach (var code in origCodes)
                    {
                        using var cmdInc = conn.CreateCommand();
                        cmdInc.Transaction = tx;
                        cmdInc.CommandText = @"UPDATE SL_SODTL d SET UDF_CLAIMED = COALESCE(UDF_CLAIMED,0) + 1 WHERE d.ITEMCODE = @SVC AND d.DOCKEY IN (SELECT s.DOCKEY FROM SL_SO s WHERE s.CODE = @CUST)";
                        cmdInc.Parameters.Add(FirebirdDb.P("@SVC", code, FbDbType.VarChar));
                        cmdInc.Parameters.Add(FirebirdDb.P("@CUST", origCustomer, FbDbType.VarChar));
                        cmdInc.ExecuteNonQuery();
                    }
                    tx.Commit();
                }
                return RedirectToAction("Index", "Calendar", new { year = m.ApptStart.Year, month = m.ApptStart.Month });
            }
            // ...existing code for changed services...

            if (!ModelState.IsValid)
                return View(m);

            if (selectedServiceCodes.Count == 0)
            {
                ModelState.AddModelError("", "Please select at least one service.");
                return View(m);
            }

            // overlap checks
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

            // diff services
            var removedServices = origCodes.Except(selectedServiceCodes).ToList();
            var addedServices = selectedServiceCodes.Except(origCodes).ToList();

            using (var conn = _db.Open())
            using (var tx = conn.BeginTransaction())
            {
                // 1) update APPOINTMENT
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

                // 2) claims: removed (-1)
                foreach (var code in removedServices)
                {
                    var svc = (code ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(svc)) continue;

                    using var cmdDec = conn.CreateCommand();
                    cmdDec.Transaction = tx;
                    cmdDec.CommandText = @"
UPDATE SL_SODTL d
SET UDF_CLAIMED = CASE WHEN COALESCE(UDF_CLAIMED,0) > 0 THEN COALESCE(UDF_CLAIMED,0) - 1 ELSE 0 END
WHERE d.ITEMCODE = @SVC
  AND d.DOCKEY IN (SELECT s.DOCKEY FROM SL_SO s WHERE s.CODE = @CUST)";
                    cmdDec.Parameters.Add(FirebirdDb.P("@SVC", svc, FbDbType.VarChar));
                    cmdDec.Parameters.Add(FirebirdDb.P("@CUST", m.CustomerCode, FbDbType.VarChar));
                    cmdDec.ExecuteNonQuery();
                }

                // 3) claims: added (+1)
                foreach (var code in addedServices)
                {
                    var svc = (code ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(svc)) continue;

                    using var cmdInc = conn.CreateCommand();
                    cmdInc.Transaction = tx;
                    cmdInc.CommandText = @"
UPDATE SL_SODTL d
SET UDF_CLAIMED = COALESCE(UDF_CLAIMED,0) + 1
WHERE d.ITEMCODE = @SVC
  AND d.DOCKEY IN (SELECT s.DOCKEY FROM SL_SO s WHERE s.CODE = @CUST)";
                    cmdInc.Parameters.Add(FirebirdDb.P("@SVC", svc, FbDbType.VarChar));
                    cmdInc.Parameters.Add(FirebirdDb.P("@CUST", m.CustomerCode, FbDbType.VarChar));
                    cmdInc.ExecuteNonQuery();
                }

                // 4) replace APPT_DTL rows
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

                // 5) logs (added/removed/general)
                foreach (var code in addedServices)
                {
                    using var cmdLog = conn.CreateCommand();
                    cmdLog.Transaction = tx;
                    cmdLog.CommandText = SQL_LOG_INSERT;

                    cmdLog.Parameters.Add(FirebirdDb.P("@APPTID", m.ApptId, FbDbType.BigInt));
                    cmdLog.Parameters.Add(FirebirdDb.P("@ACTION", "EDITED", FbDbType.VarChar));
                    cmdLog.Parameters.Add(FirebirdDb.P("@USER", (User?.Identity?.Name ?? "SYSTEM").Trim(), FbDbType.VarChar));
                    cmdLog.Parameters.Add(FirebirdDb.P("@DETAILS", $"Service added: {code}", FbDbType.VarChar));
                    cmdLog.Parameters.Add(FirebirdDb.P("@SOQTY", 0, FbDbType.Integer));
                    cmdLog.Parameters.Add(FirebirdDb.P("@CLAIMED", 0, FbDbType.Integer));
                    cmdLog.Parameters.Add(FirebirdDb.P("@PREV", 0, FbDbType.Integer));
                    cmdLog.Parameters.Add(FirebirdDb.P("@CURR", 0, FbDbType.Integer));
                    cmdLog.Parameters.Add(FirebirdDb.P("@SVC", code, FbDbType.VarChar));

                    cmdLog.ExecuteNonQuery();
                }

                foreach (var code in removedServices)
                {
                    using var cmdLog = conn.CreateCommand();
                    cmdLog.Transaction = tx;
                    cmdLog.CommandText = SQL_LOG_INSERT;

                    cmdLog.Parameters.Add(FirebirdDb.P("@APPTID", m.ApptId, FbDbType.BigInt));
                    cmdLog.Parameters.Add(FirebirdDb.P("@ACTION", "EDITED", FbDbType.VarChar));
                    cmdLog.Parameters.Add(FirebirdDb.P("@USER", (User?.Identity?.Name ?? "SYSTEM").Trim(), FbDbType.VarChar));
                    cmdLog.Parameters.Add(FirebirdDb.P("@DETAILS", $"Service removed: {code}", FbDbType.VarChar));
                    cmdLog.Parameters.Add(FirebirdDb.P("@SOQTY", 0, FbDbType.Integer));
                    cmdLog.Parameters.Add(FirebirdDb.P("@CLAIMED", 0, FbDbType.Integer));
                    cmdLog.Parameters.Add(FirebirdDb.P("@PREV", 0, FbDbType.Integer));
                    cmdLog.Parameters.Add(FirebirdDb.P("@CURR", 0, FbDbType.Integer));
                    cmdLog.Parameters.Add(FirebirdDb.P("@SVC", code, FbDbType.VarChar));

                    cmdLog.ExecuteNonQuery();
                }

                using (var cmdLog = conn.CreateCommand())
                {
                    cmdLog.Transaction = tx;
                    cmdLog.CommandText = SQL_LOG_INSERT;

                    cmdLog.Parameters.Add(FirebirdDb.P("@APPTID", m.ApptId, FbDbType.BigInt));
                    cmdLog.Parameters.Add(FirebirdDb.P("@ACTION", "EDITED", FbDbType.VarChar));
                    cmdLog.Parameters.Add(FirebirdDb.P("@USER", (User?.Identity?.Name ?? "SYSTEM").Trim(), FbDbType.VarChar));
                    cmdLog.Parameters.Add(FirebirdDb.P("@DETAILS", "Appointment edited.", FbDbType.VarChar));
                    cmdLog.Parameters.Add(FirebirdDb.P("@SOQTY", 0, FbDbType.Integer));
                    cmdLog.Parameters.Add(FirebirdDb.P("@CLAIMED", 0, FbDbType.Integer));
                    cmdLog.Parameters.Add(FirebirdDb.P("@PREV", 0, FbDbType.Integer));
                    cmdLog.Parameters.Add(FirebirdDb.P("@CURR", 0, FbDbType.Integer));
                    cmdLog.Parameters.Add(FirebirdDb.P("@SVC", "", FbDbType.VarChar));

                    cmdLog.ExecuteNonQuery();
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
};
