using Microsoft.EntityFrameworkCore;                 //@jasch_04
using Microsoft.Extensions.Logging;                  // startup logging (Windows Service)
using YourApp.Data;                                  //@jasch_04 // AppDbContext + FirebirdDb + DbInitializer
using YourApp.Middleware;                            // activation gate
using YourApp.Services;                              // activation validation
using FirebirdWeb.Helpers;                           //@jasch_04 // DbHelper
using Microsoft.AspNetCore.Authentication.Cookies;   //@jasch_04
using QuestPDF.Infrastructure;                       //@jasch_04

// CLI: dotnet run -- --print-machine-fingerprint (matches Activation LAAS Python SDK fingerprint)
if (args.Any(a => string.Equals(a, "--print-machine-fingerprint", StringComparison.Ordinal)))
{
    Console.WriteLine(MachineFingerprint.Compute());
    return;
}

var builder = WebApplication.CreateBuilder(args);    //@jasch_04
QuestPDF.Settings.License = LicenseType.Community;   //@jasch_04 // Set QuestPDF license type
builder.Host.UseWindowsService();


// =========================                                //@jasch_04
// Services                                                //@jasch_04
// =========================                                //@jasch_04
builder.Services.AddScoped<YourApp.Filters.TenantBrandingFilter>(); //@jasch_04
builder.Services.AddControllersWithViews(options =>                //@jasch_04
{                                                                   //@jasch_04
    options.Filters.AddService<YourApp.Filters.TenantBrandingFilter>(); //@jasch_04
});                                                                  //@jasch_04

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme) //@jasch_04
    .AddCookie(options =>                                                             //@jasch_04
    {
        options.LoginPath = "/Account/Login";                                          //@jasch_04
        options.AccessDeniedPath = "/Account/Login";                                   //@jasch_04
        options.ExpireTimeSpan = TimeSpan.FromHours(8);                                //@jasch_04
        options.SlidingExpiration = true;                                              //@jasch_04
        options.Cookie.HttpOnly = true; // Prevent JS access
        options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.Always; // Only send over HTTPS
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict; // Prevent CSRF
    });                                                                                //@jasch_04


builder.Services.AddAuthorization();                            //@jasch_04
builder.Services.AddSession();                                  //@jasch_04
builder.Services.AddHttpContextAccessor();                       //@jasch_04

// ✅ SQL Server (EF Core)                                      //@jasch_04
// Factory is singleton; scoped AppDbContext is created from the factory so DbContextOptions stays singleton-compatible.
var sqlConn = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(sqlConn));
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

// ✅ Existing helper                                           //@jasch_04
builder.Services.AddSingleton<DbHelper>();                      //@jasch_04

// ✅ Client Firebird: path from TENANT_DB_PROFILE when Activation is enabled; else ConnectionStrings:Firebird / Firebird:
builder.Services.Configure<ClientFirebirdOptions>(builder.Configuration.GetSection(ClientFirebirdOptions.SectionName));
builder.Services.AddSingleton<IClientFirebirdConnectionProvider, ClientFirebirdConnectionProvider>();

// ✅ Firebird helper + schema initializer                       //@jasch_04
builder.Services.AddSingleton<FirebirdDb>();                    //@jasch_04
builder.Services.AddSingleton<DbInitializer>();                 //@jasch_04

// ✅ License activation (ACTIVATION.FDB — separate from main Firebird DB)
builder.Services.Configure<ActivationOptions>(builder.Configuration.GetSection(ActivationOptions.SectionName));
builder.Services.AddSingleton<IActivationValidationService, ActivationValidationService>();

var app = builder.Build();                                      //@jasch_04

// =========================                                   //@jasch_04
// Startup: activation first, then client Firebird only if allowed (@jasch_04)
// - Activation DB (ACTIVATION.FDB) is opened only inside IActivationValidationService.
// - Tenant/client Firebird schema init must NOT run until IsActivationValid (or Activation disabled).
// - No unhandled exceptions: Windows Service must not fail with 1053 during pending activation.
// =========================                                   //@jasch_04
{
    var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
    var startupLogger = loggerFactory.CreateLogger("Startup");

    try
    {
        using var activationScope = app.Services.CreateScope();
        var activation = activationScope.ServiceProvider.GetRequiredService<IActivationValidationService>();
        activation.ValidateAsync(CancellationToken.None).GetAwaiter().GetResult();
    }
    catch (Exception ex)
    {
        startupLogger.LogError(ex,
            "Activation validation threw unexpectedly; continuing with activation treated as invalid.");
    }

    using var scope = app.Services.CreateScope();
    var activationSvc = scope.ServiceProvider.GetRequiredService<IActivationValidationService>();
    var dbInit = scope.ServiceProvider.GetRequiredService<DbInitializer>();

    if (!activationSvc.IsActivationValid)
    {
        Console.WriteLine("====================================================");
        Console.WriteLine("[DBINIT] Skipped: client Firebird — activation not valid (or validation error).");
        Console.WriteLine("====================================================");
        startupLogger.LogInformation(
            "Client Firebird schema init skipped. When Activation:Enabled, complete activation at /Activation/Blocked.");
    }
    else
    {
        Console.WriteLine("====================================================");
        Console.WriteLine("[DBINIT] Client Firebird schema init starting...");
        Console.WriteLine("====================================================");

        try
        {
            dbInit.EnsureAllStartupSchemas();
            Console.WriteLine("====================================================");
            Console.WriteLine("[DBINIT] Firebird schema ensured successfully.");
            Console.WriteLine("====================================================");
            startupLogger.LogInformation("Client Firebird schema initialization completed.");
        }
        catch (Exception ex)
        {
            Console.WriteLine("====================================================");
            Console.WriteLine("[DBINIT] Client Firebird schema init failed (app continues):");
            Console.WriteLine(ex.ToString());
            Console.WriteLine("====================================================");
            startupLogger.LogError(ex,
                "Client Firebird schema initialization failed; app continues. Fix configuration and restart.");
        }
    }
}

// =========================                                   //@jasch_04
// Middleware pipeline                                          //@jasch_04
// =========================                                   //@jasch_04
if (!app.Environment.IsDevelopment())                           //@jasch_04
{                                                               //@jasch_04
    app.UseExceptionHandler("/Home/Error");                     //@jasch_04
    app.UseHsts();                                              //@jasch_04
}                                                               //@jasch_04

app.UseHttpsRedirection();                                      //@jasch_04
app.UseStaticFiles();                                           //@jasch_04


app.UseRouting();                                               //@jasch_04
app.UseMiddleware<ActivationGateMiddleware>();
app.UseSession();                                               //@jasch_04

app.UseAuthentication();                                        //@jasch_04
app.UseAuthorization();                                         //@jasch_04

// Default route                                                //@jasch_04
app.MapControllerRoute(                                         //@jasch_04
    name: "default",                                            //@jasch_04
    pattern: "{controller=Home}/{action=Index}/{id?}");          //@jasch_04

app.Run();                                                      //@jasch_04
