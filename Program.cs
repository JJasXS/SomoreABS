using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using YourApp.Data;
using YourApp.Services;
using FirebirdWeb.Helpers;
using Microsoft.AspNetCore.Authentication.Cookies;
using QuestPDF.Infrastructure;

// CLI: dotnet run -- --print-machine-fingerprint
if (args.Any(a => string.Equals(a, "--print-machine-fingerprint", StringComparison.Ordinal)))
{
    Console.WriteLine(MachineFingerprint.Compute());
    return;
}

var builder = WebApplication.CreateBuilder(args);
QuestPDF.Settings.License = LicenseType.Community;
builder.Host.UseWindowsService();

builder.Services.AddScoped<YourApp.Filters.TenantBrandingFilter>();
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.AddService<YourApp.Filters.TenantBrandingFilter>();
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = Microsoft.AspNetCore.Http.CookieSecurePolicy.SameAsRequest;
        options.Cookie.SameSite = Microsoft.AspNetCore.Http.SameSiteMode.Strict;
    });

builder.Services.AddAuthorization();
builder.Services.AddSession();
builder.Services.AddHttpContextAccessor();

var sqlConn = builder.Configuration.GetConnectionString("DefaultConnection");
builder.Services.AddDbContextFactory<AppDbContext>(options =>
    options.UseSqlServer(sqlConn));
builder.Services.AddScoped(sp =>
    sp.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext());

builder.Services.AddSingleton<DbHelper>();

builder.Services.Configure<ClientFirebirdOptions>(builder.Configuration.GetSection(ClientFirebirdOptions.SectionName));
builder.Services.AddSingleton<IClientFirebirdConnectionProvider, ClientFirebirdConnectionProvider>();

builder.Services.AddSingleton<FirebirdDb>();
builder.Services.AddSingleton<DbInitializer>();

var app = builder.Build();

{
    var loggerFactory = app.Services.GetRequiredService<ILoggerFactory>();
    var startupLogger = loggerFactory.CreateLogger("Startup");

    using var scope = app.Services.CreateScope();
    var dbInit = scope.ServiceProvider.GetRequiredService<DbInitializer>();

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

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

// Static files: only wwwroot (e.g. wwwroot/images/somore_logo1.png → /images/somore_logo1.png).
// Do not add a second /images mapping from the project folder — it hides wwwroot/images.
app.UseStaticFiles();

app.UseRouting();
app.UseSession();

app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
