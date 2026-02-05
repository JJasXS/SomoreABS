using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;

// ✅ add these (needed for FbDbType)
using FirebirdSql.Data.FirebirdClient;

namespace YourApp.Controllers
{
    [Authorize]
    public class CalendarController : Controller
    {
        // ✅ In-memory store (resets when app restarts)
        private static readonly List<CalendarEventVm> _events = new();
        private static int _nextId = 1;

        // =========================
        // Helpers: read login claims
        // =========================
        private string GetBranchNo()
        {
            return User?.Claims?.FirstOrDefault(c => c.Type == "BranchNo")?.Value?.Trim() ?? "";
        }

        private bool IsOffice()
        {
            return (User?.Claims?.FirstOrDefault(c => c.Type == "IsOffice")?.Value?.Trim() == "1");
        }

        // /Calendar?year=2026&month=2
        public IActionResult Index(int? year, int? month)
        {
            var now = DateTime.Today;
            int y = year ?? now.Year;
            int m = month ?? now.Month;

            var firstDay = new DateTime(y, m, 1);
            var lastDay = firstDay.AddMonths(1).AddDays(-1);

            // ✅ Logged-in user's scope
            string userBranchNo = GetBranchNo();
            bool isOffice = IsOffice();

            ViewBag.UserBranchNo = userBranchNo;
            ViewBag.IsOffice = isOffice;

            // ===== In-memory calendar events (your VM) =====
            var monthEvents = _events
                .Where(e => e.Date.Date >= firstDay.Date && e.Date.Date <= lastDay.Date)
                .OrderBy(e => e.Date)
                .ThenBy(e => e.Title)
                .ToList();

            // ===== Firebird APPOINTMENT list (filtered by branch if not office) =====
            var monthAppointments = new List<YourApp.Models.Appointment>();
            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var db = (YourApp.Data.FirebirdDb)scope.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = isOffice
                    ? @"
SELECT ap.APPT_ID, ap.CUSTOMER_CODE, ap.AGENT_CODE, ap.APPT_START, ap.TITLE, ap.NOTES, ap.STATUS
FROM APPOINTMENT ap
WHERE ap.APPT_START >= @START AND ap.APPT_START <= @END
ORDER BY ap.APPT_START"
                    : @"
SELECT ap.APPT_ID, ap.CUSTOMER_CODE, ap.AGENT_CODE, ap.APPT_START, ap.TITLE, ap.NOTES, ap.STATUS
FROM APPOINTMENT ap
JOIN AGENT a ON a.CODE = ap.AGENT_CODE
WHERE ap.APPT_START >= @START
  AND ap.APPT_START <= @END
  AND a.BRANCHNO = @BRANCHNO
ORDER BY ap.APPT_START";

                cmd.Parameters.Add(YourApp.Data.FirebirdDb.P("@START", firstDay, FbDbType.TimeStamp));
                cmd.Parameters.Add(YourApp.Data.FirebirdDb.P("@END", lastDay, FbDbType.TimeStamp));

                if (!isOffice)
                {
                    cmd.Parameters.Add(YourApp.Data.FirebirdDb.P("@BRANCHNO", userBranchNo, FbDbType.VarChar));
                }

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    monthAppointments.Add(new YourApp.Models.Appointment
                    {
                        ApptId = r.GetInt64(0),
                        CustomerCode = r.IsDBNull(1) ? "" : r.GetString(1).Trim(),
                        AgentCode = r.IsDBNull(2) ? "" : r.GetString(2).Trim(),
                        ApptStart = r.GetDateTime(3),
                        Title = r.IsDBNull(4) ? "" : r.GetString(4).Trim(),
                        Notes = r.IsDBNull(5) ? "" : r.GetString(5),
                        Status = r.IsDBNull(6) ? "BOOKED" : r.GetString(6).Trim()
                    });
                }
            }
            catch
            {
                // keep page working even if DB fails
            }

            // =========================================================
            // ✅ NEW: Build services per appointment for the whole month
            // ViewBag.ApptServices: Dictionary<long, List<string>>  (ApptId -> ServiceCodes)
            // ViewBag.ServiceNames: Dictionary<string, string>      (ServiceCode -> Description)
            // =========================================================
            var apptIds = monthAppointments.Select(a => a.ApptId).Distinct().ToList();

            var apptServices = new Dictionary<long, List<string>>();
            var serviceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                if (apptIds.Count > 0)
                {
                    using var scope2 = HttpContext.RequestServices.CreateScope();
                    var db2 = (YourApp.Data.FirebirdDb)scope2.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));
                    using var conn2 = db2.Open();

                    // 1) ApptId -> Service Codes (from APPT_DTL)
                    using (var cmdSvc = conn2.CreateCommand())
                    {
                        // Build IN list safely (@p0,@p1,...)
                        var paramNames = new List<string>();
                        for (int i = 0; i < apptIds.Count; i++)
                        {
                            var p = "@p" + i;
                            paramNames.Add(p);
                            cmdSvc.Parameters.Add(YourApp.Data.FirebirdDb.P(p, apptIds[i], FbDbType.BigInt));
                        }

                        cmdSvc.CommandText = $@"
SELECT APPT_ID, SERVICE_CODE
FROM APPT_DTL
WHERE APPT_ID IN ({string.Join(",", paramNames)})
ORDER BY APPT_ID, SERVICE_CODE";

                        using var rSvc = cmdSvc.ExecuteReader();
                        while (rSvc.Read())
                        {
                            var apptId = rSvc.GetInt64(0);
                            var code = rSvc.IsDBNull(1) ? "" : rSvc.GetString(1).Trim();

                            if (string.IsNullOrWhiteSpace(code)) continue;

                            if (!apptServices.TryGetValue(apptId, out var list))
                            {
                                list = new List<string>();
                                apptServices[apptId] = list;
                            }

                            // case-insensitive distinct
                            if (!list.Contains(code, StringComparer.OrdinalIgnoreCase))
                                list.Add(code);
                        }
                    }

                    // 2) Service Code -> Service Description (from ST_ITEM)
                    using (var cmdNames = conn2.CreateCommand())
                    {
                        cmdNames.CommandText = @"
SELECT CODE, DESCRIPTION
FROM ST_ITEM
WHERE STOCKGROUP = 'SERVICE'";

                        using var rNames = cmdNames.ExecuteReader();
                        while (rNames.Read())
                        {
                            var code = rNames.IsDBNull(0) ? "" : rNames.GetString(0).Trim();
                            var desc = rNames.IsDBNull(1) ? "" : rNames.GetString(1).Trim();

                            if (!string.IsNullOrWhiteSpace(code))
                                serviceNames[code] = desc;
                        }
                    }
                }
            }
            catch
            {
                // keep page working even if service lookup fails
            }

            ViewBag.ApptServices = apptServices;
            ViewBag.ServiceNames = serviceNames;

            // ===== Dictionaries (IMPORTANT: case-insensitive) =====
            var agentNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var agentBranchNos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var agentColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // Load agents (CODE -> DESCRIPTION, BRANCHNO -> Color)
            // ✅ Office: all agents
            // ✅ Non-office: only agents in their branch
            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var db = (YourApp.Data.FirebirdDb)scope.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = isOffice
                    ? "SELECT CODE, DESCRIPTION, BRANCHNO FROM AGENT"
                    : "SELECT CODE, DESCRIPTION, BRANCHNO FROM AGENT WHERE BRANCHNO = @BRANCHNO";

                if (!isOffice)
                {
                    cmd.Parameters.Add(YourApp.Data.FirebirdDb.P("@BRANCHNO", userBranchNo, FbDbType.VarChar));
                }

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var code = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                    var name = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                    var branchNo = r.IsDBNull(2) ? "" : r.GetString(2).Trim();

                    if (string.IsNullOrWhiteSpace(code)) continue;

                    agentNames[code] = name;
                    agentBranchNos[code] = branchNo;

                    // ✅ NEVER NULL color (default grey)
                    string branchColor = "#E8E8E8";

                    if (branchNo == "1") branchColor = "#ffb6c1";      // Light Pink (Office)
                    else if (branchNo == "2") branchColor = "#add8e6"; // Light Blue
                    else if (branchNo == "3") branchColor = "#90ee90"; // Light Green
                    else if (branchNo == "4") branchColor = "#ffd700"; // Gold

                    agentColors[code] = branchColor;
                }
            }
            catch { }

            // Load customers (CODE -> COMPANYNAME)
            var customerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var db = (YourApp.Data.FirebirdDb)scope.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = "SELECT CODE, COMPANYNAME FROM AR_CUSTOMER";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var code = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                    var name = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                    if (!string.IsNullOrWhiteSpace(code))
                        customerNames[code] = name;
                }
            }
            catch { }

            // ===== ViewBag =====
            ViewBag.Year = y;
            ViewBag.Month = m;
            ViewBag.FirstDay = firstDay;
            ViewBag.LastDay = lastDay;

            ViewBag.MonthAppointments = monthAppointments;
            ViewBag.AgentNames = agentNames;
            ViewBag.CustomerNames = customerNames;
            ViewBag.AgentBranchNos = agentBranchNos;
            ViewBag.AgentColors = agentColors;

            return View(monthEvents);
        }

        // GET: /Calendar/Create?date=2026-02-02
        [HttpGet]
        public IActionResult Create(DateTime? date)
        {
            var d = (date ?? DateTime.Today).Date;
            var model = new CalendarEventVm { Date = d };

            // Query ST_ITEM for service products (descriptions only)
            List<string> serviceDescriptions = new();
            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var db = (YourApp.Data.FirebirdDb)scope.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));
                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = @"SELECT DESCRIPTION FROM ST_ITEM WHERE STOCKGROUP = 'service' ORDER BY DESCRIPTION";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    serviceDescriptions.Add(r.IsDBNull(0) ? "" : r.GetString(0).Trim());
                }
            }
            catch { }

            ViewBag.ServiceItems = serviceDescriptions;
            return View(model);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Create(CalendarEventVm model)
        {
            if (!ModelState.IsValid) return View(model);

            model.Id = _nextId++;
            model.Date = model.Date.Date; // normalize
            _events.Add(model);

            return RedirectToAction(nameof(Index), new { year = model.Date.Year, month = model.Date.Month });
        }

        [HttpGet]
        public IActionResult Edit(int id)
        {
            var ev = _events.FirstOrDefault(x => x.Id == id);
            if (ev == null) return NotFound();

            return View(new CalendarEventVm
            {
                Id = ev.Id,
                Date = ev.Date,
                Title = ev.Title,
                Notes = ev.Notes
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Edit(int id, CalendarEventVm model)
        {
            if (id != model.Id) return BadRequest();
            if (!ModelState.IsValid) return View(model);

            var ev = _events.FirstOrDefault(x => x.Id == id);
            if (ev == null) return NotFound();

            ev.Title = model.Title;
            ev.Notes = model.Notes;
            ev.Date = model.Date.Date;

            return RedirectToAction(nameof(Index), new { year = ev.Date.Year, month = ev.Date.Month });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Delete(int id)
        {
            var ev = _events.FirstOrDefault(x => x.Id == id);
            if (ev == null) return NotFound();

            _events.Remove(ev);
            return RedirectToAction(nameof(Index));
        }
    }

    // ✅ ViewModel (UI only)
    public class CalendarEventVm
    {
        public int Id { get; set; }

        [System.ComponentModel.DataAnnotations.Required]
        public DateTime Date { get; set; } = DateTime.Today;

        [System.ComponentModel.DataAnnotations.Required]
        [System.ComponentModel.DataAnnotations.StringLength(120)]
        public string Title { get; set; } = "";

        [System.ComponentModel.DataAnnotations.StringLength(500)]
        public string? Notes { get; set; }
    }
}
