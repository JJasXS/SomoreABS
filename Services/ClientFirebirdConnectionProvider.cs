using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace YourApp.Services;

public sealed class ClientFirebirdConnectionProvider : IClientFirebirdConnectionProvider
{
    private readonly ClientFirebirdOptions _clientFb;
    private readonly IConfiguration _config;

    public ClientFirebirdConnectionProvider(
        IOptions<ClientFirebirdOptions> clientFb,
        IConfiguration config)
    {
        _clientFb = clientFb.Value;
        _config = config;
    }

    public string GetConnectionString()
    {
        return BuildFromConfiguration();
    }

    private string BuildFromConfiguration()
    {
        var fromConn = _config.GetConnectionString("Firebird");
        if (!string.IsNullOrWhiteSpace(fromConn))
            return fromConn!;

        if (string.IsNullOrWhiteSpace(_clientFb.Database))
            throw new InvalidOperationException(
                "Set ConnectionStrings:Firebird or Firebird:Database in configuration.");

        var user = string.IsNullOrWhiteSpace(_clientFb.User) ? "SYSDBA" : _clientFb.User;
        var password = string.IsNullOrWhiteSpace(_clientFb.Password) ? "masterkey" : _clientFb.Password;

        var b = new FbConnectionStringBuilder
        {
            DataSource = _clientFb.Server,
            Port = _clientFb.Port,
            Database = _clientFb.Database,
            UserID = user,
            Password = password,
            Charset = _clientFb.Charset,
            Dialect = _clientFb.Dialect
        };
        return b.ConnectionString;
    }
}
