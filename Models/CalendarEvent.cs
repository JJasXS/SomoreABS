using System;
using System.ComponentModel.DataAnnotations;

namespace YourApp.Models
{
    public class CalendarEvent
    {
        [Key]
        public int Id { get; set; }

        [Required, StringLength(120)]
        public string Title { get; set; } = "";

        [StringLength(500)]
        public string? Notes { get; set; }

        [Required]
        public DateTime Start { get; set; }  // when event starts

        public DateTime? End { get; set; }   // optional end time

        public bool AllDay { get; set; } = true;
    }
}
