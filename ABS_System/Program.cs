using Microsoft.EntityFrameworkCore;
using YourApp.Data;        // AppDbContext + FirebirdDb + DbInitializer
using FirebirdWeb.Helpers; // DbHelper (your existing helper)

var builder = WebApplication.CreateBuilder(args);

// =========================
// Services
// =========================
builder.Services.AddControllersWithViews();

// ✅ SQL Server (EF Core)
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// ✅ Existing helper (keep if used elsewhere)
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
        // ✅ Branding / company header-footer first
        init.EnsureTenantSchema();

        // Columns / master tables
        init.EnsureAgentEmailColumn();
        init.EnsureAgentBranchNoColumn();

        // BRANCH table + FK
        init.EnsureBranchSchema();

        // Appointment + detail table
        init.EnsureAppointmentSchema();

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
app.UseAuthorization();

// ✅ Default route
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
