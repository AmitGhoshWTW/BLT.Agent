using Microsoft.EntityFrameworkCore;

namespace BLT.Agent.Data;

public class SyncRecord
{
    public string Id              { get; set; } = string.Empty;
    public string RecordType      { get; set; } = string.Empty;
    public string UpdatedAt       { get; set; } = string.Empty;
    public int    Version         { get; set; } = 1;
    public int    IsDeleted       { get; set; } = 0;
    public string SourceBrowserId { get; set; } = string.Empty;
    public string PayloadJson     { get; set; } = string.Empty;
}

public class ChangeLog
{
    public long   SequenceId { get; set; }
    public string RecordId   { get; set; } = string.Empty;
    public string RecordType { get; set; } = string.Empty;
    public string Operation  { get; set; } = string.Empty;
    public string BrowserId  { get; set; } = string.Empty;
    public string Timestamp  { get; set; } = string.Empty;
}

public class SyncMetadata
{
    public string  BrowserId    { get; set; } = string.Empty;
    public string? BrowserName  { get; set; }
    public long    LastSeq      { get; set; } = 0;
    public string? LastSyncTime { get; set; }
    public int     PendingPush  { get; set; } = 0;
    public string  RegisteredAt { get; set; } = string.Empty;
    public string? LastSeenAt   { get; set; }
}

public class ConflictLog
{
    public long    Id            { get; set; }
    public string  RecordId      { get; set; } = string.Empty;
    public string  RecordType    { get; set; } = string.Empty;
    public string  WinnerBrowser { get; set; } = string.Empty;
    public string  LoserBrowser  { get; set; } = string.Empty;
    public string  ConflictedAt  { get; set; } = string.Empty;
    public string? LoserPayload  { get; set; }
}

public class AgentDbContext : DbContext
{
    public AgentDbContext(DbContextOptions<AgentDbContext> opts) : base(opts) { }

    public DbSet<SyncRecord>   SyncRecords  { get; set; }
    public DbSet<ChangeLog>    ChangeLogs   { get; set; }
    public DbSet<SyncMetadata> SyncMetadata { get; set; }
    public DbSet<ConflictLog>  ConflictLogs { get; set; }

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<SyncRecord>(e => {
            e.ToTable("sync_records");
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).HasColumnName("id");
            e.Property(r => r.RecordType).HasColumnName("record_type");
            e.Property(r => r.UpdatedAt).HasColumnName("updated_at");
            e.Property(r => r.Version).HasColumnName("version");
            e.Property(r => r.IsDeleted).HasColumnName("is_deleted");
            e.Property(r => r.SourceBrowserId).HasColumnName("source_browser_id");
            e.Property(r => r.PayloadJson).HasColumnName("payload_json");
        });

        mb.Entity<ChangeLog>(e => {
            e.ToTable("change_log");
            e.HasKey(c => c.SequenceId);
            e.Property(c => c.SequenceId).HasColumnName("sequence_id").ValueGeneratedOnAdd();
            e.Property(c => c.RecordId).HasColumnName("record_id");
            e.Property(c => c.RecordType).HasColumnName("record_type");
            e.Property(c => c.Operation).HasColumnName("operation");
            e.Property(c => c.BrowserId).HasColumnName("browser_id");
            e.Property(c => c.Timestamp).HasColumnName("timestamp");
        });

        mb.Entity<SyncMetadata>(e => {
            e.ToTable("sync_metadata");
            e.HasKey(m => m.BrowserId);
            e.Property(m => m.BrowserId).HasColumnName("browser_id");
            e.Property(m => m.BrowserName).HasColumnName("browser_name");
            e.Property(m => m.LastSeq).HasColumnName("last_seq");
            e.Property(m => m.LastSyncTime).HasColumnName("last_sync_time");
            e.Property(m => m.PendingPush).HasColumnName("pending_push");
            e.Property(m => m.RegisteredAt).HasColumnName("registered_at");
            e.Property(m => m.LastSeenAt).HasColumnName("last_seen_at");
        });

        mb.Entity<ConflictLog>(e => {
            e.ToTable("conflict_log");
            e.HasKey(c => c.Id);
            e.Property(c => c.Id).HasColumnName("id").ValueGeneratedOnAdd();
            e.Property(c => c.RecordId).HasColumnName("record_id");
            e.Property(c => c.RecordType).HasColumnName("record_type");
            e.Property(c => c.WinnerBrowser).HasColumnName("winner_browser");
            e.Property(c => c.LoserBrowser).HasColumnName("loser_browser");
            e.Property(c => c.ConflictedAt).HasColumnName("conflicted_at");
            e.Property(c => c.LoserPayload).HasColumnName("loser_payload");
        });
    }
}
