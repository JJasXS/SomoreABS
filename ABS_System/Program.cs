using Microsoft.EntityFrameworkCore;                 //@jasch_04
using YourApp.Data;                                  //@jasch_04 // AppDbContext + FirebirdDb + DbInitializer
using YourApp.Middleware;                            // activation gate
using YourApp.Services;                              // activation validation
using FirebirdWeb.Helpers;                           //@jasch_04 // DbHelper
using Microsoft.AspNetCore.Authentication.Cookies;   //@jasch_04
using QuestPDF.Infrastructure;                       //@jasch_04

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

// ✅ SQL Server (EF Core)                                      //@jasch_04
builder.Services.AddDbContext<AppDbContext>(options =>          //@jasch_04
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection"))); //@jasch_04

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

// Validate activation at startup (same rules as per-request gate; refreshes snapshot)
using (var activationScope = app.Services.CreateScope())
{
    var activation = activationScope.ServiceProvider.GetRequiredService<IActivationValidationService>();
    activation.ValidateAsync().GetAwaiter().GetResult();
}

// =========================                                   //@jasch_04
// Firebird schema init (runs once at startup)                  //@jasch_04
// =========================                                   //@jasch_04
{
    var activationEnabled = app.Configuration.GetValue<bool>("Activation:Enabled");
    using var activationScope = app.Services.CreateScope();
    var activation = activationScope.ServiceProvider.GetRequiredService<IActivationValidationService>();
    var skipClientDbInit = activationEnabled && !activation.IsActivationValid;

    if (skipClientDbInit)
    {
        Console.WriteLine("====================================================");
        Console.WriteLine("[DBINIT] Skipped: activation is enabled but not yet valid (no client Firebird path).");
        Console.WriteLine("====================================================");
    }
    else
    {
        using var scope = app.Services.CreateScope();
        var init = scope.ServiceProvider.GetRequiredService<DbInitializer>();

        Console.WriteLine("====================================================");
        Console.WriteLine("[DBINIT] Firebird schema init starting...");
        Console.WriteLine("====================================================");

        try
        {
            init.EnsureAllStartupSchemas();

            Console.WriteLine("====================================================");
            Console.WriteLine("[DBINIT] ✅ Firebird schema ensured successfully.");
            Console.WriteLine("====================================================");
        }
        catch (Exception ex)
        {
            Console.WriteLine("====================================================");
            Console.WriteLine("[DBINIT] ❌ Firebird schema init FAILED:");
            Console.WriteLine(ex.ToString());
            Console.WriteLine("====================================================");
            throw;
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
