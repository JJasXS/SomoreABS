using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using YourApp.Models;
using YourApp.Data;

namespace ABS_System.ControllerAPI
{
    [ApiController]
    [Route("api/[controller]")]
    public class AppointmentDetailApiController : ControllerBase
    {
        private readonly FirebirdDb _db;
        public AppointmentDetailApiController(FirebirdDb db) { _db = db; }

        [HttpGet("{apptId}")]
        public IActionResult GetByAppointment(long apptId)
        {
            var list = new List<ApptDtl>();
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT APPT_DTL_ID, APPT_ID, SERVICE_CODE, QTY FROM APPT_DTL WHERE APPT_ID = @APPT_ID";
            cmd.Parameters.Add(new FirebirdSql.Data.FirebirdClient.FbParameter("@APPT_ID", apptId));
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new ApptDtl
                {
                    Id = r.GetInt64(0),
                    ApptId = r.GetInt64(1),
                    ServiceCode = r.IsDBNull(2) ? "" : r.GetString(2).Trim(),
                    Qty = r.IsDBNull(3) ? 0 : r.GetInt32(3)
                });
            }
            return Ok(list);
        }
    }
}
