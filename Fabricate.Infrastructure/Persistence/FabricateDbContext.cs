using Fabricate.Domain.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Fabricate.Infrastructure.Persistence;

public sealed class FabricateDbContext(DbContextOptions<FabricateDbContext> options) : DbContext(options)
{
    public DbSet<Account> Accounts => Set<Account>();
    public DbSet<AccountMembership> AccountMemberships => Set<AccountMembership>();
    public DbSet<UserProfile> UserProfiles => Set<UserProfile>();
    public DbSet<Invitation> Invitations => Set<Invitation>();
    public DbSet<AccountGroup> AccountGroups => Set<AccountGroup>();
    public DbSet<GroupMembership> GroupMemberships => Set<GroupMembership>();
    public DbSet<AllowedDomain> AllowedDomains => Set<AllowedDomain>();
    public DbSet<AuditEvent> AuditEvents => Set<AuditEvent>();
    public DbSet<DatasetRun> DatasetRuns => Set<DatasetRun>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ToolInvocation> ToolInvocations => Set<ToolInvocation>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();

    // Store all DateTimeOffset values as UTC ticks (long) so SQLite ORDER BY works correctly.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.Properties<DateTimeOffset>()
            .HaveConversion<DateTimeOffsetToBinaryConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Account>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<AccountMembership>(e =>
        {
            e.HasKey(m => new { m.AccountId, m.UserId });
        });

        modelBuilder.Entity<UserProfile>(e =>
        {
            e.HasKey(u => u.UserId);
            e.Property(u => u.Email).IsRequired().HasMaxLength(320);
            e.HasIndex(u => u.Email).IsUnique();
            e.Property(u => u.DisplayName).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<Invitation>(e =>
        {
            e.HasKey(i => i.Id);
            e.Property(i => i.Token).IsRequired().HasMaxLength(200);
            e.HasIndex(i => i.Token).IsUnique();
            e.Property(i => i.InviteeEmail).IsRequired().HasMaxLength(320);
        });

        modelBuilder.Entity<AccountGroup>(e =>
        {
            e.HasKey(g => g.Id);
            e.Property(g => g.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<GroupMembership>(e =>
        {
            e.HasKey(m => new { m.GroupId, m.UserId });
        });

        modelBuilder.Entity<AllowedDomain>(e =>
        {
            e.HasKey(d => d.Id);
            e.Property(d => d.Domain).IsRequired().HasMaxLength(255);
        });

        // AuditEvent — insert-only by convention
        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.HasKey(a => a.Id);
            e.Property(a => a.Action).IsRequired().HasMaxLength(200);
            e.Property(a => a.CorrelationId).IsRequired().HasMaxLength(100);
            e.Property(a => a.TargetType).HasMaxLength(100);
            e.Property(a => a.TargetId).HasMaxLength(100);
        });

        // DatasetRun — store JSON collections as strings
        modelBuilder.Entity<DatasetRun>(e =>
        {
            e.HasKey(r => r.Id);
            e.Property(r => r.RequestedRowCounts)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, int>>(v, (System.Text.Json.JsonSerializerOptions?)null)!);
            e.Property(r => r.ArtifactChecksums)
                .HasConversion(
                    v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => v == null ? null : System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(v, (System.Text.Json.JsonSerializerOptions?)null));
            e.Property(r => r.ArtifactPaths)
                .HasConversion(
                    v => v == null ? null : System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => v == null ? null : System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null));
        });

        modelBuilder.Entity<ChatSession>(e =>
        {
            e.HasKey(s => s.Id);
            e.Property(s => s.Name).IsRequired().HasMaxLength(200);
        });

        modelBuilder.Entity<ChatMessage>(e =>
        {
            e.HasKey(m => m.Id);
            e.Property(m => m.Content).IsRequired();
        });

        modelBuilder.Entity<ToolInvocation>(e =>
        {
            e.HasKey(t => t.Id);
            e.Property(t => t.ToolName).IsRequired().HasMaxLength(200);
        });

        // ApiKey — HashedSecret only, scopes as JSON
        modelBuilder.Entity<ApiKey>(e =>
        {
            e.HasKey(k => k.Id);
            e.Property(k => k.Name).IsRequired().HasMaxLength(200);
            e.Property(k => k.HashedSecret).IsRequired().HasMaxLength(200);
            e.HasIndex(k => k.HashedSecret).IsUnique();
            e.Property(k => k.Scopes)
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<List<string>>(v, (System.Text.Json.JsonSerializerOptions?)null)!);
        });
    }
}

internal sealed class DateTimeOffsetToTicksConverter()
    : ValueConverter<DateTimeOffset, long>(
        v => v.UtcTicks,
        v => new DateTimeOffset(v, TimeSpan.Zero));
