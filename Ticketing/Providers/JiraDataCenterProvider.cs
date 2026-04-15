// Ticketing/Providers/JiraDataCenterProvider.cs
//
// JIRA Data Center (on-premises) implementation.
// Uses JIRA REST API v2 with Basic Auth (username + PAT).

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BLT.Agent.Ticketing.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BLT.Agent.Ticketing.Providers;

public sealed class JiraDataCenterProvider : ITicketingProvider
{
    private readonly JiraDataCenterOptions _opts;
    private readonly HttpClient            _http;
    private readonly ILogger               _log;

    public string ProviderName => "JIRA Data Center";

    /// <summary>
    /// 
    /// </summary>
    /// <param name="options"></param>
    /// <param name="httpFactory"></param>
    /// <param name="log"></param>
    public JiraDataCenterProvider(
        IOptions<TicketingOptions> options,
        IHttpClientFactory httpFactory,
        ILogger<JiraDataCenterProvider> log)
    {
        _opts = options.Value.JiraDataCenter;
        _http = httpFactory.CreateClient(nameof(JiraDataCenterProvider));
        _log  = log;

        // Basic auth header: base64(username:token)
        var creds  = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_opts.Username}:{_opts.ApiToken}"));
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", creds);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        _http.BaseAddress = new Uri(_opts.BaseUrl.TrimEnd('/') + "/");
        _http.Timeout     = TimeSpan.FromSeconds(_opts.TimeoutSecs);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="request"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public async Task<TicketResult> CreateTicketAsync(
        TicketCreateRequest request, CancellationToken ct = default)
    {
        try
        {
            var projectKey = request.ProjectKeyOverride ?? _opts.ProjectKey;

            // ── Build JIRA issue payload ──────────────────────────────────────
            var payload = new
            {
                fields = new
                {
                    project    = new { key = projectKey },
                    summary    = request.Title,
                    description= BuildJiraDescription(request),
                    issuetype  = new { name = _opts.IssueType },
                    priority   = new { name = MapPriority(request.Priority) },
                    labels     = new[] { "BLT", request.Category, $"blt-report:{request.BltReportId}" },
                    reporter   = string.IsNullOrEmpty(request.Reporter.Email)
                        ? null
                        : new { name = request.Reporter.Email }
                }
            };

            var json     = JsonSerializer.Serialize(payload);
            var content  = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("rest/api/2/issue", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _log.LogError("[JiraDC] Create failed {Status}: {Error}",
                    response.StatusCode, err);
                return TicketResult.Fail($"HTTP {(int)response.StatusCode}: {err}", ProviderName);
            }

            var body    = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var key     = doc.RootElement.GetProperty("key").GetString() ?? string.Empty;
            var id      = doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
            var url     = $"{_opts.BaseUrl.TrimEnd('/')}/browse/{key}";

            _log.LogInformation("[JiraDC] Ticket created: {Key} {Url}", key, url);

            // ── Attach screenshots ────────────────────────────────────────────
            await AttachFilesAsync(id, request.Screenshots, ct);
            await AttachFilesAsync(id, request.LogFiles, ct);

            return TicketResult.Ok(key, url, ProviderName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[JiraDC] CreateTicketAsync failed");
            return TicketResult.Fail(ex.Message, ProviderName);
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="ticketKey"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    public async Task<TicketStatus?> GetTicketStatusAsync(
        string ticketKey, CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync($"rest/api/2/issue/{ticketKey}", ct);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var fields   = doc.RootElement.GetProperty("fields");
            var status   = fields.GetProperty("status")
                                 .GetProperty("name").GetString() ?? string.Empty;
            var assignee = fields.TryGetProperty("assignee", out var a) &&
                           a.ValueKind != JsonValueKind.Null
                ? a.GetProperty("displayName").GetString() ?? string.Empty
                : "Unassigned";

            return new TicketStatus
            {
                TicketKey = ticketKey,
                Status    = status,
                Assignee  = assignee,
                TicketUrl = $"{_opts.BaseUrl.TrimEnd('/')}/browse/{ticketKey}"
            };
        }
        catch { return null; }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="ct"></param>
    /// <returns></returns>
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            var resp = await _http.GetAsync("rest/api/2/serverInfo", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────


    /// <summary>
    /// 
    /// </summary>
    /// <param name="issueId"></param>
    /// <param name="files"></param>
    /// <param name="ct"></param>
    /// <returns></returns>
    private async Task AttachFilesAsync(
        string issueId, List<TicketAttachment> files, CancellationToken ct)
    {
        if (!files.Any()) return;
        foreach (var file in files)
        {
            try
            {
                var bytes   = Convert.FromBase64String(file.Base64);
                using var form = new MultipartFormDataContent();
                form.Add(new ByteArrayContent(bytes), "file", file.FileName);
                var req = new HttpRequestMessage(
                    HttpMethod.Post, $"rest/api/2/issue/{issueId}/attachments");
                req.Headers.Add("X-Atlassian-Token", "no-check");
                req.Content = form;
                await _http.SendAsync(req, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[JiraDC] Attachment failed: {File}", file.FileName);
            }
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="r"></param>
    /// <returns></returns>
    private static string BuildJiraDescription(TicketCreateRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine(r.Description);
        sb.AppendLine();
        sb.AppendLine("----");
        sb.AppendLine($"*Reporter:* {r.Reporter.Name} ({r.Reporter.Email})");
        sb.AppendLine($"*Department:* {r.Reporter.Department}");
        sb.AppendLine($"*BLT Report ID:* {r.BltReportId}");
        foreach (var kv in r.Metadata)
            sb.AppendLine($"*{kv.Key}:* {kv.Value}");
        return sb.ToString();
    }

    private static string MapPriority(string bltPriority) => bltPriority switch
    {
        "Critical" => "Highest",
        "High"     => "High",
        "Low"      => "Low",
        _          => "Medium"
    };
}
