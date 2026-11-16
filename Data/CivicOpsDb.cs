using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace CivicOps.Web.Data;

public class CivicOpsDb(DbContextOptions<CivicOpsDb> options) : IdentityDbContext<ApplicationUser>(options)
{
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<SlaRule> SlaRules => Set<SlaRule>();
    public DbSet<District> Districts => Set<District>();
    public DbSet<ServiceRequest> Requests => Set<ServiceRequest>();
    public DbSet<RequestEvent> Events => Set<RequestEvent>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        base.OnModelCreating(b);

        b.Entity<ApplicationUser>().Property(u => u.DisplayName).HasMaxLength(100);

        b.Entity<Category>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(80);
            e.Property(x => x.Department).HasMaxLength(80);
            e.HasIndex(x => x.Name).IsUnique();
        });
        b.Entity<SlaRule>(e =>
        {
            e.HasIndex(x => new { x.CategoryId, x.Priority }).IsUnique();
            e.HasOne(x => x.Category).WithMany(c => c.SlaRules).HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<District>(e =>
        {
            e.Property(x => x.Code).HasMaxLength(8);
            e.Property(x => x.Name).HasMaxLength(60);
        });
        b.Entity<ServiceRequest>(e =>
        {
            e.Property(x => x.Number).HasMaxLength(20);
            e.Property(x => x.Title).HasMaxLength(160);
            e.Property(x => x.Description).HasMaxLength(2000);
            e.Property(x => x.Address).HasMaxLength(160);
            e.Property(x => x.RequesterName).HasMaxLength(100);
            e.HasIndex(x => x.Number).IsUnique();
            e.HasIndex(x => x.Status);
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.DueAt);
            e.HasOne(x => x.Category).WithMany().HasForeignKey(x => x.CategoryId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.District).WithMany().HasForeignKey(x => x.DistrictId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.Assignee).WithMany().HasForeignKey(x => x.AssigneeId).OnDelete(DeleteBehavior.SetNull);
        });
        b.Entity<RequestEvent>(e =>
        {
            e.Property(x => x.Actor).HasMaxLength(100);
            e.Property(x => x.Note).HasMaxLength(1000);
            e.Property(x => x.ActorId).HasMaxLength(450);
            e.HasOne(x => x.ServiceRequest).WithMany(r => r.Events).HasForeignKey(x => x.ServiceRequestId).OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<AuditLog>(e =>
        {
            e.Property(x => x.ActorId).HasMaxLength(450);
            e.Property(x => x.ActorName).HasMaxLength(100);
            e.Property(x => x.Action).HasMaxLength(60);
            e.Property(x => x.EntityType).HasMaxLength(40);
            e.Property(x => x.EntityKey).HasMaxLength(100);
            e.Property(x => x.Details).HasMaxLength(1000);
            e.HasIndex(x => x.At);
        });
    }
}
