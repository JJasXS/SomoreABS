using System;

namespace YourApp.Models
{
    public class ApptDtl
    {
        public long Id { get; set; }
        public long ApptId { get; set; }
        public string ServiceCode { get; set; } = "";

        // QTY from APPT_DTL
        public int Qty { get; set; }

        // Added for CLAIMED info
        public int Claimed { get; set; }
        public int PrevClaimed { get; set; }
    }
}
