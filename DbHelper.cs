using System;
using System.Collections.Generic;
using System.Data;
using FirebirdSql.Data.FirebirdClient;
using YourApp.Services;

namespace FirebirdWeb.Helpers
{
    public class DbHelper
    {
        private readonly IClientFirebirdConnectionProvider _clientFirebird;

        public DbHelper(IClientFirebirdConnectionProvider clientFirebird)
        {
            _clientFirebird = clientFirebird;
        }

        private string ConnectionString => _clientFirebird.GetConnectionString();

        // Optional: open and return a connection (caller must dispose)
        public FbConnection GetConnection()
        {
            var conn = new FbConnection(ConnectionString);
            conn.Open();
            return conn;
        }

        // SELECT query results
        public List<Dictionary<string, object>> ExecuteSelect(string sql)
        {
            var results = new List<Dictionary<string, object>>();

            using (var conn = new FbConnection(ConnectionString))
            {
                conn.Open();

                using (var cmd = new FbCommand(sql, conn))
                using (var reader = cmd.ExecuteReader())
                {
                    while (reader.Read())
                    {
                        var row = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                        for (int i = 0; i < reader.FieldCount; i++)
                        {
                            row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
                        }
                        results.Add(row);
                    }
                }
            }

            return results;
        }

        // INSERT/UPDATE/DELETE
        public int ExecuteNonQuery(string sql)
        {
            try
            {
                // Do not log SQL with sensitive data
                Console.WriteLine("[DB QUERY] Executing non-query.");

                using (var conn = new FbConnection(ConnectionString))
                {
                    conn.Open();
                    Console.WriteLine("[DB] Connection opened successfully");

                    using (var cmd = new FbCommand(sql, conn))
                    {
                        int rowsAffected = cmd.ExecuteNonQuery();
                        Console.WriteLine($"[DB] Query executed successfully. Rows affected: {rowsAffected}");
                        return rowsAffected;
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("[DB ERROR] Failed to execute non-query.");
                Console.WriteLine($"[DB ERROR] Error: {ex.Message}");
                throw;
            }
        }
    }
}
