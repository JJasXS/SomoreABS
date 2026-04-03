using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using YourApp.Models;
using YourApp.Data;

namespace ABS_System.ControllerAPI
{
    [ApiController]
    [Route("api/[controller]")]
    public class ItemApiController : ControllerBase
    {
        private readonly FirebirdDb _db;
        public ItemApiController(FirebirdDb db) { _db = db; }

        [HttpGet]
        public IActionResult GetAll()
        {
            var list = new List<ST_ITEM>();
            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT CODE, DESCRIPTION, STOCKGROUP FROM ST_ITEM ORDER BY CODE";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                list.Add(new ST_ITEM
                {
                    CODE = r.IsDBNull(0) ? "" : r.GetString(0).Trim(),
                    DESCRIPTION = r.IsDBNull(1) ? "" : r.GetString(1).Trim(),
                    STOCKGROUP = r.IsDBNull(2) ? "" : r.GetString(2).Trim()
                });
            }
            return Ok(list);
        }
    }
}
