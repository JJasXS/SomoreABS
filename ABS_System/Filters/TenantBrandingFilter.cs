using System;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;
using FirebirdSql.Data.FirebirdClient;
using YourApp.Data;
using YourApp.Models;
using YourApp.Services;

namespace YourApp.Filters
{
    // Subdomain-based tenant routing is for web SaaS.
    // Desktop EXE clients should send tenantCode during login instead of relying on host subdomain.
    public class TenantBrandingFilter : IActionFilter
    {
        private readonly FirebirdDb _db;
        private readonly IActivationValidationService _activation;
        private readonly ActivationOptions _activationOptions;
        private static string NormalizeTenantCode(string? code)
        {
            code = (code ?? "").Trim();
            return string.IsNullOrEmpty(code) ? "DEFAULT" : code.ToUpperInvariant();
        }

        public TenantBrandingFilter(
            FirebirdDb db,
            IActivationValidationService activation,
            IOptions<ActivationOptions> activationOptions)
        {
            _db = db;
            _activation = activation;
            _activationOptions = activationOptions.Value;
        }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            var http = context.HttpContext;

            // Multi-tenant SaaS: detect tenantCode from subdomain, query, or cookie/session
            string tenantCode = DetectTenantCode(http);

            var branding = LoadTenantBranding(tenantCode);
            ApplyActivationTenantBranding(branding);

            if (context.Controller is Controller controller)
            {
                controller.ViewBag.TenantBranding = branding;
            }

            http.Items["TenantCode"] = tenantCode;
        }

        public void OnActionExecuted(ActionExecutedContext context)
        {
        }

        /// <summary>
        /// Detects the tenant code from subdomain, query string, or cookie/session.
        /// </summary>
        private static string DetectTenantCode(HttpContext http)
        {
            // 1) Try subdomain (tenantA.yourapp.com => tenantA)
            var host = http.Request.Host.Host;
            if (!string.IsNullOrEmpty(host))
            {
                var parts = host.Split('.');

                // Ignore localhost and treat as no subdomain
                bool isLocalhost =
                    host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                    host.StartsWith("localhost:", StringComparison.OrdinalIgnoreCase);

                if (parts.Length >= 3 && !isLocalhost)
                {
                    var sub = parts[0];
                    if (!string.Equals(sub, "www", StringComparison.OrdinalIgnoreCase))
                        return NormalizeTenantCode(sub);
                }
            }

            // 2) Fallback to query string ?tenant=CODE
            if (http.Request.Query.TryGetValue("tenant", out var qval) && !string.IsNullOrWhiteSpace(qval))
                return NormalizeTenantCode(qval.ToString());

            // 3) Fallback to cookie TENANT_CODE
            if (http.Request.Cookies.TryGetValue("TENANT_CODE", out var cval) && !string.IsNullOrWhiteSpace(cval))
                return NormalizeTenantCode(cval);

            // 4) Fallback to session (if used)
            if (http.Session != null && http.Session.TryGetValue("TENANT_CODE", out var sval) && sval != null && sval.Length > 0)
                return NormalizeTenantCode(Encoding.UTF8.GetString(sval));

            // 5) Default
            return NormalizeTenantCode("DEFAULT");
        }

        private TenantBrandingVm LoadTenantBranding(string tenantCode)
        {
            tenantCode = NormalizeTenantCode(tenantCode);

            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
SELECT
  TENANT_CODE,
  TENANT_NAME,
  HEADER_LOGO_URL,
  HEADER_TEXT1,
  HEADER_TEXT2,
  FOOTER_TEXT1,
  FOOTER_TEXT2,
  FOOTER_TEXT3,
  FOOTER_IMAGE_URL
FROM TENANT
WHERE UPPER(TENANT_CODE) = @CODE
  AND (IS_ACTIVE IS NULL OR IS_ACTIVE <> 0)";

            cmd.Parameters.Add(FirebirdDb.P("@CODE", tenantCode, FbDbType.VarChar));

            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                return new TenantBrandingVm
                {
                    TenantCode = r.IsDBNull(0) ? tenantCode : r.GetString(0).Trim(),
                    TenantName = r.IsDBNull(1) ? "" : r.GetString(1).Trim(),
                    HeaderLogoUrl = r.IsDBNull(2) ? null : r.GetString(2).Trim(),
                    HeaderText1 = r.IsDBNull(3) ? null : r.GetString(3).Trim(),
                    HeaderText2 = r.IsDBNull(4) ? null : r.GetString(4).Trim(),
                    FooterText1 = r.IsDBNull(5) ? null : r.GetString(5).Trim(),
                    FooterText2 = r.IsDBNull(6) ? null : r.GetString(6).Trim(),
                    FooterText3 = r.IsDBNull(7) ? null : r.GetString(7).Trim(),
                    FooterImageUrl = r.IsDBNull(8) ? null : r.GetString(8).Trim()
                };
            }

            return new TenantBrandingVm
            {
                TenantCode = tenantCode,
                TenantName = "Default Company",
                HeaderText1 = "Welcome",
                FooterText1 = "Thank you for using our system."
            };
        }

        /// <summary>
        /// When license activation is valid, show TENANT.COMPANY_NAME (and code) from ACTIVATION.FDB on the header/login instead of the main DB default.
        /// </summary>
        private void ApplyActivationTenantBranding(TenantBrandingVm branding)
        {
            if (!_activationOptions.Enabled || !_activation.IsActivationValid)
                return;

            var snap = _activation.ActivatedTenant;
            if (snap == null)
                return;

            if (!string.IsNullOrWhiteSpace(snap.CompanyName))
                branding.TenantName = snap.CompanyName;
            else if (!string.IsNullOrWhiteSpace(snap.TenantCode))
                branding.TenantName = snap.TenantCode;

            if (!string.IsNullOrWhiteSpace(snap.TenantCode))
                branding.TenantCode = snap.TenantCode;
        }
    }
}
