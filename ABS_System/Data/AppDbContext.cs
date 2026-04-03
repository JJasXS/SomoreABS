using Microsoft.EntityFrameworkCore;
using YourApp.Models;

namespace YourApp.Data
{
    public class AppDbContext : DbContext
    {
        public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

        public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();
        public DbSet<YourApp.Models.ST_ITEM> ST_ITEMs => Set<YourApp.Models.ST_ITEM>();
        public DbSet<LocalDeploymentInfo> LocalDeploymentInfos => Set<LocalDeploymentInfo>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<LocalDeploymentInfo>(e =>
            {
                e.ToTable("LocalDeploymentInfo");
                e.HasKey(x => x.Id);
                e.Property(x => x.MachineFingerprintHex).HasMaxLength(64).IsRequired();
            });
        }
    }
}
