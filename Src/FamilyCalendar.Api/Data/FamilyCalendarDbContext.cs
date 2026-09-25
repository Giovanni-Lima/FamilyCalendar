using FamilyCalendar.Domain;
using Microsoft.EntityFrameworkCore;

namespace FamilyCalendar.Data;

public class FamilyCalendarDbContext : DbContext
{
    public FamilyCalendarDbContext(DbContextOptions<FamilyCalendarDbContext> options) : base(options)
    {
    }

    public DbSet<Member> Members => Set<Member>();
    public DbSet<Label> Labels => Set<Label>();
    public DbSet<TaskItem> Tasks => Set<TaskItem>();
    public DbSet<PushSubscription> PushSubscriptions => Set<PushSubscription>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Member>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.DisplayName).IsRequired().HasMaxLength(100);
            e.HasIndex(m => m.DisplayName).IsUnique();
            e.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
        });

        b.Entity<Label>(e =>
        {
            e.HasKey(l => l.Id);
            e.Property(l => l.Name).IsRequired().HasMaxLength(50);
            e.Property(l => l.Color).IsRequired().HasMaxLength(7);
            e.HasData(
                new Label { Id = 1, Name = "Famiglia", Color = "#2fb344" },
                new Label { Id = 2, Name = "Lavoro", Color = "#3b82f6" },
                new Label { Id = 3, Name = "Scuola", Color = "#f2c500" },
                new Label { Id = 4, Name = "Salute", Color = "#e5484d" },
                new Label { Id = 5, Name = "Altro", Color = "#8a8f98" });
        });

        b.Entity<PushSubscription>(e =>
        {
            e.HasKey(p => p.Id);
            e.Property(p => p.Endpoint).IsRequired().HasMaxLength(512);
            e.Property(p => p.P256dh).IsRequired().HasMaxLength(200);
            e.Property(p => p.Auth).IsRequired().HasMaxLength(100);
            e.HasIndex(p => p.Endpoint).IsUnique();
            e.HasOne(p => p.Member).WithMany().HasForeignKey(p => p.MemberId).OnDelete(DeleteBehavior.Cascade);
        });

        b.Entity<TaskItem>(e =>
        {
            e.ToTable("Tasks");
            e.HasKey(t => t.Id);
            e.Property(t => t.Title).IsRequired().HasMaxLength(200);
            e.Property(t => t.Note).HasMaxLength(1000);
            e.Property(t => t.Address).HasMaxLength(300);
            e.HasIndex(t => t.Date);
            e.HasOne(t => t.Label).WithMany().HasForeignKey(t => t.LabelId).OnDelete(DeleteBehavior.SetNull);
            e.HasOne(t => t.CreatedBy).WithMany().HasForeignKey(t => t.CreatedByMemberId).OnDelete(DeleteBehavior.SetNull);
        });
    }
}
