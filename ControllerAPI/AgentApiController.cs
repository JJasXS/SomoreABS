using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using YourApp.Models;
using YourApp.Data;

namespace ABS_System.ControllerAPI
{
    [ApiController]
    [Route("api/[controller]")]
    public class AgentApiController : ControllerBase
    {
        private readonly FirebirdDb _db;
        public AgentApiController(FirebirdDb db) { _db = db; }

        [HttpGet]
        public IActionResult GetAll()
        {
            var list = new List<Agent>();
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CODE, DESCRIPTION FROM AGENT ORDER BY CODE";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new Agent
                {
                    Code = r.IsDBNull(0) ? "" : r.GetString(0).Trim(),
                    Description = r.IsDBNull(1) ? "" : r.GetString(1).Trim()
                });
            }
            return Ok(list);
        }
    }
}
