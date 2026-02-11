using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Mail;
using System.Security.Claims;
using System.Threading.Tasks;
using YourApp.Data;
using YourApp.Models;

namespace YourApp.Controllers
{
    public class AccountController : Controller
    {
        // OTP stored by AgentCode
        private static readonly ConcurrentDictionary<string, string> OtpStore = new();

        private readonly FirebirdDb _db;
        private readonly IConfiguration _config;

        public AccountController(FirebirdDb db, IConfiguration config)
        {
            _db = db;
            _config = config;
        }

        // =========================================================
        // LOGIN (GET)
        // =========================================================
        [HttpGet]
        public IActionResult Login()
        {
            return View(new AgentLoginViewModel());
        }

        // =========================================================
        // LOGIN (POST) -> Generate OTP and Email
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Login(AgentLoginViewModel model)
        {
            Console.WriteLine($"[OTP] Login POST hit. Email={model?.Email}");

            if (!ModelState.IsValid)
                return View(model);

            var emailIn = (model?.Email ?? "").Trim();

            if (string.IsNullOrWhiteSpace(emailIn))
            {
                ModelState.AddModelError("Email", "Email is required.");
                return View(model);
            }

            // =========================
            // 1) Check agent exists by email only
            // =========================
            string? agentCode = null;

            try
            {
                using (var conn = _db.Open())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT CODE FROM AGENT WHERE UDF_EMAIL = @UDF_EMAIL";
                    cmd.Parameters.Add(FirebirdDb.P("@UDF_EMAIL", emailIn,
                        FirebirdSql.Data.FirebirdClient.FbDbType.VarChar));

                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        agentCode = r.IsDBNull(0) ? null : r.GetString(0).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[OTP] Login load agent FAILED:");
                Console.WriteLine(ex.ToString());
                ModelState.AddModelError("", "Server error while checking email. Please try again.");
                return View(model);
            }

            if (string.IsNullOrEmpty(agentCode))
            {
                ModelState.AddModelError("Email", "Email not found. Please contact administrator.");
                return View(model);
            }

            // =========================
            // 2) Generate OTP + store
            // =========================
            var otp = new Random().Next(100000, 999999).ToString();
            OtpStore[agentCode] = otp;

            Console.WriteLine($"[OTP] Generated OTP for AgentCode={agentCode}, Email={emailIn}: {otp}");

            // =========================
            // 3) Send OTP email (SMTP)
            // =========================
            try
            {
                Console.WriteLine("[OTP] Sending email via SMTP...");
                SendOtpEmail(emailIn, otp);
                Console.WriteLine("[OTP] Email send OK");
            }
            catch (Exception ex)
            {
                Console.WriteLine("[OTP] Email send FAILED:");
                Console.WriteLine(ex.ToString());

                ModelState.AddModelError("", "Failed to send OTP email. Please check SMTP settings / server logs.");
                return View(model);
            }

            // =========================
            // 4) Redirect to VerifyOtp
            // =========================
            TempData["AgentCode"] = agentCode;
            TempData["Email"] = emailIn;

            return RedirectToAction("VerifyOtp");
        }

        // =========================================================
        // VERIFY OTP (GET)
        // =========================================================
        [HttpGet]
        public IActionResult VerifyOtp()
        {
            var agentCode = TempData["AgentCode"] as string;
            var email = TempData["Email"] as string;

            if (string.IsNullOrEmpty(agentCode) || string.IsNullOrEmpty(email))
                return RedirectToAction("Login");

            // keep for next post
            TempData["AgentCode"] = agentCode;
            TempData["Email"] = email;

            return View(new OtpViewModel { AgentCode = agentCode });
        }

        // =========================================================
        // VERIFY OTP (POST) -> Sign In cookie
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> VerifyOtp(OtpViewModel model)
        {
            if (!ModelState.IsValid)
                return View(model);

            var agentCodeIn = (model.AgentCode ?? "").Trim();
            var otpIn = (model.Otp ?? "").Trim();

            // ✅ 1) OTP check
            if (!OtpStore.TryGetValue(agentCodeIn, out var otp) || otp != otpIn)
            {
                ModelState.AddModelError("Otp", "Invalid OTP.");
                return View(model);
            }

            // ✅ 2) OTP success → remove OTP
            OtpStore.TryRemove(agentCodeIn, out _);

            // ✅ 3) Load Email + BranchNo from AGENT for claims
            string email = "";
            string branchNo = "";

            try
            {
                using (var conn = _db.Open())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "SELECT UDF_EMAIL, UDF_BRANCH FROM AGENT WHERE CODE = @CODE";
                    cmd.Parameters.Add(FirebirdDb.P("@CODE", agentCodeIn,
                        FirebirdSql.Data.FirebirdClient.FbDbType.VarChar));

                    using var r = cmd.ExecuteReader();
                    if (r.Read())
                    {
                        email = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                        branchNo = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[OTP] VerifyOtp load agent FAILED:");
                Console.WriteLine(ex.ToString());
            }

            // fallback: if email empty, try TempData email (from Login step)
            if (string.IsNullOrWhiteSpace(email))
                email = (TempData["Email"] as string ?? "").Trim();

            bool isOffice = branchNo == "1";

            // ✅ 4) Build claims
            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, email),
                new Claim(ClaimTypes.Email, email),
                new Claim("AgentCode", agentCodeIn),
                new Claim("BranchNo", branchNo),
                new Claim("IsOffice", isOffice ? "1" : "0")
            };

            var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
            var principal = new ClaimsPrincipal(identity);

            // ✅ 5) Sign in (cookie) - use await
            await HttpContext.SignInAsync(
                CookieAuthenticationDefaults.AuthenticationScheme,
                principal,
                new AuthenticationProperties
                {
                    IsPersistent = true,
                    ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8)
                }
            );

            return RedirectToAction("Index", "Calendar");
        }

        // =========================================================
        // LOGOUT (POST) -> MUST redirect to Login
        // =========================================================
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Logout()
        {
            // clear server session (if used)
            HttpContext.Session.Clear();

            // sign out auth cookie
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            // ✅ DO NOT go to Home/Index, go to Login
            return RedirectToAction("Login", "Account");
        }

        // =========================================================
        // SMTP EMAIL (READS FROM appsettings.json)
        // =========================================================
        private void SendOtpEmail(string toEmail, string otp)
        {
            var host = _config["Smtp:Host"];
            var portStr = _config["Smtp:Port"];
            var enableSslStr = _config["Smtp:EnableSsl"];
            var user = _config["Smtp:User"];
            var pass = _config["Smtp:Pass"];
            var fromEmail = _config["Smtp:FromEmail"];
            if (string.IsNullOrWhiteSpace(fromEmail)) fromEmail = user; // fallback to user if not provided
            var fromName = _config["Smtp:FromName"] ?? "OTP";

            if (string.IsNullOrWhiteSpace(host)) throw new Exception("Missing Smtp:Host in appsettings.json");
            if (string.IsNullOrWhiteSpace(portStr)) throw new Exception("Missing Smtp:Port in appsettings.json");
            if (string.IsNullOrWhiteSpace(user)) throw new Exception("Missing Smtp:User in appsettings.json");
            if (string.IsNullOrWhiteSpace(pass)) throw new Exception("Missing Smtp:Pass in appsettings.json");

            int port = int.Parse(portStr);

            bool enableSsl = true;
            if (!string.IsNullOrWhiteSpace(enableSslStr))
                enableSsl = bool.Parse(enableSslStr);

            using var smtp = new SmtpClient(host, port)
            {
                EnableSsl = enableSsl,
                UseDefaultCredentials = false,
                Credentials = new NetworkCredential(user, pass),
                DeliveryMethod = SmtpDeliveryMethod.Network
            };

            if (string.IsNullOrWhiteSpace(fromEmail))
                throw new Exception("fromEmail cannot be null or empty for MailAddress");
            using var msg = new MailMessage
            {
                From = new MailAddress(fromEmail, fromName),
                Subject = "Your OTP Code",
                Body = $"Your OTP code is: {otp}",
                IsBodyHtml = false
            };

            msg.To.Add(new MailAddress(toEmail));

            smtp.Send(msg);
        }
    }
}
