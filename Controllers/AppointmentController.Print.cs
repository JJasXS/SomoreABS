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
        // ✅ PRINT PDF (QuestPDF) by LOG_ID
        // GET: /Appointment/PrintPdfByLog/123
        // =========================================================
        [HttpGet]
        [Route("Appointment/PrintPdfByLog/{logId}")]
        public IActionResult PrintPdfByLog(long logId)
        {
            if (logId <= 0) return NotFound();
            try
            {
                using var conn = _db.Open();
                var doc = AppointmentPdf.FromLogId(logId, conn);
                var pdfBytes = doc.GeneratePdf();
                var filename = $"AppointmentLog_{logId}.pdf";
                return File(pdfBytes, "application/pdf", filename);
            }
            catch (Exception ex)
            {
                return StatusCode(500, "Failed to build PDF: " + ex.Message);
            }
        }
    }
}
