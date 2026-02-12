using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Collections.Generic;
using System.Linq;
using FirebirdSql.Data.FirebirdClient;

namespace YourApp.Controllers
{
    [Authorize]
    public class CalendarController : Controller
    {
        // ✅ In-memory calendar events (resets when app restarts)
        private static readonly List<CalendarEventVm> _events = new();
        private static int _nextId = 1;

        // =========================
        // Login Claim Helpers
        // =========================
        private string GetBranchNo()
            => User?.Claims?.FirstOrDefault(c => c.Type == "UDF_BRANCH")?.Value?.Trim() ?? "";

        private bool IsOffice()
            => (User?.Claims?.FirstOrDefault(c => c.Type == "IsOffice")?.Value?.Trim() == "1");

        // =========================
        // Main Calendar Page
        // /Calendar?year=2026&month=2
        // =========================
        public IActionResult Index(int? year, int? month)
        {
            var now = DateTime.Today;
            int y = year ?? now.Year;
            int m = month ?? now.Month;

            var firstDay = new DateTime(y, m, 1);
            var lastDay = new DateTime(y, m, DateTime.DaysInMonth(y, m), 23, 59, 59);

            // ✅ logged-in scope
            string userBranchNo = GetBranchNo();
            bool canSeeAllBranches = userBranchNo == "0";

            // Get selected branches from query string (for server-side filter)
            var selectedBranches = Request.Query["branches"].ToArray();
            List<string> selectedBranchList = selectedBranches.Length > 0 ? selectedBranches.ToList() : null;
            ViewBag.SelectedBranches = selectedBranchList;

            ViewBag.UserBranchNo = userBranchNo;
            ViewBag.CanSeeAllBranches = canSeeAllBranches;

            // ===== In-memory events (your VM list) =====
            var monthEvents = _events
                .Where(e => e.Date.Date >= firstDay.Date && e.Date.Date <= lastDay.Date)
                .OrderBy(e => e.Date)
                .ThenBy(e => e.Title)
                .ToList();

            // ===== DB: appointments in month =====
            var monthAppointments = LoadMonthAppointmentsFiltered(firstDay, lastDay, canSeeAllBranches, userBranchNo, selectedBranchList);

            // ===== DB: appt services for month + service names =====
            BuildApptServicesAndNames(monthAppointments, out var apptServices, out var serviceNames);
            ViewBag.ApptServices = apptServices;
            ViewBag.ServiceNames = serviceNames;

            // ===== DB: agent dictionaries + colors =====
            BuildAgentDictionaries(canSeeAllBranches, userBranchNo,
                out var agentNames,
                out var agentBranchNos,
                out var agentColors);

            // ===== DB: customer dictionary =====
            var customerNames = LoadCustomerNames();

            // ===== ViewBags for Calendar.cshtml =====
            ViewBag.Year = y;
            ViewBag.Month = m;
            ViewBag.FirstDay = firstDay;
            ViewBag.LastDay = lastDay;

            ViewBag.MonthAppointments = monthAppointments;
            ViewBag.AgentNames = agentNames;
            ViewBag.CustomerNames = customerNames;
            ViewBag.AgentBranchNos = agentBranchNos;
            ViewBag.AgentColors = agentColors;

            var birthdays = new List<CustomerBirthdayVm>();
            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var dbObj = scope.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));
                if (dbObj is YourApp.Data.FirebirdDb db)
                {
                    using var conn = db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT COMPANYNAME, UDF_DOB FROM AR_CUSTOMER WHERE UDF_DOB IS NOT NULL";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var name = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                        var dobStr = r.IsDBNull(1) ? null : r.GetString(1).Trim();
                        if (DateTime.TryParse(dobStr, out var dob))
                        {
                            // Only show birthdays in the selected month
                            if (dob.Month == month)
                            {
                                birthdays.Add(new CustomerBirthdayVm { Name = name, Birthday = dob });
                            }
                        }
                    }
                }
            }
            catch { }
            ViewBag.CustomerBirthdays = birthdays;

            return View(monthEvents);
        }
        // New: LoadMonthAppointmentsFiltered for multi-branch filter
        private List<YourApp.Models.Appointment> LoadMonthAppointmentsFiltered(
            DateTime start,
            DateTime end,
            bool canSeeAllBranches,
            string userBranchNo,
            List<string> selectedBranches)
        {
            var list = new List<YourApp.Models.Appointment>();
            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var dbObj = scope.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));
                if (dbObj is YourApp.Data.FirebirdDb db)
                {
                    using var conn = db.Open();
                    using var cmd = conn.CreateCommand();
                    if (canSeeAllBranches && selectedBranches != null && selectedBranches.Count > 0)
                    {
                        // Filter by selected branches (for office)
                        var branchParams = new List<string>();
                        for (int i = 0; i < selectedBranches.Count; i++)
                        {
                            var pname = "@branch" + i;
                            branchParams.Add(pname);
                            cmd.Parameters.Add(YourApp.Data.FirebirdDb.P(pname, selectedBranches[i], FbDbType.VarChar));
                        }
                        cmd.CommandText = $@"SELECT ap.APPT_ID, ap.CUSTOMER_CODE, ap.AGENT_CODE, ap.APPT_START, ap.TITLE, ap.NOTES, ap.STATUS FROM APPOINTMENT ap JOIN AGENT a ON a.CODE = ap.AGENT_CODE WHERE a.UDF_BRANCH IN ({string.Join(",", branchParams)}) ORDER BY ap.APPT_START";
                    }
                    else if (userBranchNo == "0")
                    {
                        // Office, no filter
                        cmd.CommandText = @"SELECT ap.APPT_ID, ap.CUSTOMER_CODE, ap.AGENT_CODE, ap.APPT_START, ap.TITLE, ap.NOTES, ap.STATUS FROM APPOINTMENT ap ORDER BY ap.APPT_START";
                    }
                    else
                    {
                        // Normal user, single branch
                        cmd.CommandText = @"SELECT ap.APPT_ID, ap.CUSTOMER_CODE, ap.AGENT_CODE, ap.APPT_START, ap.TITLE, ap.NOTES, ap.STATUS FROM APPOINTMENT ap JOIN AGENT a ON a.CODE = ap.AGENT_CODE WHERE a.UDF_BRANCH = @UDF_BRANCH ORDER BY ap.APPT_START";
                        cmd.Parameters.Add(YourApp.Data.FirebirdDb.P("@UDF_BRANCH", userBranchNo, FbDbType.VarChar));
                    }
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        list.Add(new YourApp.Models.Appointment
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
            }
            catch
            {
                // keep page working even if DB fails
            }
            return list;
        }

        // =========================================================
        // DB: Load appointments for the month (branch-filtered)
        // =========================================================
        private List<YourApp.Models.Appointment> LoadMonthAppointments(
            DateTime start,
            DateTime end,
            bool canSeeAllBranches,
            string userBranchNo)
        {
            var list = new List<YourApp.Models.Appointment>();

            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var dbObj = scope.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));
                if (dbObj is YourApp.Data.FirebirdDb db)
                {
                    using var conn = db.Open();
                    using var cmd = conn.CreateCommand();
                    if (userBranchNo == "0")
                    {
                        cmd.CommandText = @"SELECT ap.APPT_ID, ap.CUSTOMER_CODE, ap.AGENT_CODE, ap.APPT_START, ap.TITLE, ap.NOTES, ap.STATUS FROM APPOINTMENT ap ORDER BY ap.APPT_START";
                    }
                    else
                    {
                        cmd.CommandText = @"SELECT ap.APPT_ID, ap.CUSTOMER_CODE, ap.AGENT_CODE, ap.APPT_START, ap.TITLE, ap.NOTES, ap.STATUS FROM APPOINTMENT ap JOIN AGENT a ON a.CODE = ap.AGENT_CODE WHERE a.UDF_BRANCH = @UDF_BRANCH ORDER BY ap.APPT_START";
                        cmd.Parameters.Add(YourApp.Data.FirebirdDb.P("@UDF_BRANCH", userBranchNo, FbDbType.VarChar));
                    }
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        list.Add(new YourApp.Models.Appointment
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
            }
            catch
            {
                // keep page working even if DB fails
            }

            return list;
        }

        // =========================================================
        // DB: Build ApptServices + ServiceNames
        // =========================================================
        private void BuildApptServicesAndNames(
            List<YourApp.Models.Appointment> monthAppointments,
            out Dictionary<long, List<string>> apptServices,
            out Dictionary<string, string> serviceNames)
        {
            apptServices = new Dictionary<long, List<string>>();
            serviceNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var apptIds = monthAppointments.Select(a => a.ApptId).Distinct().ToList();
            if (apptIds.Count == 0) return;

            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var dbObj = scope.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));
                if (dbObj is YourApp.Data.FirebirdDb db)
                {
                    using var conn = db.Open();
                    // 1) ApptId -> Service Codes (APPT_DTL)
                    using (var cmdSvc = conn.CreateCommand())
                    {
                        var paramNames = new List<string>();
                        for (int i = 0; i < apptIds.Count; i++)
                        {
                            var p = "@p" + i;
                            paramNames.Add(p);
                            cmdSvc.Parameters.Add(YourApp.Data.FirebirdDb.P(p, apptIds[i], FbDbType.BigInt));
                        }
                        cmdSvc.CommandText = $@"SELECT APPT_ID, SERVICE_CODE FROM APPT_DTL WHERE APPT_ID IN ({string.Join(",", paramNames)}) ORDER BY APPT_ID, SERVICE_CODE";
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
                            if (!list.Contains(code, StringComparer.OrdinalIgnoreCase))
                                list.Add(code);
                        }
                    }
                    // 2) Service Code -> Description (ST_ITEM)
                    using (var cmdNames = conn.CreateCommand())
                    {
                        cmdNames.CommandText = @"SELECT CODE, DESCRIPTION FROM ST_ITEM WHERE STOCKGROUP = 'SERVICE'";
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
        }

        // =========================================================
        // DB: Agent names + branch numbers + colors
        // =========================================================
        private void BuildAgentDictionaries(
            bool canSeeAllBranches,
            string userBranchNo,
            out Dictionary<string, string> agentNames,
            out Dictionary<string, string> agentBranchNos,
            out Dictionary<string, string> agentColors)
        {
            agentNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            agentBranchNos = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            agentColors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var dbObj = scope.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));
                if (dbObj is YourApp.Data.FirebirdDb db)
                {
                    using var conn = db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = canSeeAllBranches
                        ? "SELECT CODE, DESCRIPTION, UDF_BRANCH FROM AGENT"
                        : "SELECT CODE, DESCRIPTION, UDF_BRANCH FROM AGENT WHERE UDF_BRANCH = @UDF_BRANCH";
                    if (!canSeeAllBranches)
                        cmd.Parameters.Add(YourApp.Data.FirebirdDb.P("@UDF_BRANCH", userBranchNo, FbDbType.VarChar));
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
                    {
                        var code = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                        var name = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                        var branchNo = r.IsDBNull(2) ? "" : r.GetString(2).Trim();
                        if (string.IsNullOrWhiteSpace(code)) continue;
                        agentNames[code] = name;
                        agentBranchNos[code] = branchNo;
                        agentColors[code] = GetBranchColor(branchNo);
                    }
                }
            }
            catch { }
        }

        private string GetBranchColor(string branchNo)
        {
            // ✅ NEVER NULL color (default grey)
            string branchColor = "#E8E8E8";
            switch (branchNo)
            {
                case "0": branchColor = "#aafcff"; break;      // Light Pink
                case "1": branchColor = "#ffb6c1"; break;      // Light Pink
                case "2": branchColor = "#add8e6"; break;      // Light Blue
                case "3": branchColor = "#90ee90"; break;      // Light Green
                case "4": branchColor = "#ffd700"; break;      // Gold
                case "5": branchColor = "#ffa07a"; break;      // Light Salmon
                case "6": branchColor = "#20b2aa"; break;      // Light Sea Green
                case "7": branchColor = "#9370db"; break;      // Medium Purple
                case "8": branchColor = "#f08080"; break;      // Light Coral
                case "9": branchColor = "#b0e0e6"; break;      // Powder Blue
                case "10": branchColor = "#ff69b4"; break;     // Hot Pink
                case "11": branchColor = "#87cefa"; break;     // Light Sky Blue
                case "12": branchColor = "#98fb98"; break;     // Pale Green
                case "13": branchColor = "#ffe4b5"; break;     // Moccasin
                case "14": branchColor = "#d8bfd8"; break;     // Thistle
                case "15": branchColor = "#afeeee"; break;     // Pale Turquoise
                case "16": branchColor = "#ffdead"; break;     // Navajo White
                case "17": branchColor = "#e9967a"; break;     // Dark Salmon
                case "18": branchColor = "#bdb76b"; break;     // Dark Khaki
                case "19": branchColor = "#8fbc8f"; break;     // Dark Sea Green
                case "20": branchColor = "#dda0dd"; break;     // Plum
                default: branchColor = "#ff9c9c"; break;         // Default Grey
            }
            return branchColor;
        }

        // =========================================================
        // DB: Customer CODE -> COMPANYNAME
        // =========================================================
        private Dictionary<string, string> LoadCustomerNames()
        {
            var customerNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var dbObj = scope.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));
                if (dbObj is YourApp.Data.FirebirdDb db)
                {
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
            }
            catch { }

            return customerNames;
        }

        // =========================
        // In-memory Calendar Events
        // =========================

        // GET: /Calendar/Create?date=2026-02-02
        [HttpGet]
        public IActionResult Create(DateTime? date)
        {
            var d = (date ?? DateTime.Today).Date;
            var model = new CalendarEventVm { Date = d };

            // (Kept from your code) query service descriptions if you still need it
            var serviceDescriptions = new List<string>();

            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var dbObj = scope.ServiceProvider.GetService(typeof(YourApp.Data.FirebirdDb));
                if (dbObj is YourApp.Data.FirebirdDb db)
                {
                    using var conn = db.Open();
                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"SELECT DESCRIPTION FROM ST_ITEM WHERE STOCKGROUP = 'SERVICE' ORDER BY DESCRIPTION";
                    using var r = cmd.ExecuteReader();
                    while (r.Read())
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
            model.Date = model.Date.Date;
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

    public class CustomerBirthdayVm
    {
        public string? Name { get; set; }
        public DateTime? Birthday { get; set; }
    }
}
