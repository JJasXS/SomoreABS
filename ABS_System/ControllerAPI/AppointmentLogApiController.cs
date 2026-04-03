using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using YourApp.Models;
using YourApp.Data;

namespace ABS_System.ControllerAPI
{
    [ApiController]
    [Route("api/[controller]")]
    public class AppointmentLogApiController : ControllerBase
    {
        private readonly FirebirdDb _db;
        public AppointmentLogApiController(FirebirdDb db) { _db = db; }

        [HttpGet("{apptId}")]
        public IActionResult GetByAppointment(long apptId)
        {
            var list = new List<AppointmentLog>();
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT LOG_ID, APPT_ID, ACTION_TYPE, ACTION_TIME, USERNAME, DETAILS, SO_QTY, CLAIMED, PREV_CLAIMED, CURR_CLAIMED, SERVICE_CODE FROM APPOINTMENT_LOG WHERE APPT_ID = @APPT_ID ORDER BY ACTION_TIME DESC";
            cmd.Parameters.Add(new FirebirdSql.Data.FirebirdClient.FbParameter("@APPT_ID", apptId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new AppointmentLog
                {
                    LogId = r.GetInt64(0),
                    ApptId = r.GetInt64(1),
                    ActionType = r.IsDBNull(2) ? "" : r.GetString(2).Trim(),
                    ActionTime = r.GetDateTime(3),
                    Username = r.IsDBNull(4) ? null : r.GetString(4).Trim(),
                    Details = r.IsDBNull(5) ? null : r.GetString(5).Trim(),
                    SoQty = r.IsDBNull(6) ? null : r.GetInt32(6),
                    Claimed = r.IsDBNull(7) ? null : r.GetInt32(7),
                    PrevClaimed = r.IsDBNull(8) ? null : r.GetInt32(8),
                    CurrClaimed = r.IsDBNull(9) ? null : r.GetInt32(9),
                    ServiceCode = r.IsDBNull(10) ? null : r.GetString(10).Trim()
                });
            }
            return Ok(list);
        }
    }
}
