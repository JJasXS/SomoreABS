using Microsoft.EntityFrameworkCore;
using YourApp.Data;        // AppDbContext + FirebirdDb + DbInitializer
using FirebirdWeb.Helpers; // DbHelper
using Microsoft.AspNetCore.Authentication.Cookies;
using QuestPDF.Infrastructure;

var builder = WebApplication.CreateBuilder(args);
QuestPDF.Settings.License = LicenseType.Community; // Set QuestPDF license type

// =========================
// Services
// =========================
builder.Services.AddControllersWithViews();

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/Login";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
    });

builder.Services.AddAuthorization();

// ✅ SQL Server (EF Core)
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// ✅ Existing helper
builder.Services.AddSingleton<DbHelper>();

// ✅ Firebird helper + schema initializer
builder.Services.AddSingleton<FirebirdDb>();
builder.Services.AddSingleton<DbInitializer>();

var app = builder.Build();

// =========================
// Firebird schema init (runs once at startup)
// =========================
using (var scope = app.Services.CreateScope())
{
    var init = scope.ServiceProvider.GetRequiredService<DbInitializer>();

    Console.WriteLine("====================================================");
    Console.WriteLine("[DBINIT] Firebird schema init starting...");
    Console.WriteLine("====================================================");

    try
    {
        // Branding / company header-footer
        init.EnsureTenantSchema();

        // Columns / master tables
        init.EnsureAgentEmailColumn();
        init.EnsureAgentBranchNoColumn();

        // BRANCH table + FK
        init.EnsureBranchSchema();

        // Appointment + detail table
        init.EnsureAppointmentSchema();

        // ✅ Customer signature proof table
        init.EnsureApptSignatureSchema();

        // ✅ NEW: Sales Order Detail extra fields
        init.EnsureSalesOrderDetailClaimColumns();

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

// =========================
// Middleware pipeline
// =========================
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

// Default route
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
