using System.Data;
using FirebirdSql.Data.FirebirdClient;

namespace YourApp.Data
{
    public class FirebirdDb
    {
        private readonly string _cs;

        public FirebirdDb(IConfiguration config)
        {
            _cs = config.GetConnectionString("Firebird") 
                  ?? throw new Exception("Missing ConnectionStrings:Firebird in appsettings.json");
        }

        public FbConnection Open()
        {
            var conn = new FbConnection(_cs);
            conn.Open();
            return conn;
        }

        public static FbParameter P(string name, object? value, FbDbType type)
        {
            var p = new FbParameter(name, type);
            p.Value = value ?? DBNull.Value;
            return p;
        }
    }
}
