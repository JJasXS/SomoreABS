using System;
using System.Collections.Generic;
using System.Data;
using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;

namespace FirebirdWeb.Helpers
{
    public class DbHelper
    {
        private readonly string _connectionString;

        // ✅ Read from appsettings.json
        public DbHelper(IConfiguration config)
        {
            var dbPath  = config["Firebird:Database"];   // e.g. C:\eStream\SQLAccounting\DB\ACC-PROACC202601.FDB
            var server  = config["Firebird:Server"] ?? "localhost";
            var port    = config["Firebird:Port"] ?? "3050";
            var user    = config["Firebird:User"] ?? "SYSDBA";
            var pass    = config["Firebird:Password"] ?? "masterkey";
            var charset = config["Firebird:Charset"] ?? "UTF8";

            if (string.IsNullOrWhiteSpace(dbPath))
                throw new InvalidOperationException("Firebird:Database is missing in appsettings.json");

            // ✅ Your requested style: Database=localhost:C:\path\file.fdb
            // Firebird accepts this "server:path" syntax.
            var dbValue = $"{server}:{dbPath}";

            _connectionString =
                $"User={user};" +
                $"Password={pass};" +
                $"Database={dbValue};" +
                $"Port={port};" +
                $"Dialect=3;" +
                $"Charset={charset};" +
                $"Pooling=true;";
        }

        // Optional: open and return a connection (caller must dispose)
        public FbConnection GetConnection()
        {
            var conn = new FbConnection(_connectionString);
            conn.Open();
            return conn;
        }

        // SELECT query results
        public List<Dictionary<string, object>> ExecuteSelect(string sql)
        {
            var results = new List<Dictionary<string, object>>();

            using (var conn = new FbConnection(_connectionString))
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
                Console.WriteLine($"[DB QUERY] Executing: {sql}");

                using (var conn = new FbConnection(_connectionString))
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
                Console.WriteLine($"[DB ERROR] Failed to execute query: {sql}");
                Console.WriteLine($"[DB ERROR] Error: {ex.Message}");
                throw;
            }
        }
    }
}
