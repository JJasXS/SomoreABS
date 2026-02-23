using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using YourApp.Models;
using YourApp.Data;

namespace ABS_System.ControllerAPI
{
    [ApiController]
    [Route("api/[controller]")]
    public class AppointmentApiController : ControllerBase
    {
        private readonly FirebirdDb _db;
        public AppointmentApiController(FirebirdDb db) { _db = db; }

        [HttpGet]
        public IActionResult GetAll()
        {
            var list = new List<Appointment>();
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT APPT_ID, CUSTOMER_CODE, AGENT_CODE, APPT_START, APPT_END, TITLE, NOTES, STATUS FROM APPOINTMENT ORDER BY APPT_START DESC";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new Appointment
                {
                    ApptId = r.GetInt64(0),
                    CustomerCode = r.IsDBNull(1) ? "" : r.GetString(1).Trim(),
                    AgentCode = r.IsDBNull(2) ? "" : r.GetString(2).Trim(),
                    ApptStart = r.GetDateTime(3),
                    ApptEnd = r.IsDBNull(4) ? (DateTime?)null : r.GetDateTime(4),
                    Title = r.IsDBNull(5) ? null : r.GetString(5).Trim(),
                    Notes = r.IsDBNull(6) ? null : r.GetString(6).Trim(),
                    Status = r.IsDBNull(7) ? "BOOKED" : r.GetString(7).Trim()
                });
            }
            return Ok(list);
        }
    }
}
