using System.Diagnostics;
using System.Text.Json;
using BLT.Agent.Models;

namespace BLT.Agent.Services;

public class LogCollectorService
{
    private readonly ILogger<LogCollectorService> _log;
    private readonly IConfiguration               _config;

    public const string AGENT_VERSION = "3.0.0";
    public const string AGENT_NAME    = "BLT Agent";

    private readonly string _dataDir;
    private readonly string _configPath;
    private readonly string _auditPath;
    private List<string>    _logPaths;
    private string          _apiToken = string.Empty;

    private static readonly List<string> DefaultPaths =
    [
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Palo Alto Networks", "GlobalProtect", "PanGPS.log"),
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Palo Alto Networks", "GlobalProtect", "PanGPA.log"),
        @"C:\Program Files\Palo Alto Networks\GlobalProtect\PanGPS.log"
    ];

    public LogCollectorService(ILogger<LogCollectorService> log, IConfiguration config)
    {
        _log    = log;
        _config = config;

        _dataDir    = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BLT");
        _configPath = Path.Combine(_dataDir, "agent-config.json");
        _auditPath  = Path.Combine(_dataDir, "audit.log");

        Directory.CreateDirectory(_dataDir);

        _logPaths = LoadPaths();
        _apiToken = EnsureToken();

        _log.LogInformation("[Agent] v{V} started. SQLite: {D}", AGENT_VERSION, _dataDir);
    }

    public string ApiToken => _apiToken;
    public string DataDir  => _dataDir;

    // ── Audit ──────────────────────────────────────────────────
    public void Audit(string action, string browserId, object? details = null)
    {
        try
        {
            File.AppendAllText(_auditPath,
                JsonSerializer.Serialize(new {
                    ts      = DateTimeOffset.UtcNow.ToString("o"),
                    action,
                    browser = browserId.Length >= 12 ? browserId[..12] : browserId,
                    details
                }) + Environment.NewLine);
        }
        catch { }
    }

    // ── Token ──────────────────────────────────────────────────
    private string EnsureToken()
    {
        var cfg = ReadConfig();
        if (!string.IsNullOrEmpty(cfg?.ApiToken)) return cfg.ApiToken;

        var token = Convert.ToHexString(
            System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();

        SaveConfig(new AgentConfig { LogPaths = _logPaths, ApiToken = token });
        _log.LogInformation("[Agent] API token generated");
        return token;
    }

    // ── Log paths ──────────────────────────────────────────────
    public List<LogPathStatus> GetLogPaths() =>
        _logPaths.Select(p => {
            var exp    = Expand(p);
            var exists = File.Exists(exp);
            return new LogPathStatus { Path = p, ExpandedPath = exp, Exists = exists, Readable = exists && CanRead(exp) };
        }).ToList();

    public bool AddLogPath(string path)
    {
        if (_logPaths.Contains(path)) return false;
        _logPaths.Add(path);
        SaveConfig(new AgentConfig { LogPaths = _logPaths, ApiToken = _apiToken });
        return true;
    }

    public bool RemoveLogPath(string path)
    {
        if (!_logPaths.Remove(path)) return false;
        SaveConfig(new AgentConfig { LogPaths = _logPaths, ApiToken = _apiToken });
        return true;
    }

    // ── Collect logs ───────────────────────────────────────────
    public async Task<CollectLogsResponse> CollectAsync()
    {
        var files  = new List<CollectedFile>();
        var errors = new List<CollectError>();

        foreach (var lp in _logPaths)
        {
            var exp = Expand(lp);
            try
            {
                if (!File.Exists(exp)) { errors.Add(new() { Path = lp, Error = "File not found" }); continue; }

                var content = await ReadSharedAsync(exp, 512 * 1024);
                var fi      = new FileInfo(exp);

                files.Add(new CollectedFile
                {
                    OriginalPath = lp, ExpandedPath = exp,
                    Filename     = Path.GetFileName(exp),
                    Content      = content, Size = fi.Length,
                    LastModified = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(),
                    CollectedAt  = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
                });
                _log.LogInformation("[Agent] Collected: {P}", exp);
            }
            catch (Exception ex)
            {
                _log.LogWarning("[Agent] Error {P}: {M}", lp, ex.Message);
                errors.Add(new() { Path = lp, Error = ex.Message });
            }
        }

        return new CollectLogsResponse { Success = true, Collected = files.Count, Files = files, Errors = errors };
    }

    // ── Test path ──────────────────────────────────────────────
    public TestPathResponse TestPath(string testPath)
    {
        var exp = Expand(testPath);
        try
        {
            var fExists = File.Exists(exp);
            var dExists = Directory.Exists(exp);
            if (!fExists && !dExists) return new() { Path = testPath, ExpandedPath = exp, Exists = false };

            long size = 0; long? lastMod = null;
            if (fExists) { var fi = new FileInfo(exp); size = fi.Length; lastMod = new DateTimeOffset(fi.LastWriteTimeUtc).ToUnixTimeMilliseconds(); }
            else         { var di = new DirectoryInfo(exp); lastMod = new DateTimeOffset(di.LastWriteTimeUtc).ToUnixTimeMilliseconds(); }

            return new() { Path = testPath, ExpandedPath = exp, Exists = true, Readable = CanRead(exp),
                IsFile = fExists, IsDirectory = dExists, Size = size, LastModified = lastMod };
        }
        catch (Exception ex)
        {
            return new() { Path = testPath, ExpandedPath = exp, Exists = false, Error = ex.Message };
        }
    }

    // ── Screen capture ─────────────────────────────────────────
    public async Task<CaptureResult> RunCaptureAsync(string? exePath, string? args)
    {
        var exe = exePath ?? _config["Agent:CaptureExePath"] ?? @"C:\ScreenCapture\ScreenCapture.exe";
        var arg = args    ?? _config["Agent:CaptureArgs"]    ?? "-m Edge -v -d c:\\screenshots4\\";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe, Arguments = arg,
                RedirectStandardOutput = true, RedirectStandardError = true,
                UseShellExecute = false, CreateNoWindow = true
            };
            using var proc = Process.Start(psi) ?? throw new Exception("Failed to start");
            var stdout = await proc.StandardOutput.ReadToEndAsync();
            var stderr = await proc.StandardError.ReadToEndAsync();
            await proc.WaitForExitAsync();
            return new CaptureResult { Success = proc.ExitCode == 0, ExitCode = proc.ExitCode, Stdout = stdout, Stderr = stderr };
        }
        catch (Exception ex)
        {
            return new CaptureResult { Success = false, Error = ex.Message };
        }
    }

    // ── Info ───────────────────────────────────────────────────
    public object GetInfo() => new
    {
        name           = AGENT_NAME,
        version        = AGENT_VERSION,
        platform       = Environment.OSVersion.Platform.ToString(),
        arch           = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
        hostname       = Environment.MachineName,
        username       = Environment.UserName,
        monitoredPaths = _logPaths
    };

    // ── Helpers ────────────────────────────────────────────────
    public static string Expand(string p)
    {
        var home  = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var app   = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return p
            .Replace("~",              home,  StringComparison.OrdinalIgnoreCase)
            .Replace("%USERPROFILE%",  home,  StringComparison.OrdinalIgnoreCase)
            .Replace("%USERNAME%",     Environment.UserName, StringComparison.OrdinalIgnoreCase)
            .Replace("%APPDATA%",      app,   StringComparison.OrdinalIgnoreCase)
            .Replace("%LOCALAPPDATA%", local, StringComparison.OrdinalIgnoreCase)
            .Replace("$HOME",          home,  StringComparison.OrdinalIgnoreCase);
    }

    private static bool CanRead(string path)
    {
        try { using var _ = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite); return true; }
        catch { return false; }
    }

    private static async Task<string> ReadSharedAsync(string path, long maxBytes)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (fs.Length <= maxBytes) { using var r = new StreamReader(fs); return await r.ReadToEndAsync(); }
        fs.Seek(-maxBytes, SeekOrigin.End);
        using var tail = new StreamReader(fs);
        await tail.ReadLineAsync();
        return await tail.ReadToEndAsync();
    }

    private List<string> LoadPaths()
    {
        var cfg = ReadConfig();
        return cfg?.LogPaths?.Count > 0 ? cfg.LogPaths : [..DefaultPaths];
    }

    private AgentConfig? ReadConfig()
    {
        if (!File.Exists(_configPath)) return null;
        try { return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(_configPath)); }
        catch { return null; }
    }

    private void SaveConfig(AgentConfig cfg)
    {
        try { File.WriteAllText(_configPath, JsonSerializer.Serialize(cfg, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { _log.LogError("[Agent] Config save failed: {M}", ex.Message); }
    }
}
