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
        private static void LogWarn(string where, Exception ex)
            => Console.WriteLine($"[WARN][AppointmentController] {where}: {ex.Message}");

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
            catch (Exception ex)
            {
                LogWarn(nameof(GetApptYearMonth), ex);
            }

            return (y, m);
        }

        /// <summary>First non-empty phone from AR_CUSTOMERBRANCH: PHONE1, then PHONE2, then MOBILE.</summary>
        private static string PickCustomerBranchPhone(string? phone1, string? phone2, string? mobile)
        {
            if (!string.IsNullOrWhiteSpace(phone1)) return phone1.Trim();
            if (!string.IsNullOrWhiteSpace(phone2)) return phone2.Trim();
            if (!string.IsNullOrWhiteSpace(mobile)) return mobile.Trim();
            return "";
        }

        private void LoadAgentsAndCustomers(string branchNo, out List<dynamic> agents, out List<dynamic> customers)
        {
            agents = new List<dynamic>();
            customers = new List<dynamic>();

            branchNo = (branchNo ?? "").Trim();

            using var conn = _db.Open();
            using var cmd = conn.CreateCommand();


            if (branchNo == "0")
            {
                // Office/superuser: see all agents
                cmd.CommandText = @"SELECT CODE, DESCRIPTION FROM AGENT ORDER BY DESCRIPTION";
            }
            else
            {
                // Normal: see only agents in own branch
                cmd.CommandText = @"SELECT CODE, DESCRIPTION FROM AGENT WHERE UDF_BRANCH = @UDF_BRANCH ORDER BY DESCRIPTION";
                cmd.Parameters.Add(FirebirdDb.P("@UDF_BRANCH", branchNo, FbDbType.VarChar));
            }

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
            cmd.CommandText = @"
SELECT c.CODE, c.COMPANYNAME, b.PHONE1, b.PHONE2, b.MOBILE
FROM AR_CUSTOMER c
LEFT JOIN AR_CUSTOMERBRANCH b ON b.CODE = c.CODE";

            // Merge duplicate customer codes (multiple branch rows): prefer a row with any phone.
            var byCode = new Dictionary<string, (string Name, string P1, string P2, string Mob)>(StringComparer.OrdinalIgnoreCase);
            using (var r = cmd.ExecuteReader())
            {
                while (r.Read())
                {
                    var code = r.IsDBNull(0) ? "" : r.GetString(0).Trim();
                    if (string.IsNullOrWhiteSpace(code)) continue;

                    var name = r.IsDBNull(1) ? "" : r.GetString(1).Trim();
                    var p1 = r.IsDBNull(2) ? "" : r.GetString(2).Trim();
                    var p2 = r.IsDBNull(3) ? "" : r.GetString(3).Trim();
                    var mob = r.IsDBNull(4) ? "" : r.GetString(4).Trim();

                    if (!byCode.TryGetValue(code, out var existing))
                    {
                        byCode[code] = (name, p1, p2, mob);
                        continue;
                    }

                    var hadPhone = !string.IsNullOrWhiteSpace(PickCustomerBranchPhone(existing.P1, existing.P2, existing.Mob));
                    var newPhone = !string.IsNullOrWhiteSpace(PickCustomerBranchPhone(p1, p2, mob));
                    if (!hadPhone && newPhone)
                        byCode[code] = (name, p1, p2, mob);
                }
            }

            foreach (var kv in byCode.OrderBy(x => x.Value.Name, StringComparer.OrdinalIgnoreCase))
            {
                var phone = PickCustomerBranchPhone(kv.Value.P1, kv.Value.P2, kv.Value.Mob);
                var companyName = kv.Value.Name;
                var displayName = string.IsNullOrWhiteSpace(phone)
                    ? companyName
                    : $"{companyName} ({phone})";

                customers.Add(new
                {
                    Code = kv.Key,
                    Name = companyName,
                    DisplayName = displayName
                });
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
                var codes = allItems.Select(x => (x.CODE ?? "").Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
                if (codes.Count == 0)
                    return serviceItems;

                var inClause = BuildInClauseParams(cmdDtl, "@p_svc_", codes);
                cmdDtl.CommandText = $"SELECT ITEMCODE, QTY, UDF_CLAIMED, UDF_PREV_CLAIMED FROM SL_SODTL WHERE ITEMCODE IN ({inClause})";
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
            catch (Exception ex)
            {
                LogWarn(nameof(LoadServiceItems), ex);
            }
            return serviceItems;
        }

        private static string BuildInClauseParams(FbCommand cmd, string paramPrefix, List<string> values)
        {
            var names = new List<string>(values.Count);
            for (int i = 0; i < values.Count; i++)
            {
                var pName = $"{paramPrefix}{i}";
                names.Add(pName);
                cmd.Parameters.Add(FirebirdDb.P(pName, values[i], FbDbType.VarChar));
            }
            return string.Join(", ", names);
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
SELECT FIRST 1 UDF_BRANCH
FROM AGENT
WHERE LOWER(UDF_EMAIL) = LOWER(@UDF_EMAIL)";

                cmd.Parameters.Add(FirebirdDb.P("@UDF_EMAIL", email, FbDbType.VarChar));

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
