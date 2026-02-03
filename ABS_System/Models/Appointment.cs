using System.ComponentModel.DataAnnotations;

namespace YourApp.Models
{
    public class Appointment
    {
        public long ApptId { get; set; }

        [Required]
        public string CustomerCode { get; set; } = "";

        [Required]
        public string AgentCode { get; set; } = "";

        [Required]
        public DateTime ApptStart { get; set; }

        [Required]
        public DateTime ApptEnd { get; set; }


        public string? Title { get; set; }
        public string? Notes { get; set; }

        public string Status { get; set; } = "NEW";
    }
}
