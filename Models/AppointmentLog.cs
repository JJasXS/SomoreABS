using System;

namespace YourApp.Models
{
    public class AppointmentLog
    {
        public long LogId { get; set; }
        public long ApptId { get; set; }
        public string ActionType { get; set; } = ""; // e.g. 'E_SIGNATURE_SUBMIT'
        public DateTime ActionTime { get; set; }
        public string? Username { get; set; }
        public string? Details { get; set; }
        public int? SoQty { get; set; }
        public int? Claimed { get; set; }
        public int? PrevClaimed { get; set; }
        public int? CurrClaimed { get; set; }
        public string? ServiceCode { get; set; }
    }
}