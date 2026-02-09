using System;
using System.IO;
using Microsoft.AspNetCore.Mvc;
using FirebirdSql.Data.FirebirdClient;
using QuestPDF.Fluent;
using YourApp.Data;
using YourApp.Models;
using YourApp.Documents;

namespace YourApp.Controllers
{
    public partial class AppointmentController : Controller
    {
        // =========================================================
        // ✅ PRINT PDF (QuestPDF)
        // GET: /Appointment/PrintPdf/5
        // =========================================================
        [HttpGet]
        public IActionResult PrintPdf(long id)
        {
            if (id <= 0) return NotFound();

            Appointment? appt = null;
            byte[]? sigBytes = null;
            string statementText = "";

            try
            {
                using var conn = _db.Open();

                // Load appointment
                using (var cmd = conn.CreateCommand())
                {
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

                if (appt == null) return NotFound();

                var custName = GetCustomerName(appt.CustomerCode, conn);
                var agentName = GetAgentName(appt.AgentCode, conn);
                SetIfPropertyExists(appt, "CustomerName", custName);
                SetIfPropertyExists(appt, "AgentName", agentName);

                // Load services with CLAIMED and PREV_CLAIMED
                appt.Services = new List<ApptDtl>();
                using (var cmd = conn.CreateCommand())
                {
                                        cmd.CommandText = @"
                    SELECT d.APPT_DTL_ID, d.APPT_ID, d.SERVICE_CODE,
                           COALESCE(s.QTY,0) AS QTY,
                           COALESCE(s.CLAIMED,0) AS CLAIMED, COALESCE(s.PREV_CLAIMED,0) AS PREV_CLAIMED
                    FROM APPT_DTL d
                    LEFT JOIN SL_SODTL s ON s.ITEMCODE = d.SERVICE_CODE
                        AND s.DOCKEY IN (SELECT DOCKEY FROM SL_SO WHERE CODE = @CUST)
                    WHERE d.APPT_ID = @APPTID
                    ORDER BY d.SERVICE_CODE";
                    cmd.Parameters.Add(FirebirdDb.P("@APPTID", appt.ApptId, FbDbType.BigInt));
                    cmd.Parameters.Add(FirebirdDb.P("@CUST", appt.CustomerCode, FbDbType.VarChar));
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var dtl = new ApptDtl
                        {
                            Id = r.GetInt64(0),
                            ApptId = r.GetInt64(1),
                            ServiceCode = r.IsDBNull(2) ? "" : r.GetString(2).Trim(),
                            Qty = r.IsDBNull(3) ? 0 : Convert.ToInt32(r.GetValue(3)),
                            Claimed = r.IsDBNull(4) ? 0 : Convert.ToInt32(r.GetValue(4)),
                            PrevClaimed = r.IsDBNull(5) ? 0 : Convert.ToInt32(r.GetValue(5))
                        };
                        appt.Services.Add(dtl);
                    }
                }

                using (var cmdSig = conn.CreateCommand())
                {
                    cmdSig.CommandText = @"
SELECT SIGNATURE_PNG, REMARKS
FROM APPT_SIGNATURE
WHERE APPT_ID = @ID";
                    cmdSig.Parameters.Add(FirebirdDb.P("@ID", id, FbDbType.BigInt));
                    using var r2 = cmdSig.ExecuteReader();
                    if (r2.Read())
                    {
                        if (!r2.IsDBNull(0))
                        {
                            using var ms = new MemoryStream();
                            long read;
                            long offset = 0;
                            var buffer = new byte[8192];
                            while ((read = r2.GetBytes(0, offset, buffer, 0, buffer.Length)) > 0)
                            {
                                ms.Write(buffer, 0, (int)read);
                                offset += read;
                            }
                            sigBytes = ms.ToArray();
                        }
                        if (!r2.IsDBNull(1))
                            statementText = r2.GetString(1);
                    }
                }
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Failed to build PDF: " + ex.Message);
            }

            if (string.IsNullOrWhiteSpace(statementText))
            {
                statementText =
                    "I hereby confirm and acknowledge that the appointment details shown are correct. " +
                    "I agree that this e-signature is valid and may be used as proof of acknowledgement.";
            }

            appt!.Notes = statementText;

            var doc = new AppointmentPdf(appt, sigBytes);
            var pdfBytes = doc.GeneratePdf();

            var filename = $"Appointment_{appt.ApptId}.pdf";
            return File(pdfBytes, "application/pdf", filename);
        }
    }
}
