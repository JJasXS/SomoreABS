using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.AspNetCore.Mvc;
using FirebirdSql.Data.FirebirdClient;
using YourApp.Data;
using YourApp.Models;

namespace YourApp.Controllers
{
    public partial class AppointmentController : Controller
    {
        // =========================================================
        // HELPERS
        // =========================================================

        private bool HasSignature(long apptId)
        {
            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM APPT_SIGNATURE WHERE APPT_ID = @ID";
                cmd.Parameters.Add(FirebirdDb.P("@ID", apptId, FbDbType.BigInt));
                return Convert.ToInt32(cmd.ExecuteScalar()) > 0;
            }
            catch
            {
                return false;
            }
        }

        private string GetCustomerCompanyName(string customerCode)
        {
            var code = (customerCode ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code)) return "";

            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = @"
SELECT COMPANYNAME
FROM AR_CUSTOMER
WHERE CODE = @CODE";

                cmd.Parameters.Add(FirebirdDb.P("@CODE", code, FbDbType.VarChar));

                var v = cmd.ExecuteScalar();
                var name = (v == null || v == DBNull.Value) ? "" : v.ToString()?.Trim();
                return !string.IsNullOrWhiteSpace(name) ? name : code;
            }
            catch
            {
                return code;
            }
        }

        private string GetCustomerName(string customerCode, FbConnection conn)
        {
            var code = (customerCode ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code)) return "";

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COMPANYNAME FROM AR_CUSTOMER WHERE CODE = @CODE";
                cmd.Parameters.Add(FirebirdDb.P("@CODE", code, FbDbType.VarChar));

                var v = cmd.ExecuteScalar();
                var name = (v == null || v == DBNull.Value) ? "" : v.ToString()?.Trim();
                return !string.IsNullOrWhiteSpace(name) ? name : code;
            }
            catch
            {
                return code;
            }
        }

        private string GetAgentName(string agentCode, FbConnection conn)
        {
            var code = (agentCode ?? "").Trim();
            if (string.IsNullOrWhiteSpace(code)) return "";

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT DESCRIPTION FROM AGENT WHERE CODE = @CODE";
                cmd.Parameters.Add(FirebirdDb.P("@CODE", code, FbDbType.VarChar));

                var v = cmd.ExecuteScalar();
                var name = (v == null || v == DBNull.Value) ? "" : v.ToString()?.Trim();
                return !string.IsNullOrWhiteSpace(name) ? name : code;
            }
            catch
            {
                return code;
            }
        }

        private static void SetIfPropertyExists(object target, string propName, string value)
        {
            var prop = target.GetType().GetProperty(propName);
            if (prop == null) return;
            if (!prop.CanWrite) return;
            if (prop.PropertyType != typeof(string)) return;
            prop.SetValue(target, value);
        }

        private (int year, int month) GetApptYearMonth(long apptId)
        {
            int y = DateTime.Today.Year;
            int m = DateTime.Today.Month;

            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT APPT_START FROM APPOINTMENT WHERE APPT_ID = @ID";
                cmd.Parameters.Add(FirebirdDb.P("@ID", apptId, FbDbType.BigInt));

                var dt = cmd.ExecuteScalar();
                if (dt != null && dt != DBNull.Value)
                {
                    var apptStart = Convert.ToDateTime(dt);
                    y = apptStart.Year;
                    m = apptStart.Month;
                }
            }
            catch { }

            return (y, m);
        }

        private void LoadAgentsAndCustomers(string branchNo, out List<dynamic> agents, out List<dynamic> customers)
        {
            agents = new List<dynamic>();
            customers = new List<dynamic>();

            branchNo = (branchNo ?? "").Trim();

            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();

            cmd.CommandText = @"
SELECT CODE, DESCRIPTION
FROM AGENT
WHERE (@BRANCHNO = '1' OR BRANCHNO = @BRANCHNO)
ORDER BY DESCRIPTION";

            cmd.Parameters.Add(FirebirdDb.P("@BRANCHNO", branchNo, FbDbType.VarChar));

            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    agents.Add(new
                    {
                        Code = r.IsDBNull(0) ? "" : r.GetString(0).Trim(),
                        Description = r.IsDBNull(1) ? "" : r.GetString(1).Trim()
                    });
                }
            }

            cmd.Parameters.Clear();
            cmd.CommandText = "SELECT CODE, COMPANYNAME FROM AR_CUSTOMER ORDER BY COMPANYNAME";
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    customers.Add(new
                    {
                        Code = r.IsDBNull(0) ? "" : r.GetString(0).Trim(),
                        Name = r.IsDBNull(1) ? "" : r.GetString(1).Trim()
                    });
                }
            }
        }

        private List<ST_ITEM> LoadServiceItems()
        {
            var serviceItems = new List<ST_ITEM>();
            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();
                // Query all service items
                cmd.CommandText = @"SELECT CODE, DESCRIPTION FROM ST_ITEM WHERE STOCKGROUP = 'SERVICE' ORDER BY DESCRIPTION";
                using var r = cmd.ExecuteReader();
                var allItems = new List<ST_ITEM>();
                while (r.Read())
                {
                    allItems.Add(new ST_ITEM
                    {
                        CODE = r.IsDBNull(0) ? "" : r.GetString(0).Trim(),
                        DESCRIPTION = r.IsDBNull(1) ? "" : r.GetString(1).Trim(),
                        STOCKGROUP = "SERVICE"
                    });
                }
                r.Close();

                // Query UDF_CLAIMED, UDF_PREV_CLAIMED, QTY for each service
                using var cmdDtl = conn.CreateCommand();
                cmdDtl.CommandText = @"SELECT CODE, QTY, UDF_CLAIMED, UDF_PREV_CLAIMED FROM SL_SODTL WHERE CODE IN ('" + string.Join("','", allItems.Select(x => x.CODE)) + "')";
                var claimedInfo = new Dictionary<string, (int qty, int claimed, int prevClaimed)>();
                using var rDtl = cmdDtl.ExecuteReader();
                while (rDtl.Read())
                {
                    var code = rDtl.IsDBNull(0) ? "" : rDtl.GetString(0).Trim();
                    var qty = rDtl.IsDBNull(1) ? 0 : rDtl.GetInt32(1);
                    var claimed = rDtl.IsDBNull(2) ? 0 : rDtl.GetInt32(2);
                    var prevClaimed = rDtl.IsDBNull(3) ? 0 : rDtl.GetInt32(3);
                    claimedInfo[code] = (qty, claimed, prevClaimed);
                }

                foreach (var item in allItems)
                {
                    if (claimedInfo.TryGetValue(item.CODE, out var info))
                    {
                        var totalClaimed = info.claimed + info.prevClaimed;
                        // Hide service if fully claimed
                        if (totalClaimed >= info.qty)
                            continue;
                        // Add a property for frontend warning if only one left
                        item.DESCRIPTION += (info.qty - totalClaimed == 1) ? " <span style=\"color:red\">(Last one!)</span>" : "";
                    }
                    serviceItems.Add(item);
                }
            }
            catch { }
            return serviceItems;
        }

        private List<string> LoadSelectedServiceCodes(long apptId)
        {
            var list = new List<string>();

            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT SERVICE_CODE FROM APPT_DTL WHERE APPT_ID = @id ORDER BY SERVICE_CODE";
            cmd.Parameters.Add(FirebirdDb.P("@id", apptId, FbDbType.BigInt));

            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                if (!r.IsDBNull(0))
                    list.Add(r.GetString(0).Trim());
            }

            return list;
        }

        private bool HasAgentOverlap(string agentCode, DateTime start, DateTime end, long? excludeApptId)
        {
            var agent = (agentCode ?? "").Trim();
            if (string.IsNullOrEmpty(agent))
                return false;

            return HasOverlap_Generic("AGENT_CODE", agent, start, end, excludeApptId);
        }

        private bool HasCustomerOverlap(string customerCode, DateTime start, DateTime end, long? excludeApptId)
        {
            var cust = (customerCode ?? "").Trim();
            if (string.IsNullOrEmpty(cust))
                return false;

            return HasOverlap_Generic("CUSTOMER_CODE", cust, start, end, excludeApptId);
        }

        private bool HasOverlap_Generic(string columnName, string codeValue, DateTime start, DateTime end, long? excludeApptId)
        {
            using var conn = _db.Open();

            try
            {
                using var cmd = conn.CreateCommand();
                cmd.CommandText = $@"
SELECT COUNT(*)
FROM APPOINTMENT
WHERE {columnName} = @CODE
  AND ((@S < APPT_END) AND (@E > APPT_START))
  AND (@EX IS NULL OR APPT_ID <> @EX)";

                cmd.Parameters.Add(FirebirdDb.P("@CODE", codeValue, FbDbType.VarChar));
                cmd.Parameters.Add(FirebirdDb.P("@S", start, FbDbType.TimeStamp));
                cmd.Parameters.Add(FirebirdDb.P("@E", end, FbDbType.TimeStamp));
                cmd.Parameters.Add(FirebirdDb.P("@EX", excludeApptId, FbDbType.BigInt));

                var count = Convert.ToInt32(cmd.ExecuteScalar());
                return count > 0;
            }
            catch
            {
                // fallback below
            }

            using (var cmd2 = conn.CreateCommand())
            {
                cmd2.CommandText = $@"
SELECT APPT_START
FROM APPOINTMENT
WHERE {columnName} = @CODE
  AND (@EX IS NULL OR APPT_ID <> @EX)";

                cmd2.Parameters.Add(FirebirdDb.P("@CODE", codeValue, FbDbType.VarChar));
                cmd2.Parameters.Add(FirebirdDb.P("@EX", excludeApptId, FbDbType.BigInt));

                using var r = cmd2.ExecuteReader();
                while (r.Read())
                {
                    var existingStart = r.GetDateTime(0);
                    var existingEnd = existingStart.AddMinutes(DEFAULT_DURATION_MINUTES);

                    if (start < existingEnd && end > existingStart)
                        return true;
                }
            }

            return false;
        }

        private List<string> ParseCsv(string csv)
        {
            return (csv ?? "")
                .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private string GetBranchNoByEmail(string email)
        {
            email = (email ?? "").Trim();
            if (string.IsNullOrWhiteSpace(email)) return "";

            try
            {
                using var conn = _db.Open();
                using var cmd = conn.CreateCommand();

                cmd.CommandText = @"
SELECT FIRST 1 BRANCHNO
FROM AGENT
WHERE LOWER(EMAIL) = LOWER(@EMAIL)";

                cmd.Parameters.Add(FirebirdDb.P("@EMAIL", email, FbDbType.VarChar));

                var v = cmd.ExecuteScalar();
                return (v == null || v == DBNull.Value) ? "" : (v?.ToString()?.Trim() ?? "");
            }
            catch
            {
                return "";
            }
        }
    }
}
