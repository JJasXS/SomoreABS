using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using FirebirdSql.Data.FirebirdClient;
using YourApp.Data;

namespace YourApp.Controllers
{
    public partial class AppointmentController : Controller
    {
        // =========================================================
        // ✅ DELETE (AJAX) - FK-safe + NO Delete.cshtml
        // POST: /Appointment/DeleteAjax
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult DeleteAjax(long id)
        {
            if (id <= 0)
                return Json(new { ok = false, message = "Invalid appointment id." });

            try
            {
                using var conn = _db.Open();
                using var tx = conn.BeginTransaction();

                // Get customer code and service codes for this appointment
                string customerCode = "";
                var serviceCodes = new List<string>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT CUSTOMER_CODE FROM APPOINTMENT WHERE APPT_ID = @id";
                    cmd.Parameters.Add(FirebirdDb.P("@id", id, FbDbType.BigInt));
                    var v = cmd.ExecuteScalar();
                    customerCode = (v == null || v == DBNull.Value) ? "" : (v?.ToString()?.Trim() ?? "");
                }
                // Get service codes and QTYs for this appointment
                var serviceQtys = new List<(string svc, int qty)>();
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "SELECT SERVICE_CODE, QTY FROM APPT_DTL WHERE APPT_ID = @id";
                    cmd.Parameters.Add(FirebirdDb.P("@id", id, FbDbType.BigInt));
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        if (!r.IsDBNull(0))
                        {
                            var svc = r.GetString(0).Trim();
                            var qty = r.IsDBNull(1) ? 1 : r.GetInt32(1);
                            serviceQtys.Add((svc, qty));
                        }
                    }
                }

                // Restore CLAIMED for each service by QTY
                foreach (var (svc, qty) in serviceQtys)
                {
                    using (var cmdSo = conn.CreateCommand())
                    {
                        cmdSo.Transaction = tx;
                        cmdSo.CommandText = @"
UPDATE SL_SODTL d
SET UDF_CLAIMED = COALESCE(UDF_CLAIMED,0) - @QTY
WHERE d.ITEMCODE = @SVC
  AND d.DOCKEY IN (
      SELECT s.DOCKEY FROM SL_SO s
      WHERE s.CODE = @CUST
  )";
                        cmdSo.Parameters.Add(FirebirdDb.P("@QTY", qty, FbDbType.Integer));
                        cmdSo.Parameters.Add(FirebirdDb.P("@SVC", svc, FbDbType.VarChar));
                        cmdSo.Parameters.Add(FirebirdDb.P("@CUST", customerCode, FbDbType.VarChar));
                        cmdSo.ExecuteNonQuery();
                    }
                }

                // Instead of deleting, just update status to CANCELLED
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = @"
UPDATE APPOINTMENT
SET STATUS = 'CANCELLED', LAST_UPD_DT = CURRENT_TIMESTAMP, LAST_UPD_BY = @BY
WHERE APPT_ID = @ID";
                    cmd.Parameters.Add(FirebirdDb.P("@BY", User?.Identity?.Name ?? "SYSTEM", FbDbType.VarChar));
                    cmd.Parameters.Add(FirebirdDb.P("@ID", id, FbDbType.BigInt));
                    cmd.ExecuteNonQuery();
                }

                tx.Commit();
                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, message = "Cancel failed.", detail = ex.Message });
            }
        }

        // =========================================================
        // ✅ QUICK STATUS UPDATE (AJAX)
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
                "FULFILLED",
                "CANCELLED"
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

                return Json(new { ok = true, status });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, message = "Failed to update status.", detail = ex.Message });
            }
        }
    }
}
