using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using YourApp.Models;
using YourApp.Data;

namespace YourApp.Filters
{
    public class TenantBrandingFilter : IActionFilter
    {
        private readonly FirebirdDb _db;
        public TenantBrandingFilter(FirebirdDb db)
        {
            _db = db;
        }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            // Detect tenant code (domain, query, session, etc.)
            // For demo, use a fixed code
            string tenantCode = "SOMORE";
            var branding = LoadTenantBranding(tenantCode);
            var controller = context.Controller as Controller;
            if (controller != null)
            {
                controller.ViewBag.TenantBranding = branding;
            }
        }

        public void OnActionExecuted(ActionExecutedContext context) { }

        private TenantBrandingVm LoadTenantBranding(string tenantCode)
        {
            tenantCode = (tenantCode ?? "").Trim().ToUpperInvariant();
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT TENANT_CODE, TENANT_NAME, HEADER_LOGO_URL, HEADER_TEXT1, HEADER_TEXT2, FOOTER_TEXT1, FOOTER_TEXT2, FOOTER_TEXT3, FOOTER_IMAGE_URL FROM TENANT WHERE UPPER(TENANT_CODE) = @CODE AND (IS_ACTIVE IS NULL OR IS_ACTIVE <> 0)";
            cmd.Parameters.Add(FirebirdDb.P("@CODE", tenantCode, FirebirdSql.Data.FirebirdClient.FbDbType.VarChar));
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
    }
}
