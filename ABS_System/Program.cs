using Microsoft.EntityFrameworkCore;
using YourApp.Data;            // AppDbContext + FirebirdDb + DbInitializer
using FirebirdWeb.Helpers;     // DbHelper (your existing helper)

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();

// ✅ SQL Server (your existing EF DbContext)
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

// ✅ Your existing helper (keep it if other parts use it)
builder.Services.AddSingleton<DbHelper>();

// ✅ Firebird DB helper + Schema initializer (NEW)
builder.Services.AddSingleton<FirebirdDb>();
builder.Services.AddSingleton<DbInitializer>();

var app = builder.Build();

// ✅ Create APPOINTMENT table if missing (runs once at startup)
using (var scope = app.Services.CreateScope())
{
    var init = scope.ServiceProvider.GetRequiredService<DbInitializer>();
    init.EnsureAppointmentSchema();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles(); // serve wwwroot

app.UseRouting();

app.UseAuthorization();

// ✅ Keep your existing default route (Home/Index)
// (You can still access Appointment pages via /Appointment)
app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

app.Run();
