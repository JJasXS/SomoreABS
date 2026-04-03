using System.Data;
using FirebirdSql.Data.FirebirdClient;
using YourApp.Services;

namespace YourApp.Data
{
    public class FirebirdDb
    {
        private readonly IClientFirebirdConnectionProvider _connectionProvider;

        public FirebirdDb(IClientFirebirdConnectionProvider connectionProvider)
        {
            _connectionProvider = connectionProvider;
        }

        public FbConnection Open()
        {
            var cs = _connectionProvider.GetConnectionString();
            var conn = new FbConnection(cs);
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
