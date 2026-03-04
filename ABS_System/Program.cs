using Microsoft.EntityFrameworkCore;                 //@jasch_04
using YourApp.Data;                                  //@jasch_04 // AppDbContext + FirebirdDb + DbInitializer
using FirebirdWeb.Helpers;                           //@jasch_04 // DbHelper
using Microsoft.AspNetCore.Authentication.Cookies;   //@jasch_04
using QuestPDF.Infrastructure;                       //@jasch_04

var builder = WebApplication.CreateBuilder(args);    //@jasch_04
QuestPDF.Settings.License = LicenseType.Community;   //@jasch_04 // Set QuestPDF license type


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

// ✅ Firebird helper + schema initializer                       //@jasch_04
builder.Services.AddSingleton<FirebirdDb>();                    //@jasch_04
builder.Services.AddSingleton<DbInitializer>();                 //@jasch_04

var app = builder.Build();                                      //@jasch_04

// =========================                                   //@jasch_04
// Firebird schema init (runs once at startup)                  //@jasch_04
// =========================                                   //@jasch_04
using (var scope = app.Services.CreateScope())                  //@jasch_04
{                                                               //@jasch_04
    var init = scope.ServiceProvider.GetRequiredService<DbInitializer>(); //@jasch_04

    Console.WriteLine("===================================================="); //@jasch_04
    Console.WriteLine("[DBINIT] Firebird schema init starting...");        //@jasch_04
    Console.WriteLine("===================================================="); //@jasch_04

    try                                                        //@jasch_04
    {                                                          //@jasch_04

        // Branding / company header-footer                     //@jasch_04
        init.EnsureTenantSchema();                              //@jasch_04

        // Columns / master tables                              //@jasch_04
        init.EnsureAgentEmailColumn();                          //@jasch_04

        // Branch filtering columns
        init.EnsureAgentUdfBranchColumn();
        init.EnsureAgentUdfBranchNoColumn();

        // Appointment + detail table                            //@jasch_04
        init.EnsureAppointmentSchema();                         //@jasch_04

        // ✅ Customer signature proof table                     //@jasch_04
        init.EnsureApptSignatureSchema();                       //@jasch_04

        // ✅ NEW: Sales Order Detail extra fields               //@jasch_04
        init.EnsureSalesOrderDetailClaimColumns();              //@jasch_04

        // ✅ NEW: Appointment log table for audit/receipts      //@jasch_04
        init.EnsureAppointmentLogTable();                       //@jasch_04

        Console.WriteLine("===================================================="); //@jasch_04
        Console.WriteLine("[DBINIT] ✅ Firebird schema ensured successfully."); //@jasch_04
        Console.WriteLine("===================================================="); //@jasch_04
    }                                                          //@jasch_04
    catch (Exception ex)                                       //@jasch_04
    {                                                          //@jasch_04
        Console.WriteLine("===================================================="); //@jasch_04
        Console.WriteLine("[DBINIT] ❌ Firebird schema init FAILED:");          //@jasch_04
        Console.WriteLine(ex.ToString());                        //@jasch_04
        Console.WriteLine("===================================================="); //@jasch_04
        throw;                                                   //@jasch_04
    }                                                          //@jasch_04
}                                                               //@jasch_04

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
app.UseSession();                                               //@jasch_04

app.UseAuthentication();                                        //@jasch_04
app.UseAuthorization();                                         //@jasch_04

// Default route                                                //@jasch_04
app.MapControllerRoute(                                         //@jasch_04
    name: "default",                                            //@jasch_04
    pattern: "{controller=Home}/{action=Index}/{id?}");          //@jasch_04

app.Run();                                                      //@jasch_04
