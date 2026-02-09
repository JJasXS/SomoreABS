using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using FirebirdSql.Data.FirebirdClient;
using YourApp.Data;
using YourApp.Models;

namespace YourApp.Controllers
{
    public partial class AppointmentController : Controller
    {
        // =========================================================
        // ✅ SIGNATURE (GET)
        // GET: /Appointment/Sign/5
        // =========================================================
        [HttpGet]
        public IActionResult Sign(long id)
        {
            if (id <= 0) return NotFound();

            Appointment? appt = null;

            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = @"
SELECT APPT_ID, CUSTOMER_CODE, AGENT_CODE, APPT_START, TITLE, STATUS, NOTES
FROM APPOINTMENT
WHERE APPT_ID = @ID";

                cmd.Parameters.Add(FirebirdDb.P("@ID", id, FbDbType.BigInt));

                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    appt = new Appointment
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
            catch
            {
                // keep appt null
            }

            if (appt == null) return NotFound();

            ViewBag.HasSignature = HasSignature(id);
            ViewBag.SignatureUrl = Url.Action("SignatureImage", "Appointment", new { id });

            ViewBag.DefaultSignedBy = GetCustomerCompanyName(appt.CustomerCode);

            ViewBag.StatementText =
                "I hereby confirm and acknowledge that the appointment details shown are correct. " +
                "I agree that this e-signature is valid and may be used as proof of acknowledgement.";

            return View(appt);
        }

        // =========================================================
        // ✅ SIGNATURE SAVE (POST)
        // POST: /Appointment/SignSave
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult SignSave(long apptId, string? signedBy, string? remarks, string? statementText, string signatureDataUrl)
        {
            if (apptId <= 0)
            {
                TempData["Err"] = "Invalid appointment.";
                return RedirectToAction("Sign", new { id = apptId });
            }

            signedBy = (signedBy ?? "").Trim();
            remarks = (remarks ?? "").Trim();
            statementText = (statementText ?? "").Trim();

            if (string.IsNullOrWhiteSpace(signatureDataUrl))
            {
                TempData["Err"] = "Signature is required.";
                return RedirectToAction("Sign", new { id = apptId });
            }

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

            string combinedRemarks = statementText;
            if (!string.IsNullOrWhiteSpace(remarks))
            {
                combinedRemarks =
                    (string.IsNullOrWhiteSpace(combinedRemarks) ? "" : combinedRemarks + Environment.NewLine + Environment.NewLine)
                    + "Additional Notes: " + remarks;
            }

            try
            {
                using var conn = _db.Open();
                using var tx = conn.BeginTransaction();

                bool exists;
                using (var cmdChk = conn.CreateCommand())
                {
                    cmdChk.Transaction = tx;
                    cmdChk.CommandText = "SELECT COUNT(*) FROM APPT_SIGNATURE WHERE APPT_ID = @ID";
                    cmdChk.Parameters.Add(FirebirdDb.P("@ID", apptId, FbDbType.BigInt));
                    exists = Convert.ToInt32(cmdChk.ExecuteScalar()) > 0;
                }

                if (exists)
                {
                    using var cmdUp = conn.CreateCommand();
                    cmdUp.Transaction = tx;
                    cmdUp.CommandText = @"
UPDATE APPT_SIGNATURE
SET SIGNED_DT = CURRENT_TIMESTAMP,
    SIGNED_BY = @BY,
    REMARKS = @REM,
    SIGNATURE_PNG = @PNG
WHERE APPT_ID = @ID";

                    cmdUp.Parameters.Add(FirebirdDb.P("@BY", string.IsNullOrWhiteSpace(signedBy) ? null : signedBy, FbDbType.VarChar));
                    cmdUp.Parameters.Add(FirebirdDb.P("@REM", string.IsNullOrWhiteSpace(combinedRemarks) ? null : combinedRemarks, FbDbType.VarChar));
                    cmdUp.Parameters.Add(new FbParameter("@PNG", FbDbType.Binary) { Value = pngBytes });
                    cmdUp.Parameters.Add(FirebirdDb.P("@ID", apptId, FbDbType.BigInt));
                    cmdUp.ExecuteNonQuery();
                }
                else
                {
                    using var cmdIns = conn.CreateCommand();
                    cmdIns.Transaction = tx;
                    cmdIns.CommandText = @"
INSERT INTO APPT_SIGNATURE (APPT_ID, SIGNED_DT, SIGNED_BY, REMARKS, SIGNATURE_PNG)
VALUES (@ID, CURRENT_TIMESTAMP, @BY, @REM, @PNG)";

                    cmdIns.Parameters.Add(FirebirdDb.P("@ID", apptId, FbDbType.BigInt));
                    cmdIns.Parameters.Add(FirebirdDb.P("@BY", string.IsNullOrWhiteSpace(signedBy) ? null : signedBy, FbDbType.VarChar));
                    cmdIns.Parameters.Add(FirebirdDb.P("@REM", string.IsNullOrWhiteSpace(combinedRemarks) ? null : combinedRemarks, FbDbType.VarChar));
                    cmdIns.Parameters.Add(new FbParameter("@PNG", FbDbType.Binary) { Value = pngBytes });
                    cmdIns.ExecuteNonQuery();
                }

                using (var cmdAppt = conn.CreateCommand())
                {
                    cmdAppt.Transaction = tx;
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

                // Update CLAIMED and PREV_CLAIMED in SL_SODTL for each service in APPT_DTL
                using (var cmdDtl = conn.CreateCommand())
                {
                    cmdDtl.Transaction = tx;
                    cmdDtl.CommandText = @"SELECT SERVICE_CODE FROM APPT_DTL WHERE APPT_ID = @APPTID";
                    cmdDtl.Parameters.Add(FirebirdDb.P("@APPTID", apptId, FbDbType.BigInt));
                    var serviceCodes = new List<string>();
                    using (var r = cmdDtl.ExecuteReader())
                    {
                        while (r.Read())
                        {
                            if (!r.IsDBNull(0))
                                serviceCodes.Add(r.GetString(0).Trim());
                        }
                    }

                    // Get customer code for this appointment
                    string customerCode = "";
                    using (var cmdCust = conn.CreateCommand())
                    {
                        cmdCust.Transaction = tx;
                        cmdCust.CommandText = "SELECT CUSTOMER_CODE FROM APPOINTMENT WHERE APPT_ID = @ID";
                        cmdCust.Parameters.Add(FirebirdDb.P("@ID", apptId, FbDbType.BigInt));
                        var v = cmdCust.ExecuteScalar();
                        customerCode = (v == null || v == DBNull.Value) ? "" : v.ToString().Trim();
                    }

                    int totalAffected = 0;
                    foreach (var svc in serviceCodes)
                    {
                        int affected = 0;
                        using (var cmdSo = conn.CreateCommand())
                        {
                            cmdSo.Transaction = tx;
                            cmdSo.CommandText = @"
UPDATE SL_SODTL d
SET PREV_CLAIMED = COALESCE(CLAIMED,0),
    CLAIMED = COALESCE(CLAIMED,0) + 1
WHERE d.ITEMCODE = @SVC
  AND d.DOCKEY IN (
      SELECT s.DOCKEY FROM SL_SO s
      WHERE s.CODE = @CUST
  )";
                            cmdSo.Parameters.Add(FirebirdDb.P("@SVC", svc, FbDbType.VarChar));
                            cmdSo.Parameters.Add(FirebirdDb.P("@CUST", customerCode, FbDbType.VarChar));
                            affected = cmdSo.ExecuteNonQuery();
                        }
                        totalAffected += affected;
                        TempData[$"ClaimedDebug_{svc}"] = $"SERVICE_CODE={svc}, CUSTOMER_CODE={customerCode}, RowsAffected={affected}";
                    }
                    TempData["ClaimedDebugTotal"] = $"Total SL_SODTL rows affected: {totalAffected}";
                }

                tx.Commit();

                TempData["Ok"] = "Signature saved. Appointment marked as FULFILLED.";
                return RedirectToAction("Sign", "Appointment", new { id = apptId });
            }
            catch (Exception ex)
            {
                TempData["Err"] = "Failed to save signature: " + ex.Message;
                return RedirectToAction("Sign", new { id = apptId });
            }
        }

        // =========================================================
        // ✅ SIGNATURE IMAGE (GET)
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

                byte[] bytes;
                using (var ms = new MemoryStream())
                {
                    long read;
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

        // =========================================================
        // ✅ Get services purchased by customer
        // GET: /Appointment/GetCustomerServices?customerCode=XXXX
        // =========================================================
        [HttpGet]
        public IActionResult GetCustomerServices(string customerCode)
        {
            customerCode = (customerCode ?? "").Trim();
            if (string.IsNullOrWhiteSpace(customerCode))
                return Json(new { ok = true, items = new List<object>() });

            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = @"
SELECT DISTINCT
    d.ITEMCODE,
    TRIM(COALESCE(d.DESCRIPTION, d.ITEMCODE)) AS ITEMDESC
FROM SL_SO s
JOIN SL_SODTL d ON d.DOCKEY = s.DOCKEY
WHERE s.CODE = @CUST
ORDER BY 2";

                cmd.Parameters.Add(FirebirdDb.P("@CUST", customerCode, FbDbType.VarChar));

                var items = new List<object>();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var code = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                    var desc = r.IsDBNull(1) ? "" : r.GetString(1).Trim();

                    if (!string.IsNullOrWhiteSpace(code))
                        items.Add(new { code, desc });
                }

                return Json(new { ok = true, items });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { ok = false, message = "Failed to load customer services.", detail = ex.Message });
            }
        }
    }
}
