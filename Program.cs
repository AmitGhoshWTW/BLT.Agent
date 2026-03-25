using BLT.Agent.Data;
using BLT.Agent.Models;
using BLT.Agent.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

// ── Windows Service ───────────────────────────────────────────
builder.Host.UseWindowsService(o => o.ServiceName = "BLTAgent");

// ── SQLite ────────────────────────────────────────────────────
var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BLT");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "sync.db");

builder.Services.AddDbContextFactory<AgentDbContext>(o =>
    o.UseSqlite($"Data Source={dbPath}"));

// ── Services ──────────────────────────────────────────────────
builder.Services.AddSingleton<LogCollectorService>();
builder.Services.AddScoped<SyncEngine>();

// ── CORS ──────────────────────────────────────────────────────
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(
        "http://localhost:5173", "http://localhost:3000",
        "http://localhost:4173", "https://blt.company.com")
     .AllowAnyMethod().AllowAnyHeader()));

// ── Port + body size ──────────────────────────────────────────
builder.WebHost.UseUrls("http://localhost:42080");
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 50 * 1024 * 1024);

var app = builder.Build();

// ── DB init ───────────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var fac = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AgentDbContext>>();
    await using var db = await fac.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();
    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode = WAL;");
    await db.Database.ExecuteSqlRawAsync("PRAGMA foreign_keys = ON;");
}

app.UseCors();

// ── API token middleware (protects /api/sync/*) ───────────────
app.Use(async (ctx, next) =>
{
    if (ctx.Request.Path.StartsWithSegments("/api/sync"))
    {
        var svc    = ctx.RequestServices.GetRequiredService<LogCollectorService>();
        var header = ctx.Request.Headers.Authorization.ToString();
        var token  = header.Replace("Bearer ", "").Trim();
        if (token != svc.ApiToken)
        {
            svc.Audit("unauthorized", "", new { path = ctx.Request.Path.Value, ip = ctx.Connection.RemoteIpAddress?.ToString() });
            ctx.Response.StatusCode = 401;
            await ctx.Response.WriteAsJsonAsync(new { error = "Unauthorized" });
            return;
        }
    }
    await next();
});

// =============================================================
// HEALTH
// =============================================================
app.MapGet("/health", (LogCollectorService a) => new
{
    status    = "ok",
    agent     = LogCollectorService.AGENT_NAME,
    version   = LogCollectorService.AGENT_VERSION,
    timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
});

// =============================================================
// SYNC
// =============================================================
app.MapPost("/api/sync/register", async (RegisterRequest req, SyncEngine sync, LogCollectorService a) =>
{
    if (string.IsNullOrEmpty(req.BrowserId)) return Results.BadRequest(new { error = "browserId required" });
    await sync.RegisterBrowserAsync(req.BrowserId, req.BrowserName);
    a.Audit("register", req.BrowserId);
    return Results.Ok(new { registered = true, browserId = req.BrowserId });
});

app.MapPost("/api/sync/push", async (PushRequest req, SyncEngine sync, LogCollectorService a) =>
{
    if (string.IsNullOrEmpty(req.BrowserId)) return Results.BadRequest(new { error = "browserId required" });
    var result = await sync.PushAsync(req);
    a.Audit("push", req.BrowserId, new { records = req.Records.Count, accepted = result.Accepted });
    return Results.Ok(result);
});

app.MapPost("/api/sync/pull", async (PullRequest req, SyncEngine sync, LogCollectorService a) =>
{
    if (string.IsNullOrEmpty(req.BrowserId)) return Results.BadRequest(new { error = "browserId required" });
    var result = await sync.PullAsync(req);
    a.Audit("pull", req.BrowserId, new { sinceSeq = req.SinceSeq, returned = result.Total });
    return Results.Ok(result);
});

app.MapGet("/api/sync/status/{browserId}", async (string browserId, SyncEngine sync) =>
    Results.Ok(await sync.GetStatusAsync(browserId)));

app.MapGet("/api/sync/browsers", async (SyncEngine sync) =>
    Results.Ok(await sync.GetBrowsersAsync()));

app.MapGet("/api/sync/changelog", async (SyncEngine sync, long since = 0, int limit = 100) =>
    Results.Ok(await sync.GetChangelogAsync(since, limit)));

app.MapDelete("/api/sync/clearall", async (SyncEngine sync) =>
{
    await sync.ClearAllAsync();
    return Results.Ok(new { cleared = true });
});

// Token endpoint — localhost only
app.MapGet("/api/token", (HttpContext ctx, LogCollectorService a) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    if (ip != "127.0.0.1" && ip != "::1" && ip != "::ffff:127.0.0.1")
        return Results.Json(new { error = "Forbidden" }, statusCode: 403);
    return Results.Ok(new { token = a.ApiToken });
});

// =============================================================
// AGENT
// =============================================================
app.MapGet("/api/info", (LogCollectorService a) => Results.Ok(a.GetInfo()));

app.MapGet("/api/log-paths", (LogCollectorService a) => Results.Ok(new { paths = a.GetLogPaths() }));

app.MapPost("/api/log-paths", ([FromBody] AddPathRequest req, LogCollectorService a) =>
    string.IsNullOrEmpty(req.Path) ? Results.BadRequest(new { error = "Path required" }) :
    a.AddLogPath(req.Path) ? Results.Ok(new { success = true, paths = a.GetLogPaths() }) :
    Results.Conflict(new { error = "Path already exists" }));

app.MapDelete("/api/log-paths", ([FromBody] AddPathRequest req, LogCollectorService a) =>
    string.IsNullOrEmpty(req.Path) ? Results.BadRequest(new { error = "Path required" }) :
    a.RemoveLogPath(req.Path) ? Results.Ok(new { success = true, paths = a.GetLogPaths() }) :
    Results.NotFound(new { error = "Path not found" }));

app.MapGet("/api/collect-logs", async (LogCollectorService a) =>
    Results.Ok(await a.CollectAsync()));

app.MapPost("/api/test-path", ([FromBody] TestPathRequest req, LogCollectorService a) =>
    string.IsNullOrEmpty(req.Path) ? Results.BadRequest(new { error = "Path required" }) :
    Results.Ok(a.TestPath(req.Path)));

app.MapGet("/api/run-capture", async (LogCollectorService a, string? exePath = null, string? args = null) =>
{
    var r = await a.RunCaptureAsync(exePath, args);
    return r.Success ? Results.Ok(r) : Results.Json(r, statusCode: 500);
});

// Dev query — non-production only
app.MapGet("/api/dev/query", async (IDbContextFactory<AgentDbContext> fac, string? sql) =>
{
    if (app.Environment.IsProduction())
        return Results.Json(new { error = "Not in production" }, statusCode: 403);
    if (string.IsNullOrEmpty(sql))
        return Results.BadRequest(new { error = "sql param required" });
    try
    {
        await using var db  = await fac.CreateDbContextAsync();
        using var cmd       = db.Database.GetDbConnection().CreateCommand();
        cmd.CommandText     = sql;
        await db.Database.OpenConnectionAsync();
        using var reader    = await cmd.ExecuteReaderAsync();
        var rows = new List<Dictionary<string, object?>>();
        while (await reader.ReadAsync())
        {
            var row = new Dictionary<string, object?>();
            for (int i = 0; i < reader.FieldCount; i++)
                row[reader.GetName(i)] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            rows.Add(row);
        }
        return Results.Ok(new { rows, count = rows.Count });
    }
    catch (Exception ex) { return Results.BadRequest(new { error = ex.Message }); }
});

app.Run();
