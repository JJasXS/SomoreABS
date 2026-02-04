using Microsoft.AspNetCore.Mvc;
using YourApp.Models;
using System;
using System.Collections.Concurrent;
using System.Net.Mail;
using System.Net;
using YourApp.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using System.Security.Claims;


namespace YourApp.Controllers
{
    public class AccountController : Controller
    {
        private static readonly ConcurrentDictionary<string, string> OtpStore = new();
        private readonly FirebirdDb _db;

        public AccountController(FirebirdDb db)
        {
            _db = db;
        }

        [HttpGet]
        public IActionResult Login()
        {
            return View(new AgentLoginViewModel());
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Login(AgentLoginViewModel model)
        {
            if (!ModelState.IsValid) return View(model);

            // Check agent exists by email only
            string? agentCode = null;
            using (var conn = _db.Open())
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "SELECT CODE FROM AGENT WHERE EMAIL = @EMAIL";
                cmd.Parameters.Add(FirebirdDb.P("@EMAIL", model.Email.Trim(), FirebirdSql.Data.FirebirdClient.FbDbType.VarChar));
                using var r = cmd.ExecuteReader();
                if (r.Read())
                {
                    agentCode = r.IsDBNull(0) ? null : r.GetString(0);
                }
            }
            if (string.IsNullOrEmpty(agentCode))
            {
                ModelState.AddModelError("Email", "Email not found. Please contact administrator.");
                return View(model);
            }

            // Generate OTP
            var otp = new Random().Next(100000, 999999).ToString();
            OtpStore[agentCode] = otp;

            // Send OTP to email (simulate SMS by email for now)
            SendOtpEmail(model.Email, otp);
            // [DEBUG] Display OTP in terminal
            System.Console.WriteLine($"[DEBUG] OTP for {model.Email}: {otp}");

            TempData["AgentCode"] = agentCode;
            TempData["Email"] = model.Email;
            return RedirectToAction("VerifyOtp");
        }

        [HttpGet]
        public IActionResult VerifyOtp()
        {
            var agentCode = TempData["AgentCode"] as string;
            var email = TempData["Email"] as string;
            if (string.IsNullOrEmpty(agentCode) || string.IsNullOrEmpty(email)) return RedirectToAction("Login");
            TempData["AgentCode"] = agentCode;
            TempData["Email"] = email;
            return View(new OtpViewModel { AgentCode = agentCode });
        }

        [HttpPost]
[ValidateAntiForgeryToken]
public IActionResult VerifyOtp(OtpViewModel model)
{
    if (!ModelState.IsValid) return View(model);

    // ✅ 1) OTP check
    if (!OtpStore.TryGetValue(model.AgentCode, out var otp) || otp != model.Otp)
    {
        ModelState.AddModelError("Otp", "Invalid OTP.");
        return View(model);
    }

    // ✅ 2) OTP success → remove OTP
    OtpStore.TryRemove(model.AgentCode, out _);

    // ✅ 3) Load Email + BranchNo from AGENT for claims
    string email = "";
    string branchNo = "";

    try
    {
        using (var conn = _db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT EMAIL, BRANCHNO FROM AGENT WHERE CODE = @CODE";
            cmd.Parameters.Add(FirebirdDb.P("@CODE", model.AgentCode.Trim(),
                FirebirdSql.Data.FirebirdClient.FbDbType.VarChar));

            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                email = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                branchNo = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
            }
        }
    }
    catch
    {
        // keep going; will fallback below
    }

    // fallback: if email empty, try TempData email (from Login step)
    if (string.IsNullOrWhiteSpace(email))
    {
        email = (TempData["Email"] as string ?? "").Trim();
    }

    bool isOffice = branchNo == "1";

    // ✅ 4) Build claims (this is what CalendarController will read)
    var claims = new List<Claim>
    {
        new Claim(ClaimTypes.Name, email),   // simple default
        new Claim(ClaimTypes.Email, email),
        new Claim("AgentCode", model.AgentCode.Trim()),
        new Claim("BranchNo", branchNo),
        new Claim("IsOffice", isOffice ? "1" : "0")
    };

    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);

    // ✅ 5) Sign in (cookie)
    HttpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        principal,
        new AuthenticationProperties
        {
            IsPersistent = true,
            ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
        }
    ).GetAwaiter().GetResult();

    return RedirectToAction("Index", "Calendar");
}


        private void SendOtpEmail(string email, string otp)
        {
            // For demo: send via SMTP (configure in appsettings.json)
            // In production, use SMS gateway
            var smtp = new SmtpClient("localhost");
            var msg = new MailMessage("noreply@yourapp.com", email)
            {
                Subject = "Your OTP Code",
                Body = $"Your OTP code is: {otp}"
            };
            try { smtp.Send(msg); } catch { /* ignore for demo */ }
        }
    }
}
