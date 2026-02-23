using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using YourApp.Models;
using YourApp.Data;

namespace ABS_System.ControllerAPI
{
    [ApiController]
    [Route("api/[controller]")]
    public class CustomerApiController : ControllerBase
    {
        private readonly FirebirdDb _db;
        public CustomerApiController(FirebirdDb db) { _db = db; }

        [HttpGet]
        public IActionResult GetAll()
        {
            var list = new List<AR_CUSTOMER>();
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CODE, COMPANYNAME FROM AR_CUSTOMER ORDER BY COMPANYNAME";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new AR_CUSTOMER
                {
                    CustomerCode = r.IsDBNull(0) ? "" : r.GetString(0).Trim(), // CODE
                    Name = r.IsDBNull(1) ? "" : r.GetString(1).Trim()           // COMPANYNAME
                });
            }
            return Ok(list);
        }
    }
}
