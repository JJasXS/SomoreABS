using System.ComponentModel.DataAnnotations;

namespace YourApp.Models
{
    public class AgentLoginViewModel
    {
        [Required]
        [EmailAddress]
        public string Email { get; set; } = "";
    }

    public class OtpViewModel
    {
        [Required]
        public string AgentCode { get; set; } = "";

        [Required]
        public string Otp { get; set; } = "";
    }
}
