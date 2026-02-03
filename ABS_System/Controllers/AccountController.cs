using Microsoft.AspNetCore.Mvc;
using YourApp.Models;
using System;
using System.Collections.Concurrent;
using System.Net.Mail;
using System.Net;
using YourApp.Data;

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
            if (OtpStore.TryGetValue(model.AgentCode, out var otp) && otp == model.Otp)
            {
                // Success: log in user (set session/cookie as needed)
                OtpStore.TryRemove(model.AgentCode, out _);
                // TODO: Set authentication cookie/session
                return RedirectToAction("Index", "Calendar");
            }
            ModelState.AddModelError("Otp", "Invalid OTP.");
            return View(model);
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
