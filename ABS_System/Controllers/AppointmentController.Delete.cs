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

                // 1) delete signature first (child)
                using (var cmdSig = conn.CreateCommand())
                {
                    cmdSig.Transaction = tx;
                    cmdSig.CommandText = "DELETE FROM APPT_SIGNATURE WHERE APPT_ID = @id";
                    cmdSig.Parameters.Add(FirebirdDb.P("@id", id, FbDbType.BigInt));
                    cmdSig.ExecuteNonQuery();
                }

                // 2) delete details first (child)
                using (var cmdDtl = conn.CreateCommand())
                {
                    cmdDtl.Transaction = tx;
                    cmdDtl.CommandText = "DELETE FROM APPT_DTL WHERE APPT_ID = @id";
                    cmdDtl.Parameters.Add(FirebirdDb.P("@id", id, FbDbType.BigInt));
                    cmdDtl.ExecuteNonQuery();
                }

                // 3) delete parent
                int rows;
                using (var cmd = conn.CreateCommand())
                {
                    cmd.Transaction = tx;
                    cmd.CommandText = "DELETE FROM APPOINTMENT WHERE APPT_ID = @id";
                    cmd.Parameters.Add(FirebirdDb.P("@id", id, FbDbType.BigInt));
                    rows = cmd.ExecuteNonQuery();
                }

                if (rows <= 0)
                {
                    tx.Rollback();
                    return Json(new { ok = false, message = "Appointment not found or already deleted." });
                }

                tx.Commit();
                return Json(new { ok = true });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, message = "Delete failed.", detail = ex.Message });
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

                return Json(new { ok = true, status });
            }
            catch (Exception ex)
            {
                return Json(new { ok = false, message = "Failed to update status.", detail = ex.Message });
            }
        }
    }
}
