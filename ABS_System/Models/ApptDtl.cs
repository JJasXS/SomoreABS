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

        // Added for UDF_CLAIMED info
        public int Claimed { get; set; } // UDF_CLAIMED
        public int PrevClaimed { get; set; } // UDF_PREV_CLAIMED
    }
}
