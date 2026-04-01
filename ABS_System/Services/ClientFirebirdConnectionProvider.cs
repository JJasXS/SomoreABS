using FirebirdSql.Data.FirebirdClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using YourApp.Models;

namespace YourApp.Services;

public sealed class ClientFirebirdConnectionProvider : IClientFirebirdConnectionProvider
{
    private readonly ActivationOptions _activation;
    private readonly ClientFirebirdOptions _clientFb;
    private readonly IConfiguration _config;
    private readonly IActivationValidationService _activationValidation;

    public ClientFirebirdConnectionProvider(
        IOptions<ActivationOptions> activation,
        IOptions<ClientFirebirdOptions> clientFb,
        IConfiguration config,
        IActivationValidationService activationValidation)
    {
        _activation = activation.Value;
        _clientFb = clientFb.Value;
        _config = config;
        _activationValidation = activationValidation;
    }

    public string GetConnectionString()
    {
        if (!_activation.Enabled)
            return BuildFromConfigurationFallback();

        if (!_activationValidation.IsActivationValid)
            throw new InvalidOperationException(
                "Client Firebird is not available until activation succeeds. Submit a valid activation code on the blocked page.");

        var tenant = _activationValidation.ActivatedTenant;
        var profile = tenant?.ClientDatabase;
        var path = profile?.DatabasePath?.Trim();
        if (string.IsNullOrEmpty(path))
            throw new InvalidOperationException(
                "Activation succeeded but TENANT_DB_PROFILE has no DB_PATH_ENC for this license. Configure the profile in the Activation database.");

        var dataSource = string.IsNullOrWhiteSpace(profile!.DataSource) ? _clientFb.Server : profile.DataSource.Trim();
        var port = profile.Port ?? _clientFb.Port;
        var user = string.IsNullOrWhiteSpace(profile.User) ? _clientFb.User : profile.User.Trim();
        var password = string.IsNullOrWhiteSpace(profile.Password) ? _clientFb.Password : profile.Password;

        var b = new FbConnectionStringBuilder
        {
            Database = path,
            DataSource = dataSource,
            Port = port,
            UserID = user,
            Password = password,
            Charset = _clientFb.Charset,
            Dialect = _clientFb.Dialect
        };
        return b.ConnectionString;
    }

    private string BuildFromConfigurationFallback()
    {
        var fromConn = _config.GetConnectionString("Firebird");
        if (!string.IsNullOrWhiteSpace(fromConn))
            return fromConn!;

        if (string.IsNullOrWhiteSpace(_clientFb.Database))
            throw new InvalidOperationException(
                "Set ConnectionStrings:Firebird or Firebird:Database when Activation:Enabled is false.");

        var b = new FbConnectionStringBuilder
        {
            DataSource = _clientFb.Server,
            Port = _clientFb.Port,
            Database = _clientFb.Database,
            UserID = _clientFb.User,
            Password = _clientFb.Password,
            Charset = _clientFb.Charset,
            Dialect = _clientFb.Dialect
        };
        return b.ConnectionString;
    }
}
