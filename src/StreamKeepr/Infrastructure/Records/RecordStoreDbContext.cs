using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace StreamKeepr.Infrastructure.Records;

/// <summary>
/// The record store's EF Core model (ADR-0014). Contexts are short-lived and come from
/// <see cref="IDbContextFactory{TContext}"/>; never hold one across awaits that outlive a call.
/// </summary>
public sealed class RecordStoreDbContext(DbContextOptions<RecordStoreDbContext> options)
    : DbContext(options)
{
    public DbSet<ChannelRow> Channels => Set<ChannelRow>();

    public DbSet<AppliedSeedRow> AppliedSeeds => Set<AppliedSeedRow>();

    public DbSet<SessionRow> Sessions => Set<SessionRow>();

    public DbSet<SessionMetadataChangeRow> SessionMetadataChanges => Set<SessionMetadataChangeRow>();

    public DbSet<ChatSessionRow> ChatSessions => Set<ChatSessionRow>();

    public DbSet<ChatMessageRow> ChatMessages => Set<ChatMessageRow>();

    public DbSet<ChatEventRow> ChatEvents => Set<ChatEventRow>();

    public DbSet<ViewerSampleRow> ViewerSamples => Set<ViewerSampleRow>();

    public DbSet<ResumePositionRow> ResumePositions => Set<ResumePositionRow>();

    public DbSet<AdminTokenRow> AdminTokens => Set<AdminTokenRow>();

    public DbSet<EventSubConduitRow> EventSubConduits => Set<EventSubConduitRow>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // SQLite has no date type, and EF cannot order or compare DateTimeOffset stored as text.
        // UTC ticks are exact, sortable, and compare correctly in SQL.
        configurationBuilder.Properties<DateTimeOffset>().HaveConversion<UtcTicksConverter>();
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ChannelRow>(channel =>
        {
            channel.ToTable("Channels");
            channel.HasKey(row => row.Login);
            channel.HasIndex(row => row.Position);
        });

        modelBuilder.Entity<AppliedSeedRow>(seed =>
        {
            seed.ToTable("AppliedSeeds");
            seed.HasKey(row => row.Name);
        });

        // Sessions keep their channel login but no foreign key to Channels: removing a watched
        // channel must never remove its recordings.
        modelBuilder.Entity<SessionRow>(session =>
        {
            session.ToTable("Sessions");
            session.HasKey(row => row.Id);
            session.Property(row => row.Status).HasConversion<string>();
            session.HasIndex(row => new { row.Channel, row.StartedAt });
            session.HasIndex(row => row.Status);
        });

        modelBuilder.Entity<SessionMetadataChangeRow>(change =>
        {
            change.ToTable("SessionMetadataChanges");
            change.HasKey(row => row.Id);
            change.HasOne<SessionRow>().WithMany()
                .HasForeignKey(row => row.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            change.HasIndex(row => new { row.SessionId, row.At });
        });

        modelBuilder.Entity<ChatSessionRow>(chat =>
        {
            chat.ToTable("ChatSessions");
            chat.HasKey(row => row.SessionId);
            chat.HasOne<SessionRow>().WithOne()
                .HasForeignKey<ChatSessionRow>(row => row.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatMessageRow>(message =>
        {
            message.ToTable("ChatMessages");
            message.HasKey(row => row.Id);
            message.HasOne<ChatSessionRow>().WithMany()
                .HasForeignKey(row => row.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            message.HasIndex(row => new { row.SessionId, row.At });
            message.HasIndex(row => new { row.SessionId, row.MessageId });
        });

        modelBuilder.Entity<ChatEventRow>(chatEvent =>
        {
            chatEvent.ToTable("ChatEvents");
            chatEvent.HasKey(row => row.Id);
            chatEvent.HasOne<ChatSessionRow>().WithMany()
                .HasForeignKey(row => row.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            chatEvent.HasIndex(row => new { row.SessionId, row.At });
        });

        modelBuilder.Entity<ViewerSampleRow>(sample =>
        {
            sample.ToTable("ViewerSamples");
            sample.HasKey(row => row.Id);
            sample.HasOne<SessionRow>().WithMany()
                .HasForeignKey(row => row.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            sample.HasIndex(row => new { row.SessionId, row.OffsetSeconds });
        });

        modelBuilder.Entity<ResumePositionRow>(position =>
        {
            position.ToTable("ResumePositions");
            position.HasKey(row => row.SessionId);
            position.HasOne<SessionRow>().WithOne()
                .HasForeignKey<ResumePositionRow>(row => row.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AdminTokenRow>(token =>
        {
            token.ToTable("AdminToken", table => table.HasCheckConstraint(
                "CK_AdminToken_Singleton", $"\"Id\" = {AdminTokenRow.SingletonId}"));
            token.HasKey(row => row.Id);
            token.Property(row => row.Id).ValueGeneratedNever();
        });

        modelBuilder.Entity<EventSubConduitRow>(conduit =>
        {
            conduit.ToTable("EventSubConduit", table => table.HasCheckConstraint(
                "CK_EventSubConduit_Singleton", $"\"Id\" = {EventSubConduitRow.SingletonId}"));
            conduit.HasKey(row => row.Id);
            conduit.Property(row => row.Id).ValueGeneratedNever();
        });
    }

    /// <summary>Stores a <see cref="DateTimeOffset"/> as UTC ticks; values read back are UTC.</summary>
    private sealed class UtcTicksConverter() : ValueConverter<DateTimeOffset, long>(
        value => value.UtcTicks,
        ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
}
