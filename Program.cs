using BLT.Agent.Data;
using BLT.Agent.Models;
using BLT.Agent.Services;
using BLT.Agent.Ticketing;
using BLT.Agent.Ticketing.Contracts;
using BLT.Agent.Ticketing.Factory;
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

builder.Services.AddBltTicketing(builder.Configuration);

// ── CORS ──────────────────────────────────────────────────────
//builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
//    p.WithOrigins(
//        "http://localhost:5173", "http://localhost:3000",
//        "http://localhost:4173", "https://blt.company.com")
//     .AllowAnyMethod().AllowAnyHeader()));

// Program.cs — find the CORS block and add port 5000
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(
        "http://localhost:5173",     // Vite dev
        "http://localhost:3000",     // legacy
        "http://localhost:4173",     // Vite preview
        "http://localhost:5000",     // ← ADD: BFF dev server
        "http://localhost:5011",
        "http://localhost:5022",
        "http://localhost:5174",
        "https://blt-pwa-bff.azurewebsites.net",
        "https://localhost:5001")    // ← ADD: BFF HTTPS dev
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


static (bool Success, string ErrorMessage) RunCapture(
    string exePath, string arguments, int timeoutMs, ILogger logger)
{
    try
    {
        if (!File.Exists(exePath))
            return (false, $"ScreenCapture.exe not found at: {exePath}");

        // Windows Service runs in Session 0 (no desktop access)
        // Must use SessionHelper to launch in the user's interactive session
        bool isWindowsService = !System.Environment.UserInteractive;

        if (isWindowsService)
        {
            logger.LogInformation("[Capture] Running as Windows Service — using SessionHelper");
            var r = SessionHelper.LaunchInUserSession(exePath, arguments, waitForExit: true, timeoutMs: timeoutMs);
            return (r.Success, r.ErrorMessage ?? string.Empty);
            //return SessionHelper.LaunchInUserSession(exePath, arguments, waitForExit: true, timeoutMs: timeoutMs);
        }

        // Dev mode (VS F5 / dotnet run) — launch directly as current user
        logger.LogInformation("[Capture] Running in dev mode — launching directly");
        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new System.Diagnostics.ProcessStartInfo(exePath, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        proc.Start();
        bool exited = proc.WaitForExit(timeoutMs);
        if (!exited)
        {
            proc.Kill();
            return (false, $"ScreenCapture.exe timed out after {timeoutMs}ms");
        }
        if (proc.ExitCode != 0)
        {
            var err = proc.StandardError.ReadToEnd();
            return (false, $"ScreenCapture.exe exited with code {proc.ExitCode}: {err}");
        }
        return (true, string.Empty);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "RunCapture failed");
        return (false, ex.Message);
    }
}

var screenCaptureExePath = app.Configuration["ScreenCapture:ExePath"] ??
    @"C:\Tools\ScreenCapture\ScreenCapture.exe";
var outputDirectory = app.Configuration["ScreenCapture:OutputDir"] ??
    @"C:\ScreenCaptureService\Captures";

var noWinRt = app.Configuration.GetValue<bool>("ScreenCapture:NoWinRt");
var winRtFlag = noWinRt ? "--no-winrt" : string.Empty;

Directory.CreateDirectory(outputDirectory);

// =============================================================
// CAPTURE ENDPOINTS
// =============================================================

app.MapPost("/api/capture/active", async (CaptureRequest? req, ILogger<Program> logger) =>
{
    try
    {
        var outputDir = req?.OutputDirectory ?? outputDirectory;
        var prefix = req?.FilePrefix ?? "active_";
        var arguments = $"-m active -d \"{outputDir}\" -p \"{prefix}\"";

        logger.LogInformation("Launching ScreenCapture: {Args}", arguments);

        var result = RunCapture(screenCaptureExePath, arguments, 30000, logger);
        if (!result.Success)
        {
            logger.LogError("Capture failed: {Error}", result.ErrorMessage);
            return Results.Problem(result.ErrorMessage, statusCode: 500);
        }

        var latestFile = new DirectoryInfo(outputDir)
            .GetFiles($"{prefix}*.png")
            .OrderByDescending(f => f.CreationTime)
            .FirstOrDefault();

        if (latestFile == null)
            return Results.Problem("Screenshot not found after capture", statusCode: 500);

        return Results.Ok(new
        {
            success = true,
            filePath = latestFile.FullName,
            fileName = latestFile.Name,
            fileSize = latestFile.Length,
            timestamp = latestFile.CreationTime
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error capturing active screenshot");
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/capture/window", async (WindowCaptureRequest req, ILogger<Program> logger) =>
{
    if (string.IsNullOrWhiteSpace(req.WindowTitle))
        return Results.BadRequest(new { error = "windowTitle is required" });

    try
    {
        var outputDir = req.OutputDirectory ?? outputDirectory;
        var prefix = req.FilePrefix ?? "window_";
        var arguments = $"-m window -w \"{req.WindowTitle}\" -d \"{outputDir}\" -p \"{prefix}\"";

        if (req.DelayMs > 0)
            arguments += $" --delay {req.DelayMs}";

        logger.LogInformation("Launching ScreenCapture: {Args}", arguments);

        var result = RunCapture(screenCaptureExePath, arguments, 30000, logger);
        if (!result.Success)
        {
            logger.LogError("Capture failed: {Error}", result.ErrorMessage);
            return Results.Problem(result.ErrorMessage, statusCode: 500);
        }

        var latestFile = new DirectoryInfo(outputDir)
            .GetFiles($"{prefix}*.png")
            .OrderByDescending(f => f.CreationTime)
            .FirstOrDefault();

        if (latestFile == null)
            return Results.Problem("Screenshot not found after capture", statusCode: 500);

        return Results.Ok(new
        {
            success = true,
            filePath = latestFile.FullName,
            fileName = latestFile.Name,
            fileSize = latestFile.Length,
            timestamp = latestFile.CreationTime,
            windowTitle = req.WindowTitle
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error capturing window screenshot");
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/capture/fullscreen", async (CaptureRequest? req, ILogger<Program> logger) =>
{
    try
    {
        var outputDir = req?.OutputDirectory ?? outputDirectory;
        var prefix = req?.FilePrefix ?? "fullscreen_";
        var arguments = $"-m full -d \"{outputDir}\" -p \"{prefix}\"";

        logger.LogInformation("Launching ScreenCapture: {Args}", arguments);

        var result = RunCapture(screenCaptureExePath, arguments, 30000, logger);
        if (!result.Success)
        {
            logger.LogError("Capture failed: {Error}", result.ErrorMessage);
            return Results.Problem(result.ErrorMessage, statusCode: 500);
        }

        var latestFile = new DirectoryInfo(outputDir)
            .GetFiles($"{prefix}*.png")
            .OrderByDescending(f => f.CreationTime)
            .FirstOrDefault();

        if (latestFile == null)
            return Results.Problem("Screenshot not found after capture", statusCode: 500);

        return Results.Ok(new
        {
            success = true,
            filePath = latestFile.FullName,
            fileName = latestFile.Name,
            fileSize = latestFile.Length,
            timestamp = latestFile.CreationTime
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error capturing fullscreen screenshot");
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/capture/region", async (RegionCaptureRequest req, ILogger<Program> logger) =>
{
    if (req.X < 0 || req.Y < 0 || req.Width <= 0 || req.Height <= 0)
        return Results.BadRequest(new { error = "Invalid region coordinates" });

    try
    {
        var outputDir = req.OutputDirectory ?? outputDirectory;
        var prefix = req.FilePrefix ?? "region_";
        var arguments = $"-m region -r {req.X} {req.Y} {req.Width} {req.Height} -d \"{outputDir}\" -p \"{prefix}\"";

        logger.LogInformation("Launching ScreenCapture: {Args}", arguments);

        var result = RunCapture(screenCaptureExePath, arguments, 30000, logger);
        if (!result.Success)
        {
            logger.LogError("Capture failed: {Error}", result.ErrorMessage);
            return Results.Problem(result.ErrorMessage, statusCode: 500);
        }

        var latestFile = new DirectoryInfo(outputDir)
            .GetFiles($"{prefix}*.png")
            .OrderByDescending(f => f.CreationTime)
            .FirstOrDefault();

        if (latestFile == null)
            return Results.Problem("Screenshot not found after capture", statusCode: 500);

        return Results.Ok(new
        {
            success = true,
            filePath = latestFile.FullName,
            fileName = latestFile.Name,
            fileSize = latestFile.Length,
            timestamp = latestFile.CreationTime,
            region = new { x = req.X, y = req.Y, width = req.Width, height = req.Height }
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error capturing region screenshot");
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/capture/browsers", async (BrowserCaptureRequest req, ILogger<Program> logger) =>
{
    try
    {
        var outputDir = req.OutputDirectory ?? outputDirectory;
        var prefix = req.FilePrefix ?? "browser_";
        var mode = req.BrowserType?.ToLower() switch
        {
            "chrome" => "chrome",
            "edge" => "edge",
            _ => "browsers"
        };
        var arguments = $"-m {mode} -d \"{outputDir}\" -p \"{prefix}\"";

        logger.LogInformation("Launching ScreenCapture: {Args}", arguments);

        var result = RunCapture(screenCaptureExePath, arguments, 60000, logger);
        if (!result.Success)
        {
            logger.LogError("Capture failed: {Error}", result.ErrorMessage);
            return Results.Problem(result.ErrorMessage, statusCode: 500);
        }

        var capturedFiles = new DirectoryInfo(outputDir)
            .GetFiles($"{prefix}*.png")
            .Where(f => f.CreationTime > DateTime.Now.AddMinutes(-1))
            .OrderByDescending(f => f.CreationTime)
            .Select(f => new
            {
                filePath = f.FullName,
                fileName = f.Name,
                fileSize = f.Length,
                timestamp = f.CreationTime
            })
            .ToList();

        return Results.Ok(new
        {
            success = true,
            browserType = mode,
            count = capturedFiles.Count,
            files = capturedFiles
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error capturing browser screenshots");
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

app.MapPost("/api/capture/monitor", async (MonitorCaptureRequest req, ILogger<Program> logger) =>
{
    try
    {
        var outputDir = req.OutputDirectory ?? outputDirectory;
        var prefix = req.FilePrefix ?? "monitor_";
        var arguments = req.CaptureAll
            ? $"-m multi -d \"{outputDir}\" -p \"{prefix}\""
            : $"-m monitor --monitor {req.MonitorIndex} -d \"{outputDir}\" -p \"{prefix}\"";

        logger.LogInformation("Launching ScreenCapture: {Args}", arguments);

        var result = RunCapture(screenCaptureExePath, arguments, 30000, logger);
        if (!result.Success)
        {
            logger.LogError("Capture failed: {Error}", result.ErrorMessage);
            return Results.Problem(result.ErrorMessage, statusCode: 500);
        }

        var latestFile = new DirectoryInfo(outputDir)
            .GetFiles($"{prefix}*.png")
            .OrderByDescending(f => f.CreationTime)
            .FirstOrDefault();

        if (latestFile == null)
            return Results.Problem("Screenshot not found after capture", statusCode: 500);

        return Results.Ok(new
        {
            success = true,
            filePath = latestFile.FullName,
            fileName = latestFile.Name,
            fileSize = latestFile.Length,
            timestamp = latestFile.CreationTime,
            captureAll = req.CaptureAll,
            monitorIndex = req.CaptureAll ? (int?)null : req.MonitorIndex
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error capturing monitor screenshot");
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

// =============================================================
// UTILITY ENDPOINTS
// =============================================================

app.MapGet("/api/capture/list-windows", async (ILogger<Program> logger) =>
{
    try
    {
        var tempOutput = Path.Combine(Path.GetTempPath(), $"windows_{Guid.NewGuid()}.txt");
        // Redirect stdout to temp file via cmd
        var arguments = $"--list-windows > \"{tempOutput}\"";

        logger.LogInformation("Listing windows");

        using var proc = new System.Diagnostics.Process();
        proc.StartInfo = new System.Diagnostics.ProcessStartInfo("cmd", $"/c \"{screenCaptureExePath}\" {arguments}")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        proc.Start();
        proc.WaitForExit(10000);

        if (!File.Exists(tempOutput))
            return Results.Problem("Could not retrieve window list", statusCode: 500);

        var output = await File.ReadAllTextAsync(tempOutput);
        File.Delete(tempOutput);

        var windows = output
            .Split('\n')
            .Where(line => !string.IsNullOrWhiteSpace(line) &&
                           !line.Contains("----") &&
                           !line.Contains("Available Windows") &&
                           !line.Contains("Total:"))
            .Select(line => line.Trim())
            .Where(line => !string.IsNullOrEmpty(line))
            .ToList();

        return Results.Ok(new { success = true, count = windows.Count, windows });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error listing windows");
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

app.MapGet("/api/capture/screenshot/{fileName}", async (string fileName) =>
{
    var filePath = Path.Combine(outputDirectory, fileName);
    if (!File.Exists(filePath))
        return Results.NotFound(new { error = "Screenshot not found" });

    var bytes = await File.ReadAllBytesAsync(filePath);
    return Results.File(bytes, "image/png", fileName);
});

app.MapGet("/api/capture/screenshots", () =>
{
    try
    {
        var files = new DirectoryInfo(outputDirectory)
            .GetFiles("*.png")
            .OrderByDescending(f => f.CreationTime)
            .Take(50)
            .Select(f => new
            {
                fileName = f.Name,
                fileSize = f.Length,
                timestamp = f.CreationTime,
                url = $"/api/capture/screenshot/{f.Name}"
            })
            .ToList();

        return Results.Ok(new { success = true, count = files.Count, screenshots = files });
    }
    catch (Exception ex)
    {
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

app.MapDelete("/api/capture/screenshot/{fileName}", async (string fileName, ILogger<Program> logger) =>
{
    try
    {
        var filePath = Path.Combine(outputDirectory, fileName);
        if (!File.Exists(filePath))
            return Results.NotFound(new { error = "Screenshot not found" });

        File.Delete(filePath);
        logger.LogInformation("Deleted screenshot: {FileName}", fileName);
        return Results.Ok(new { success = true, deleted = fileName });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error deleting screenshot: {FileName}", fileName);
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

app.MapGet("/api/capture/health", () =>
{
    var exeExists = File.Exists(screenCaptureExePath);
    var dirExists = Directory.Exists(outputDirectory);

    return Results.Ok(new
    {
        healthy = exeExists && dirExists,
        screenCaptureExe = new
        {
            path = screenCaptureExePath,
            exists = exeExists
        },
        outputDirectory = new
        {
            path = outputDirectory,
            exists = dirExists,
            screenshotCount = dirExists
                ? Directory.GetFiles(outputDirectory, "*.png").Length
                : 0
        },
        timestamp = DateTime.Now
    });
});

app.MapPost("/api/capture/all-browsers", async (CaptureRequest? req, ILogger<Program> logger) =>
{
    try
    {
        var outputDir = req?.OutputDirectory ?? outputDirectory;
        var prefix = req?.FilePrefix ?? "browsers_";
        //var arguments = $"-m browsers -d \"{outputDir}\" -p \"{prefix}\" ";
        var arguments = $"-m browsers -d \"{outputDir}\" -p \"{prefix}\" {winRtFlag}".Trim();

        logger.LogInformation("Capturing all browsers (Chrome + Edge): {Args}", arguments);

        var result = RunCapture(screenCaptureExePath, arguments, 60000, logger);
        if (!result.Success)
        {
            logger.LogError("All-browser capture failed: {Error}", result.ErrorMessage);
            return Results.Problem(result.ErrorMessage, statusCode: 500);
        }

        // Collect all files written in the last 60 seconds
        var capturedFiles = new DirectoryInfo(outputDir)
            .GetFiles($"{prefix}*.png")
            .Where(f => f.CreationTime >= DateTime.Now.AddSeconds(-60))
            .OrderByDescending(f => f.CreationTime)
            .Select(f => new
            {
                fileName = f.Name,
                filePath = f.FullName,
                fileSize = f.Length,
                timestamp = f.CreationTime,
                url = $"/api/capture/screenshot/{f.Name}"
            })
            .ToList();

        if (capturedFiles.Count == 0)
            return Results.Problem("No browser screenshots produced", statusCode: 500);

        logger.LogInformation("Captured {Count} browser screenshot(s)", capturedFiles.Count);

        return Results.Ok(new
        {
            success = true,
            count = capturedFiles.Count,
            files = capturedFiles
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Error capturing all browsers");
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

// ── POST /api/ticket/create ───────────────────────────────────────────────────
// Called by React jiraService.js — provider resolved from config automatically
app.MapPost("/api/ticket/create", async (
    TicketCreateRequest request,
    ITicketingProviderFactory factory,
    ILogger<Program> logger) =>
{
    try
    {
        var provider = factory.GetActiveProvider();
        logger.LogInformation("[Ticket] Creating via {Provider}: {Title}",
            provider.ProviderName, request.Title);

        var result = await provider.CreateTicketAsync(request);

        if (!result.Success)
        {
            logger.LogError("[Ticket] Failed: {Error}", result.Error);
            return Results.Problem(result.Error, statusCode: 500);
        }

        logger.LogInformation("[Ticket] Created: {Key} at {Url}",
            result.TicketKey, result.TicketUrl);

        return Results.Ok(new
        {
            success = true,
            key = result.TicketKey,
            url = result.TicketUrl,
            provider = result.Provider
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[Ticket] Unhandled error");
        return Results.Problem(ex.Message, statusCode: 500);
    }
});

// ── GET /api/ticket/status/{key} ──────────────────────────────────────────────
app.MapGet("/api/ticket/status/{key}", async (
    string key,
    ITicketingProviderFactory factory) =>
{
    var provider = factory.GetActiveProvider();
    var status = await provider.GetTicketStatusAsync(key);
    return status is null
        ? Results.NotFound(new { error = $"Ticket {key} not found" })
        : Results.Ok(status);
});

// ── GET /api/ticket/health ────────────────────────────────────────────────────
app.MapGet("/api/ticket/health", async (ITicketingProviderFactory factory) =>
{
    var provider = factory.GetActiveProvider();
    var healthy = await provider.IsHealthyAsync();
    var providers = factory.GetRegisteredProviders();

    return Results.Ok(new
    {
        activeProvider = provider.ProviderName,
        healthy,
        registeredProviders = providers
    });
});

// ── GET /api/ticket/providers ─────────────────────────────────────────────────
// Admin endpoint — list all registered providers
app.MapGet("/api/ticket/providers", (ITicketingProviderFactory factory) =>
    Results.Ok(new { providers = factory.GetRegisteredProviders() }));


app.Run();

// =============================================================
// REQUEST MODELS
// =============================================================

public record CaptureRequest(
    string? OutputDirectory = null,
    string? FilePrefix = null
);

public record WindowCaptureRequest(
    string WindowTitle,
    string? OutputDirectory = null,
    string? FilePrefix = null,
    int DelayMs = 0
);

public record RegionCaptureRequest(
    int X,
    int Y,
    int Width,
    int Height,
    string? OutputDirectory = null,
    string? FilePrefix = null
);

public record BrowserCaptureRequest(
    string? BrowserType = null, // "chrome", "edge", or null for all
    string? OutputDirectory = null,
    string? FilePrefix = null
);

public record MonitorCaptureRequest(
    bool CaptureAll = false,
    int MonitorIndex = 0,
    string? OutputDirectory = null,
    string? FilePrefix = null
);


