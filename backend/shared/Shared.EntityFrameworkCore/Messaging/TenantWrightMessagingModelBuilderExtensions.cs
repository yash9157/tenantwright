using Microsoft.EntityFrameworkCore;

namespace Shared.EntityFrameworkCore.Messaging;

/// <summary>The provider-neutral messaging tables every service DbContext owns.</summary>
internal static class TenantWrightMessagingTables
{
    public const string Schema = "public";
    public const string Outbox = "MessagingOutboxMessages";
    public const string Inbox = "MessagingInboxRecords";
    public const string ReplayState = "MessagingReplayState";

    public const int ContractIdentityMaxLength = 256;
    public const int EndpointMaxLength = 120;
    public const int LockOwnerMaxLength = 120;
    public const int ErrorTypeMaxLength = 256;
}

public static class TenantWrightMessagingModelBuilderExtensions
{
    /// <summary>
    /// Maps the neutral outbox, inbox and protected-replay state tables. Every service DbContext that hosts messaging
    /// calls this exactly once from <c>OnModelCreating</c>.
    /// </summary>
    public static ModelBuilder AddTenantWrightMessagingEntities(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<MessagingOutboxMessage>(entity =>
        {
            entity.ToTable(TenantWrightMessagingTables.Outbox, TenantWrightMessagingTables.Schema);
            entity.HasKey(message => message.Id);
            entity.Property(message => message.Id).ValueGeneratedNever();
            entity.Property(message => message.ContractIdentity)
                .HasMaxLength(TenantWrightMessagingTables.ContractIdentityMaxLength)
                .IsRequired();
            entity.Property(message => message.Kind).HasConversion<short>().IsRequired();
            entity.Property(message => message.Destination).HasMaxLength(TenantWrightMessagingTables.EndpointMaxLength);
            entity.Property(message => message.Payload).HasColumnType("bytea").IsRequired();
            entity.Property(message => message.Headers).HasColumnType("jsonb").IsRequired();
            entity.Property(message => message.CreatedAtUtc).IsRequired();
            entity.Property(message => message.AvailableAtUtc).IsRequired();
            entity.Property(message => message.Attempts).IsRequired();
            entity.Property(message => message.LockedBy).HasMaxLength(TenantWrightMessagingTables.LockOwnerMaxLength);
            entity.Property(message => message.LastErrorType).HasMaxLength(TenantWrightMessagingTables.ErrorTypeMaxLength);

            // The dispatcher claim scans only pending rows in availability order.
            entity.HasIndex(message => new { message.AvailableAtUtc, message.Id })
                .HasDatabaseName("IX_MessagingOutboxMessages_Pending")
                .HasFilter("\"DispatchedAtUtc\" IS NULL AND \"ParkedAtUtc\" IS NULL");

            // Retention deletes dispatched rows oldest first.
            entity.HasIndex(message => message.DispatchedAtUtc)
                .HasDatabaseName("IX_MessagingOutboxMessages_DispatchedAtUtc")
                .HasFilter("\"DispatchedAtUtc\" IS NOT NULL");

            // Operators list parked rows awaiting a decision.
            entity.HasIndex(message => message.ParkedAtUtc)
                .HasDatabaseName("IX_MessagingOutboxMessages_ParkedAtUtc")
                .HasFilter("\"ParkedAtUtc\" IS NOT NULL");
        });

        modelBuilder.Entity<MessagingInboxRecord>(entity =>
        {
            entity.ToTable(TenantWrightMessagingTables.Inbox, TenantWrightMessagingTables.Schema);
            entity.HasKey(record => new { record.MessageId, record.Endpoint });
            entity.Property(record => record.Endpoint)
                .HasMaxLength(TenantWrightMessagingTables.EndpointMaxLength)
                .IsRequired();
            entity.Property(record => record.ProcessedAtUtc).IsRequired();
            entity.HasIndex(record => record.ProcessedAtUtc)
                .HasDatabaseName("IX_MessagingInboxRecords_ProcessedAtUtc");
        });

        modelBuilder.Entity<TenantWrightReplayState>(entity =>
        {
            entity.ToTable(TenantWrightMessagingTables.ReplayState, TenantWrightMessagingTables.Schema);
            entity.HasKey(state => state.ReplayId);
            entity.Property(state => state.ReplayId).HasMaxLength(120);
            entity.Property(state => state.Destination).HasMaxLength(120).IsRequired();
            entity.Property(state => state.AuditId).HasMaxLength(120).IsRequired();
            entity.Property(state => state.ExpiresAt).IsRequired();
            entity.HasIndex(state => state.ExpiresAt);
        });

        return modelBuilder;
    }
}
