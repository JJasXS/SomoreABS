using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Processing;
using SixLabors.ImageSharp.Formats.Png;
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
        private const int MAX_SIGNATURE_DATAURL_CHARS = 1_500_000; // ~1.5MB text payload
        private const int MAX_SIGNATURE_BYTES = 1_000_000;          // 1MB PNG max
        private const int MAX_SIGNATURE_PIXELS = 4_000_000;         // DoS guard

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

            // Populate Services for this appointment
            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
SELECT d.APPT_ID, d.SERVICE_CODE, COALESCE(s.QTY, 0) AS QTY, COALESCE(s.UDF_CLAIMED, 0) AS CLAIMED, COALESCE(s.UDF_PREV_CLAIMED, 0) AS PREV_CLAIMED, s.DESCRIPTION
FROM APPT_DTL d
LEFT JOIN SL_SODTL s ON s.ITEMCODE = d.SERVICE_CODE
    AND s.DOCKEY IN (SELECT so.DOCKEY FROM SL_SO so WHERE so.CODE = @CUST)
WHERE d.APPT_ID = @APPTID
";
                cmd.Parameters.Add(FirebirdDb.P("@APPTID", appt.ApptId, FbDbType.BigInt));
                cmd.Parameters.Add(FirebirdDb.P("@CUST", appt.CustomerCode ?? string.Empty, FbDbType.VarChar));
                using var r = cmd.ExecuteReader();
                var services = new List<ApptDtl>();
                while (r.Read())
                {
                    services.Add(new ApptDtl
                    {
                        ApptId = r.IsDBNull(0) ? 0 : r.GetInt64(0),
                        ServiceCode = r.IsDBNull(1) ? "" : r.GetString(1),
                        Qty = r.IsDBNull(2) ? 0 : r.GetInt32(2),
                        Claimed = r.IsDBNull(3) ? 0 : r.GetInt32(3),
                        PrevClaimed = r.IsDBNull(4) ? 0 : r.GetInt32(4),
                        Description = r.IsDBNull(5) ? "" : r.GetString(5)
                    });
                }
                appt.Services = services;
            }
            catch { appt.Services = new List<ApptDtl>(); }

            ViewBag.HasSignature = HasSignature(id);
            ViewBag.SignatureUrl = Url.Action("SignatureImage", "Appointment", new { id });

            ViewBag.DefaultSignedBy = GetCustomerCompanyName(appt.CustomerCode);

            ViewBag.StatementText =
                "I hereby confirm and acknowledge that the appointment details shown are correct." +
                " I agree that this e-signature is valid and may be used as proof of acknowledgement.";

            // Set ViewBag.LogId for print button (latest LOG_ID for this APPT_ID)
            try
            {
                using var conn = _db.Open();
                ViewBag.LogId = YourApp.Data.AppointmentLogHelper.GetLatestLogIdForAppointment(id, conn);
            }
            catch { ViewBag.LogId = null; }

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
            if (signatureDataUrl.Length > MAX_SIGNATURE_DATAURL_CHARS)
            {
                TempData["Err"] = "Signature is too large.";
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
                if (pngBytes.Length > MAX_SIGNATURE_BYTES)
                {
                    TempData["Err"] = "Signature exceeds maximum allowed size.";
                    return RedirectToAction("Sign", new { id = apptId });
                }

                using (var inputStream = new MemoryStream(pngBytes))
                using (var image = SixLabors.ImageSharp.Image.Load(inputStream))
                {
                    if ((long)image.Width * image.Height > MAX_SIGNATURE_PIXELS)
                    {
                        TempData["Err"] = "Signature dimensions are too large.";
                        return RedirectToAction("Sign", new { id = apptId });
                    }

                    if (image.Width > 600)
                    {
                        int newWidth = 600;
                        int newHeight = (int)(image.Height * (600.0 / image.Width));
                        image.Mutate(x => x.Resize(newWidth, newHeight));
                    }
                    using (var outputStream = new MemoryStream())
                    {
                        image.Save(outputStream, new PngEncoder());
                        pngBytes = outputStream.ToArray();
                    }
                }
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

                // Log SO_QTY, CLAIMED, PREV_CLAIMED, CURR_CLAIMED for each service in APPT_DTL
                using (var cmdDtl = conn.CreateCommand())
                {
                    cmdDtl.Transaction = tx;
                    cmdDtl.CommandText = @"SELECT SERVICE_CODE FROM APPT_DTL WHERE APPT_ID = @APPTID";
                    cmdDtl.Parameters.Add(FirebirdDb.P("@APPTID", apptId, FbDbType.BigInt));
                    using var rDtl = cmdDtl.ExecuteReader();
                    var serviceCodes = new List<string>();
                    while (rDtl.Read())
                    {
                        if (!rDtl.IsDBNull(0))
                            serviceCodes.Add(rDtl.GetString(0).Trim());
                    }
                    rDtl.Close();

                    foreach (var svc in serviceCodes)
                    {
                        int soQty = 0, claimed = 0, prevClaimed = 0, currClaimed = 0;
                        using (var cmdSodtl = conn.CreateCommand())
                        {
                            cmdSodtl.Transaction = tx;
                            cmdSodtl.CommandText = @"SELECT QTY, COALESCE(UDF_CLAIMED,0), COALESCE(UDF_PREV_CLAIMED,0) FROM SL_SODTL WHERE ITEMCODE = @SVC AND DOCKEY IN (SELECT DOCKEY FROM SL_SO WHERE CODE = @CUST) ROWS 1";
                            cmdSodtl.Parameters.Add(FirebirdDb.P("@SVC", svc, FbDbType.VarChar));
                            // Get customer code from APPOINTMENT
                            string customerCode = "";
                            using (var cmdGetCust = conn.CreateCommand())
                            {
                                cmdGetCust.Transaction = tx;
                                cmdGetCust.CommandText = "SELECT CUSTOMER_CODE FROM APPOINTMENT WHERE APPT_ID = @ID";
                                cmdGetCust.Parameters.Add(FirebirdDb.P("@ID", apptId, FbDbType.BigInt));
                                var v = cmdGetCust.ExecuteScalar();
                                customerCode = (v == null || v == DBNull.Value) ? "" : v.ToString()?.Trim() ?? "";
                            }
                            cmdSodtl.Parameters.Add(FirebirdDb.P("@CUST", customerCode, FbDbType.VarChar));
                            using var rSodtl = cmdSodtl.ExecuteReader();
                            if (rSodtl.Read())
                            {
                                soQty = rSodtl.IsDBNull(0) ? 0 : rSodtl.GetInt32(0);
                                claimed = rSodtl.IsDBNull(1) ? 0 : rSodtl.GetInt32(1);
                                prevClaimed = rSodtl.IsDBNull(2) ? 0 : rSodtl.GetInt32(2);
                                currClaimed = claimed + prevClaimed;
                            }
                        }
                        // Disabled: Do not log e-signature to APPOINTMENT_LOG as per latest requirements.
                        // using (var cmdLog = conn.CreateCommand())
                        // {
                        //     cmdLog.Transaction = tx;
                        //     cmdLog.CommandText = @"INSERT INTO APPOINTMENT_LOG (APPT_ID, ACTION_TYPE, ACTION_TIME, USERNAME, DETAILS, SO_QTY, CLAIMED, PREV_CLAIMED, CURR_CLAIMED, SERVICE_CODE) VALUES (@APPTID, 'E_SIGNATURE_SUBMIT', CURRENT_TIMESTAMP, @USER, @DETAILS, @SOQTY, @CLAIMED, @PREV, @CURR, @SVC)";
                        //     cmdLog.Parameters.Add(FirebirdDb.P("@APPTID", apptId, FbDbType.BigInt));
                        //     cmdLog.Parameters.Add(FirebirdDb.P("@USER", User?.Identity?.Name ?? "SYSTEM", FbDbType.VarChar));
                        //     cmdLog.Parameters.Add(FirebirdDb.P("@DETAILS", "E-signature submitted.", FbDbType.VarChar));
                        //     cmdLog.Parameters.Add(FirebirdDb.P("@SOQTY", soQty, FbDbType.Integer));
                        //     cmdLog.Parameters.Add(FirebirdDb.P("@CLAIMED", claimed, FbDbType.Integer));
                        //     cmdLog.Parameters.Add(FirebirdDb.P("@PREV", prevClaimed, FbDbType.Integer));
                        //     cmdLog.Parameters.Add(FirebirdDb.P("@CURR", currClaimed, FbDbType.Integer));
                        //     cmdLog.Parameters.Add(FirebirdDb.P("@SVC", svc, FbDbType.VarChar));
                        //     cmdLog.ExecuteNonQuery();
                        // }
                    }
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
            // Accept optional apptId for virtual calculation
            string? apptIdStr = Request.Query["apptId"]; // nullable
            long apptId = 0;
            if (!string.IsNullOrWhiteSpace(apptIdStr ?? "")) long.TryParse(apptIdStr, out apptId);

            if (string.IsNullOrWhiteSpace(customerCode))
                return Json(new { ok = true, items = new List<object>() });

            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"
                SELECT DISTINCT
                    d.ITEMCODE,
                    TRIM(COALESCE(d.DESCRIPTION, d.ITEMCODE)) AS ITEMDESC,
                    COALESCE(d.QTY,0) AS QTY,
                    COALESCE(d.UDF_CLAIMED,0) AS UDF_CLAIMED,
                    COALESCE(d.UDF_PREV_CLAIMED,0) AS UDF_PREV_CLAIMED
                FROM SL_SO s
                JOIN SL_SODTL d ON d.DOCKEY = s.DOCKEY
                WHERE s.CODE = @CUST
                ORDER BY 2";

                cmd.Parameters.Add(FirebirdDb.P("@CUST", customerCode, FbDbType.VarChar));

                // Build a dictionary of APPT_DTL QTY for this apptId (if provided)
                var apptDtlDict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                if (apptId > 0)
                {
                    using var cmdDtl = conn.CreateCommand();
                    cmdDtl.CommandText = "SELECT SERVICE_CODE, QTY FROM APPT_DTL WHERE APPT_ID = @id";
                    cmdDtl.Parameters.Add(FirebirdDb.P("@id", apptId, FbDbType.BigInt));
                    using var rDtl = cmdDtl.ExecuteReader();
                    while (rDtl.Read())
                    {
                        var code = rDtl.IsDBNull(0) ? "" : rDtl.GetString(0).Trim();
                        var qty = rDtl.IsDBNull(1) ? 0 : rDtl.GetInt32(1);
                        if (!string.IsNullOrEmpty(code))
                            apptDtlDict[code] = qty;
                    }
                }

                var items = new List<object>();
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var code = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                    var desc = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                    var qty = r.IsDBNull(2) ? 0 : r.GetInt32(2);
                    var claimed = r.IsDBNull(3) ? 0 : r.GetInt32(3);
                    var prevClaimed = r.IsDBNull(4) ? 0 : r.GetInt32(4);
                    int apptQty = apptDtlDict.TryGetValue(code, out var q) ? q : 0;
                    // Virtual balance: (qty - claimed - prevClaimed) + apptQty
                    var balance = (qty - (claimed + prevClaimed)) + apptQty;
                    if (balance > 0 || apptQty > 0)
                    {
                        items.Add(new { code, desc, balance });
                    }
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
