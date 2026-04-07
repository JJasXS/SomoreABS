using System;

namespace YourApp.Models
{
    /// <summary>
    /// View model for site branding (header/footer). Values are set in <see cref="YourApp.Filters.TenantBrandingFilter"/>.
    /// </summary>
    public class TenantBrandingVm
    {
        public string TenantCode { get; set; } = "";
        public string TenantName { get; set; } = "";

        public string? HeaderLogoUrl { get; set; }
        public string? HeaderText1 { get; set; }
        public string? HeaderText2 { get; set; }

        public string? FooterText1 { get; set; }
        public string? FooterText2 { get; set; }
        public string? FooterText3 { get; set; }
        public string? FooterImageUrl { get; set; }
    }
}

