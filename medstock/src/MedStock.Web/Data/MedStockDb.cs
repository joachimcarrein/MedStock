using MedStock.Web.Domain;
using Microsoft.EntityFrameworkCore;

namespace MedStock.Web.Data;

public class MedStockDb(DbContextOptions<MedStockDb> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserSettings> UserSettings => Set<UserSettings>();
    public DbSet<Profile> Profiles => Set<Profile>();
    public DbSet<Medication> Medications => Set<Medication>();
    public DbSet<Snapshot> Snapshots => Set<Snapshot>();
    public DbSet<NotificationLog> NotificationLogs => Set<NotificationLog>();
    public DbSet<GlobalSettings> GlobalSettings => Set<GlobalSettings>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.DisplayName).HasMaxLength(200);
            e.HasOne(x => x.Settings).WithOne().HasForeignKey<UserSettings>(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<UserSettings>(e =>
        {
            e.HasKey(x => x.UserId);
            e.Property(x => x.NotificationsEnabled).HasDefaultValue(true);
        });
        b.Entity<Profile>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Color).HasMaxLength(9);
        });
        b.Entity<Medication>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Strength).HasMaxLength(100);
            e.Property(x => x.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.UsageMode).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.NotificationsEnabled).HasDefaultValue(true);
            e.HasOne(x => x.Profile).WithMany(p => p.Medications).HasForeignKey(x => x.ProfileId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(x => x.Snapshots).WithOne().HasForeignKey(s => s.MedicationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<Snapshot>(e =>
        {
            e.Property(x => x.Reason).HasConversion<string>().HasMaxLength(20);
            e.HasIndex(x => new { x.MedicationId, x.Id });
        });
        b.Entity<NotificationLog>(e =>
        {
            e.HasIndex(x => new { x.MedicationId, x.Level }).IsUnique();
            e.HasOne<Medication>().WithMany().HasForeignKey(x => x.MedicationId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<GlobalSettings>(e =>
        {
            e.Property(x => x.Id).ValueGeneratedNever();
            e.Property(x => x.WrittenByVersion).HasMaxLength(50);
        });
    }
}
