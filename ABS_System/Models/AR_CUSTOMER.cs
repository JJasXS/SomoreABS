using System.ComponentModel.DataAnnotations;

namespace YourApp.Models
{
    public class AR_CUSTOMER
    {
        [Key]
        public string CustomerCode { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Email { get; set; }
        public string? Phone { get; set; }
        // Add other fields as needed
    }
}
