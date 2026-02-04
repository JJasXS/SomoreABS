using System;

namespace YourApp.Models
{
    public class ApptDtl
    {
        public long Id { get; set; }
        public long ApptId { get; set; }
        public string ServiceCode { get; set; } = "";
        public string? Notes { get; set; }
    }
}
