using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace YourApp.Models
{
    public class Appointment
    {
        public long ApptId { get; set; }

        [Required(ErrorMessage = "Please select a customer.")]
        public string CustomerCode { get; set; } = "";

        [Required(ErrorMessage = "Please select an agent.")]
        public string AgentCode { get; set; } = "";

        [Required(ErrorMessage = "Please choose a start date & time.")]
        public DateTime ApptStart { get; set; }

        [Required(ErrorMessage = "Please choose an end date & time.")]
        public DateTime ApptEnd { get; set; }

        public string? Title { get; set; }
        public string? Notes { get; set; }

        public string Status { get; set; } = "NEW";

        // Optional usage if you later want to bind detail list
        public List<ApptDtl> Services { get; set; } = new List<ApptDtl>();
    }
}
