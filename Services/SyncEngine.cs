using BLT.Agent.Data;
using BLT.Agent.Models;
using Microsoft.EntityFrameworkCore;

namespace BLT.Agent.Services;

public class SyncEngine
{
    private readonly IDbContextFactory<AgentDbContext> _factory;
    private readonly ILogger<SyncEngine>               _log;

    public SyncEngine(IDbContextFactory<AgentDbContext> factory, ILogger<SyncEngine> log)
    {
        _factory = factory;
        _log     = log;
    }

    private static string Now() => DateTimeOffset.UtcNow.ToString("o");

    public async Task RegisterBrowserAsync(string browserId, string browserName)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var now  = Now();
        var meta = await db.SyncMetadata.FindAsync(browserId);
        if (meta == null)
        {
            db.SyncMetadata.Add(new SyncMetadata
            {
                BrowserId    = browserId,
                BrowserName  = browserName,
                RegisteredAt = now,
                LastSeenAt   = now
            });
        }
        else
        {
            meta.BrowserName = browserName;
            meta.LastSeenAt  = now;
        }
        await db.SaveChangesAsync();
    }

    public async Task<PushResponse> PushAsync(PushRequest req)
    {
        await RegisterBrowserAsync(req.BrowserId, req.BrowserName);
        int accepted = 0, conflicts = 0, errors = 0;
        var conflictIds = new List<string>();
        var errorIds    = new List<string>();

        foreach (var dto in req.Records)
        {
            try
            {
                var r = await UpsertAsync(dto, req.BrowserId);
                if (r == UpsertResult.Accepted) accepted++;
                else { conflicts++; conflictIds.Add(dto.Id); }
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Push error {Id}", dto.Id);
                errors++; errorIds.Add(dto.Id);
            }
        }

        _log.LogInformation("[Sync] Push {B}: +{A} accepted {C} conflicts {E} errors",
            req.BrowserId[..Math.Min(8, req.BrowserId.Length)], accepted, conflicts, errors);

        return new PushResponse
        {
            Accepted = accepted, Conflicts = conflicts, Errors = errors,
            ConflictIds = conflictIds, ErrorIds = errorIds
        };
    }

    private async Task<UpsertResult> UpsertAsync(SyncRecordDto dto, string browserId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var existing = await db.SyncRecords.AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == dto.Id);

        if (existing == null)
        {
            db.SyncRecords.Add(new SyncRecord
            {
                Id = dto.Id, RecordType = dto.RecordType, UpdatedAt = dto.UpdatedAt,
                Version = dto.Version, IsDeleted = dto.IsDeleted ? 1 : 0,
                SourceBrowserId = browserId, PayloadJson = dto.PayloadJson
            });
            db.ChangeLogs.Add(MakeChange(dto.Id, dto.RecordType, "create", browserId));
            await db.SaveChangesAsync();
            return UpsertResult.Accepted;
        }

        if (string.Compare(existing.UpdatedAt, dto.UpdatedAt, StringComparison.Ordinal) > 0
            && existing.Version >= dto.Version)
        {
            db.ConflictLogs.Add(new ConflictLog
            {
                RecordId = dto.Id, RecordType = dto.RecordType,
                WinnerBrowser = existing.SourceBrowserId, LoserBrowser = browserId,
                ConflictedAt = Now(), LoserPayload = dto.PayloadJson
            });
            await db.SaveChangesAsync();
            return UpsertResult.Conflict;
        }

        var updated = new SyncRecord
        {
            Id = dto.Id, RecordType = dto.RecordType, UpdatedAt = dto.UpdatedAt,
            Version = dto.Version, IsDeleted = dto.IsDeleted ? 1 : 0,
            SourceBrowserId = browserId, PayloadJson = dto.PayloadJson
        };
        db.SyncRecords.Attach(updated);
        db.Entry(updated).State = EntityState.Modified;
        db.ChangeLogs.Add(MakeChange(dto.Id, dto.RecordType,
            dto.IsDeleted ? "delete" : "update", browserId));
        await db.SaveChangesAsync();
        return UpsertResult.Accepted;
    }

    public async Task<PullResponse> PullAsync(PullRequest req)
    {
        await RegisterBrowserAsync(req.BrowserId, "");
        await using var db = await _factory.CreateDbContextAsync();
        int limit = Math.Min(req.Limit, 500);

        var changes = await db.ChangeLogs
            .Where(c => c.SequenceId > req.SinceSeq && c.BrowserId != req.BrowserId)
            .OrderBy(c => c.SequenceId).Take(limit + 1).ToListAsync();

        bool hasMore = changes.Count > limit;
        if (hasMore) changes = changes.Take(limit).ToList();

        long lastSeq = changes.Count > 0 ? changes.Max(c => c.SequenceId) : req.SinceSeq;

        var latestByRecord = changes
            .GroupBy(c => c.RecordId)
            .Select(g => g.OrderByDescending(c => c.SequenceId).First())
            .ToList();

        var dtos = new List<SyncRecordDto>();
        foreach (var change in latestByRecord)
        {
            var rec = await db.SyncRecords.AsNoTracking()
                .FirstOrDefaultAsync(r => r.Id == change.RecordId);
            if (rec != null)
                dtos.Add(new SyncRecordDto
                {
                    Id = rec.Id, RecordType = rec.RecordType, UpdatedAt = rec.UpdatedAt,
                    Version = rec.Version, IsDeleted = rec.IsDeleted == 1,
                    PayloadJson = rec.PayloadJson
                });
        }

        var meta = await db.SyncMetadata.FindAsync(req.BrowserId);
        if (meta != null) { meta.LastSeq = lastSeq; meta.LastSyncTime = Now(); await db.SaveChangesAsync(); }

        _log.LogInformation("[Sync] Pull {B}: {N} records seq {F}->{T}",
            req.BrowserId[..Math.Min(8, req.BrowserId.Length)], dtos.Count, req.SinceSeq, lastSeq);

        return new PullResponse { LastSeq = lastSeq, Total = dtos.Count, HasMore = hasMore, Records = dtos };
    }

    public async Task<object> GetStatusAsync(string browserId)
    {
        await using var db = await _factory.CreateDbContextAsync();
        var meta      = await db.SyncMetadata.AsNoTracking().FirstOrDefaultAsync(m => m.BrowserId == browserId);
        var total     = await db.SyncRecords.CountAsync(r => r.RecordType == "report" && r.IsDeleted == 0);
        var conflicts = await db.ConflictLogs.CountAsync();
        return new { browserId, lastSyncTime = meta?.LastSyncTime, pendingPush = meta?.PendingPush ?? 0, totalRecords = total, conflictCount = conflicts };
    }

    public async Task<List<SyncMetadata>> GetBrowsersAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.SyncMetadata.AsNoTracking().OrderByDescending(b => b.LastSeenAt).ToListAsync();
    }

    public async Task<List<ChangeLog>> GetChangelogAsync(long since, int limit)
    {
        await using var db = await _factory.CreateDbContextAsync();
        return await db.ChangeLogs.AsNoTracking()
            .Where(c => c.SequenceId > since).OrderBy(c => c.SequenceId).Take(limit).ToListAsync();
    }

    public async Task ClearAllAsync()
    {
        await using var db = await _factory.CreateDbContextAsync();
        db.SyncRecords.RemoveRange(db.SyncRecords);
        db.ChangeLogs.RemoveRange(db.ChangeLogs);
        db.ConflictLogs.RemoveRange(db.ConflictLogs);
        await db.SaveChangesAsync();
    }

    private static ChangeLog MakeChange(string id, string type, string op, string browserId) =>
        new() { RecordId = id, RecordType = type, Operation = op, BrowserId = browserId, Timestamp = Now() };
}

public enum UpsertResult { Accepted, Conflict }
