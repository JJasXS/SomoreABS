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

    try
    {
        // Columns / master tables first
        init.EnsureAgentEmailColumn();
        init.EnsureAgentBranchNoColumn();

        // Branch depends on BRANCH table + FK
        init.EnsureBranchSchema();

        // Appointment depends on AGENT + AR_CUSTOMER + creates APPT_DTL
        init.EnsureAppointmentSchema();

        Console.WriteLine("✅ Firebird schema ensured successfully.");
    }
    catch (Exception ex)
    {
        // This will show your "Schema init failed on: <SQL>" message from ExecNonQuery
        Console.WriteLine("❌ Firebird schema init failed:\n" + ex);

        // Stop startup if schema is broken (better than running with half schema)
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
