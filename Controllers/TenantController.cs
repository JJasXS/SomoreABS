using Microsoft.AspNetCore.Mvc;
using YourApp.Models;
using System.Linq;
using YourApp.Data;

namespace YourApp.Controllers
{
    public class TenantController : Controller
    {
        private readonly FirebirdDb _db;
        public TenantController(FirebirdDb db)
        {
            _db = db;
        }

        public IActionResult Index()
        {
            var tenants = new List<TenantBrandingVm>();
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT TENANT_CODE, TENANT_NAME, HEADER_LOGO_URL, HEADER_TEXT1, HEADER_TEXT2, FOOTER_TEXT1, FOOTER_TEXT2, FOOTER_TEXT3, FOOTER_IMAGE_URL FROM TENANT WHERE (IS_ACTIVE IS NULL OR IS_ACTIVE <> 0)";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                tenants.Add(new TenantBrandingVm
                {
                    TenantCode = r.IsDBNull(0) ? "" : r.GetString(0).Trim(),
                    TenantName = r.IsDBNull(1) ? "" : r.GetString(1).Trim(),
                    HeaderLogoUrl = r.IsDBNull(2) ? null : r.GetString(2).Trim(),
                    HeaderText1 = r.IsDBNull(3) ? null : r.GetString(3).Trim(),
                    HeaderText2 = r.IsDBNull(4) ? null : r.GetString(4).Trim(),
                    FooterText1 = r.IsDBNull(5) ? null : r.GetString(5).Trim(),
                    FooterText2 = r.IsDBNull(6) ? null : r.GetString(6).Trim(),
                    FooterText3 = r.IsDBNull(7) ? null : r.GetString(7).Trim(),
                    FooterImageUrl = r.IsDBNull(8) ? null : r.GetString(8).Trim()
                });
            }
            return View(tenants);
        }

        public IActionResult Details(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return NotFound();
            TenantBrandingVm? tenant = null;
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = @"SELECT TENANT_CODE, TENANT_NAME, HEADER_LOGO_URL, HEADER_TEXT1, HEADER_TEXT2, FOOTER_TEXT1, FOOTER_TEXT2, FOOTER_TEXT3, FOOTER_IMAGE_URL FROM TENANT WHERE UPPER(TENANT_CODE) = @CODE AND (IS_ACTIVE IS NULL OR IS_ACTIVE <> 0)";
            cmd.Parameters.Add(FirebirdDb.P("@CODE", id.Trim().ToUpperInvariant(), FirebirdSql.Data.FirebirdClient.FbDbType.VarChar));
            using var r = cmd.ExecuteReader();
            if (r.Read())
            {
                tenant = new TenantBrandingVm
                {
                    TenantCode = r.IsDBNull(0) ? id : r.GetString(0).Trim(),
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
            if (tenant == null) return NotFound();
            return View(tenant);
        }
    }
}
