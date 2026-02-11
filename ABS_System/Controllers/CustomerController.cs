using Microsoft.AspNetCore.Mvc;
using FirebirdSql.Data.FirebirdClient;
using YourApp.Data;
using YourApp.Models;
using System;
using System.Collections.Generic;
using System.Linq;

using System.IO;
using QuestPDF.Fluent;
using YourApp.Documents;

namespace ABS_System.Controllers
{
    public class CustomerController : Controller
    {
        private readonly FirebirdDb _db;

        public CustomerController(FirebirdDb db)
        {
            _db = db;
        }

        [HttpPost]
        public IActionResult PrintHistory(long apptId)
        {
            using var conn = _db.Open();
            // Find the log entry for this appointment creation (ACTION_TYPE = 'ADDED')
            long logId = 0;
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT FIRST 1 LOG_ID FROM APPOINTMENT_LOG WHERE APPT_ID = @APPTID AND ACTION_TYPE = 'ADDED' ORDER BY ACTION_TIME ASC";
                cmd.Parameters.Add(FirebirdDb.P("@APPTID", apptId, FbDbType.BigInt));
                var result = cmd.ExecuteScalar();
                if (result == null || result == DBNull.Value)
                    return NotFound("No log entry found for this appointment.");
                logId = Convert.ToInt64(result);
            }

            // Generate PDF
            var pdfDoc = AppointmentPdf.FromLogId(logId, conn);
            using var ms = new MemoryStream();
            pdfDoc.GeneratePdf(ms);
            ms.Position = 0;
            var fileName = $"Appointment_{apptId}.pdf";
            return File(ms.ToArray(), "application/pdf", fileName);
        }
        // GET: /Customer/History?customerCode=C0001
        public IActionResult History(string customerCode = "")
        {
            using var conn = _db.Open();

            // 1) Load dropdown customers
            var customers = LoadCustomers(conn);
            ViewBag.Customers = customers;


            var selectedInput = (customerCode ?? "").Trim();
            string selectedCode = "";
            if (!string.IsNullOrWhiteSpace(selectedInput))
            {
                // Try to find code by name (case-insensitive)
                var match = customers.FirstOrDefault(c => c.Name.Equals(selectedInput, StringComparison.OrdinalIgnoreCase));
                selectedCode = match?.CustomerCode ?? selectedInput; // fallback to input if not found
            }

            // 2) If nothing selected, return empty lists
            if (string.IsNullOrWhiteSpace(selectedCode))
            {
                ViewBag.Fulfilled = new List<Appointment>();
                ViewBag.Booked = new List<Appointment>();
                ViewBag.Cancelled = new List<Appointment>();
                return View();
            }

            // 3) Load appointments
            var appts = LoadAppointmentsByCustomer(conn, selectedCode);

            if (appts.Count == 0)
            {
                ViewBag.Fulfilled = new List<Appointment>();
                ViewBag.Booked = new List<Appointment>();
                ViewBag.Cancelled = new List<Appointment>();
                ViewBag.AppointmentLogIds = new Dictionary<long, long>();
                return View();
            }

            // 4) Bulk load names (fast)
            var customerNameMap = LoadCustomerNames(conn, appts.Select(a => a.CustomerCode));
            var agentNameMap = LoadAgentNames(conn, appts.Select(a => a.AgentCode));

            // 5) Attach logId for each appointment (ACTION_TYPE = 'ADDED')
            var logIdMap = new Dictionary<long, long>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = @"SELECT APPT_ID, MIN(LOG_ID) FROM APPOINTMENT_LOG WHERE ACTION_TYPE = 'ADDED' AND APPT_ID IN (" + string.Join(",", appts.Select(a => a.ApptId)) + ") GROUP BY APPT_ID";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var apptId = r.IsDBNull(0) ? 0 : r.GetInt64(0);
                    var logId = r.IsDBNull(1) ? 0 : r.GetInt64(1);
                    if (apptId > 0 && logId > 0) logIdMap[apptId] = logId;
                }
            }
            ViewBag.AppointmentLogIds = logIdMap;

            // 6) Apply names + group by status
            foreach (var a in appts)
            {
                a.CustomerName = customerNameMap.TryGetValue(a.CustomerCode, out var cn) ? cn : a.CustomerCode;
                a.AgentName = agentNameMap.TryGetValue(a.AgentCode, out var an) ? an : a.AgentCode;
            }

            ViewBag.Fulfilled = appts.Where(x => IsStatus(x.Status, "FULFILLED")).ToList();
            ViewBag.Cancelled = appts.Where(x => IsStatus(x.Status, "CANCELLED")).ToList();
            ViewBag.Booked = appts.Where(x => !IsStatus(x.Status, "FULFILLED") && !IsStatus(x.Status, "CANCELLED")).ToList();

            return View();
        }

        private static bool IsStatus(string? status, string match)
            => string.Equals((status ?? "").Trim(), match, StringComparison.OrdinalIgnoreCase);

        private List<AR_CUSTOMER> LoadCustomers(FbConnection conn)
        {
            var list = new List<AR_CUSTOMER>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CODE, COMPANYNAME FROM AR_CUSTOMER ORDER BY COMPANYNAME";

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new AR_CUSTOMER
                {
                    CustomerCode = r.IsDBNull(0) ? "" : r.GetString(0).Trim(),
                    Name = r.IsDBNull(1) ? "" : r.GetString(1).Trim()
                });
            }

            return list;
        }

        private List<Appointment> LoadAppointmentsByCustomer(FbConnection conn, string customerCode)
        {
            var list = new List<Appointment>();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"
SELECT 
  APPT_ID, CUSTOMER_CODE, AGENT_CODE, APPT_START, APPT_END, TITLE, STATUS, NOTES
FROM APPOINTMENT
WHERE CUSTOMER_CODE = @CODE
ORDER BY APPT_START DESC
";
            cmd.Parameters.Add(FirebirdDb.P("@CODE", customerCode, FbDbType.VarChar));

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var start = r.GetDateTime(3);

                list.Add(new Appointment
                {
                    ApptId = r.GetInt64(0),
                    CustomerCode = r.IsDBNull(1) ? "" : r.GetString(1).Trim(),
                    AgentCode = r.IsDBNull(2) ? "" : r.GetString(2).Trim(),
                    ApptStart = start,
                    ApptEnd = r.IsDBNull(4) ? start.AddMinutes(60) : r.GetDateTime(4),
                    Title = r.IsDBNull(5) ? null : r.GetString(5).Trim(),
                    Status = r.IsDBNull(6) ? "BOOKED" : r.GetString(6).Trim(),
                    Notes = r.IsDBNull(7) ? null : r.GetString(7)
                });
            }

            return list;
        }

        private Dictionary<string, string> LoadCustomerNames(FbConnection conn, IEnumerable<string> customerCodes)
        {
            var codes = customerCodes
                .Select(x => (x ?? "").Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (codes.Count == 0) return map;

            // Firebird doesn't support table-valued params, so build IN (...).
            // Safe here because we use parameters.
            var paramNames = codes.Select((c, i) => $"@C{i}").ToList();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT CODE, COMPANYNAME
FROM AR_CUSTOMER
WHERE CODE IN ({string.Join(",", paramNames)})
";

            for (int i = 0; i < codes.Count; i++)
                cmd.Parameters.Add(FirebirdDb.P(paramNames[i], codes[i], FbDbType.VarChar));

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var code = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                var name = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                if (code.Length > 0 && name.Length > 0) map[code] = name;
            }

            return map;
        }

        private Dictionary<string, string> LoadAgentNames(FbConnection conn, IEnumerable<string> agentCodes)
        {
            var codes = agentCodes
                .Select(x => (x ?? "").Trim())
                .Where(x => x.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (codes.Count == 0) return map;

            var paramNames = codes.Select((c, i) => $"@A{i}").ToList();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $@"
SELECT CODE, DESCRIPTION
FROM AGENT
WHERE CODE IN ({string.Join(",", paramNames)})
";

            for (int i = 0; i < codes.Count; i++)
                cmd.Parameters.Add(FirebirdDb.P(paramNames[i], codes[i], FbDbType.VarChar));

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var code = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                var name = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                if (code.Length > 0 && name.Length > 0) map[code] = name;
            }

            return map;
        }
    }
}
