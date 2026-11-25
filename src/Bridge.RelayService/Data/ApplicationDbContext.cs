using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Bridge.RelayService.Data;

/// <summary>Identity + OpenIddict stores in one SQLite database (WAL).
/// Production deployments use migrations; v1 local/dev runs use
/// EnsureCreated (see Program.cs) and the schema is small and owned
/// by this service alone.
/// </summary>
public sealed class ApplicationDbContext : IdentityDbContext<ApplicationUser>
{
    public DbSet<Device> Devices { get; set; } = null!;
    public DbSet<DevicePairing> DevicePairings { get; set; } = null!;
    public DbSet<ConsentSession> ConsentSessions { get; set; } = null!;
    public DbSet<ControlLease> ControlLeases { get; set; } = null!;
    public DbSet<CommandRecord> CommandRecords { get; set; } = null!;

    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        builder.UseOpenIddict();
        builder.Entity<DevicePairing>()
            .HasIndex(p => p.UserCode)
            .IsUnique();
        builder.Entity<Device>()
            .HasIndex(d => d.OwnerId);
        builder.Entity<Device>()
            .HasIndex(d => d.KeyFingerprint);
    }
}
