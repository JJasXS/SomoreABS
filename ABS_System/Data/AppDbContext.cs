using Microsoft.EntityFrameworkCore;
using YourApp.Models;

namespace YourApp.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
        public DbSet<YourApp.Models.ST_ITEM> ST_ITEMs => Set<YourApp.Models.ST_ITEM>();
    }
}
