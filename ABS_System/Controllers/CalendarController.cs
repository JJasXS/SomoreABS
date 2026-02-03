using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;

namespace YourApp.Controllers
{
    public class CalendarController : Controller
    {
        // ✅ In-memory store (resets when app restarts)
        private static readonly List<CalendarEventVm> _events = new();
        private static int _nextId = 1;

        // /Calendar?year=2026&month=2
        public IActionResult Index(int? year, int? month)
        {
            var now = DateTime.Today;
            int y = year ?? now.Year;
            int m = month ?? now.Month;

            var firstDay = new DateTime(y, m, 1);
            var lastDay = firstDay.AddMonths(1).AddDays(-1);

            var monthEvents = _events
                .Where(e => e.Date.Date >= firstDay.Date && e.Date.Date <= lastDay.Date)
                .OrderBy(e => e.Date)
                .ThenBy(e => e.Title)
                .ToList();

            // Query appointments for this month
            List<YourApp.Models.Appointment> monthAppointments = new List<YourApp.Models.Appointment>();
            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var db = (YourApp.Data.FirebirdDb)scope.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));

                using var conn = db.Open();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = @"
SELECT APPT_ID, CUSTOMER_CODE, AGENT_CODE, APPT_START, TITLE, NOTES, STATUS
FROM APPOINTMENT
WHERE APPT_START >= @START AND APPT_START <= @END";

                cmd.Parameters.Add(YourApp.Data.FirebirdDb.P("@START", firstDay, FirebirdSql.Data.FirebirdClient.FbDbType.TimeStamp));
                cmd.Parameters.Add(YourApp.Data.FirebirdDb.P("@END", lastDay, FirebirdSql.Data.FirebirdClient.FbDbType.TimeStamp));

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    monthAppointments.Add(new YourApp.Models.Appointment
                    {
                        ApptId = r.GetInt64(0),
                        CustomerCode = r.IsDBNull(1) ? "" : r.GetString(1).Trim(),
                        AgentCode = r.IsDBNull(2) ? "" : r.GetString(2).Trim(),
                        ApptStart = r.GetDateTime(3),
                        Title = r.IsDBNull(4) ? null : r.GetString(4),
                        Notes = r.IsDBNull(5) ? null : r.GetString(5),
                        Status = r.IsDBNull(6) ? "NEW" : r.GetString(6)
                    });
                }
            }
            catch
            {
                // keep page working even if DB fails
            }

            // Fetch agent code-to-name and code-to-branchno mapping
            var agentDict = new Dictionary<string, string>();
            var agentBranchNos = new Dictionary<string, string>();
            var agentColors = new Dictionary<string, string>();
            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var db = (YourApp.Data.FirebirdDb)scope.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));

                using var conn = db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT CODE, DESCRIPTION, BRANCHNO FROM AGENT";

                using var r = cmd.ExecuteReader();
                while (r.Read())
                {
                    var code = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                    var name = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                    var branchNo = r.IsDBNull(2) ? "" : r.GetString(2).Trim();
                    if (!string.IsNullOrWhiteSpace(code))
                    {
                        agentDict[code] = name;
                        agentBranchNos[code] = branchNo;
                        // Assign color based on branchNo
                        string branchColor = null;
                        if (branchNo == "1") branchColor = "#ffb6c1"; // Light Pink
                        else if (branchNo == "2") branchColor = "#add8e6"; // Light Blue
                        else if (branchNo == "3") branchColor = "#90ee90"; // Light Green
                        else if (branchNo == "4") branchColor = "#ffd700"; // Gold
                        agentColors[code] = branchColor;
                    }
                }
            }
            catch { }

            // Fetch customer code-to-name mapping
            var customerDict = new Dictionary<string, string>();
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
                        customerDict[code] = name;
                }
            }
            catch { }

            ViewBag.Year = y;
            ViewBag.Month = m;
            ViewBag.FirstDay = firstDay;
            ViewBag.LastDay = lastDay;

            ViewBag.MonthAppointments = monthAppointments;
            ViewBag.AgentNames = agentDict;
            ViewBag.CustomerNames = customerDict;
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
