using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BLT.Agent.Ticketing.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BLT.Agent.Ticketing.Providers;

public sealed class JiraCloudProvider : ITicketingProvider
{
    private readonly JiraCloudOptions _opts;
    private readonly HttpClient       _http;
    private readonly ILogger          _log;

    public string ProviderName => "JIRA Cloud";

    public JiraCloudProvider(
        IOptions<TicketingOptions> options,
        IHttpClientFactory httpFactory,
        ILogger<JiraCloudProvider> log)
    {
        _opts = options.Value.JiraCloud;
        _http = httpFactory.CreateClient(nameof(JiraCloudProvider));
        _log  = log;

        // Cloud auth: Basic base64(email:apitoken)
        var creds = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($"{_opts.Email}:{_opts.ApiToken}"));
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", creds);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        _http.BaseAddress = new Uri($"https://{_opts.CloudId.TrimEnd('/')}/");
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

            // Sanitize labels — JIRA Cloud rejects labels with spaces or colons
            static string SanitizeLabel(string label) =>
                label.Replace(" ", "-").Replace(":", "-").Replace("/", "-").Trim();

            var payload = new
            {
                fields = new
                {
                    project = new { key = projectKey },
                    summary = request.Title,
                    description = BuildAdfDescription(request),
                    issuetype = new { name = _opts.IssueType },
                    priority = new { name = MapPriority(request.Priority) },
                    labels = new[]
                    {
                    "BLT",
                    SanitizeLabel(request.Category),
                    $"blt-{SanitizeLabel(request.BltReportId)}"
                }
                    .Where(l => !string.IsNullOrWhiteSpace(l))
                    .Distinct()
                    .ToArray()
                }
            };

            var json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var response = await _http.PostAsync("rest/api/3/issue", content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _log.LogError("[JiraCloud] Create failed {Status}: {Error}",
                    response.StatusCode, err);
                return TicketResult.Fail($"HTTP {(int)response.StatusCode}: {err}", ProviderName);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var key = doc.RootElement.GetProperty("key").GetString() ?? string.Empty;
            var id = doc.RootElement.GetProperty("id").GetString() ?? string.Empty;
            var url = $"https://{_opts.CloudId.TrimEnd('/')}/browse/{key}";

            _log.LogInformation("[JiraCloud] Ticket created: {Key} {Url}", key, url);

            await AttachFilesAsync(id, request.Screenshots, ct);
            await AttachFilesAsync(id, request.LogFiles, ct);

            return TicketResult.Ok(key, url, ProviderName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[JiraCloud] CreateTicketAsync failed");
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
            var resp = await _http.GetAsync($"rest/api/3/issue/{ticketKey}", ct);
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
                TicketUrl = $"https://{_opts.CloudId.TrimEnd('/')}/browse/{ticketKey}"
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
            var resp = await _http.GetAsync("rest/api/3/myself", ct);
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
                var bytes = Convert.FromBase64String(file.Base64);
                using var form = new MultipartFormDataContent();
                form.Add(new ByteArrayContent(bytes), "file", file.FileName);
                var req = new HttpRequestMessage(
                    HttpMethod.Post, $"rest/api/3/issue/{issueId}/attachments");
                req.Headers.Add("X-Atlassian-Token", "no-check");
                req.Content = form;
                await _http.SendAsync(req, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[JiraCloud] Attachment failed: {File}", file.FileName);
            }
        }
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="r"></param>
    /// <returns></returns>
    // Atlassian Document Format (ADF) — required for JIRA Cloud v3 description
    private static object BuildAdfDescription(TicketCreateRequest r)
    {
        var paragraphs = new List<object>
        {
            AdfParagraph(r.Description),
            AdfParagraph("---"),
            AdfParagraph($"Reporter: {r.Reporter.Name} ({r.Reporter.Email})"),
            AdfParagraph($"Department: {r.Reporter.Department}"),
            AdfParagraph($"BLT Report ID: {r.BltReportId}")
        };
        foreach (var kv in r.Metadata)
            paragraphs.Add(AdfParagraph($"{kv.Key}: {kv.Value}"));

        return new
        {
            version = 1,
            type    = "doc",
            content = paragraphs
        };
    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="text"></param>
    /// <returns></returns>
    private static object AdfParagraph(string text) => new
    {
        type    = "paragraph",
        content = new[]
        {
            new { type = "text", text }
        }
    };

    /// <summary>
    /// 
    /// </summary>
    /// <param name="bltPriority"></param>
    /// <returns></returns>
    private static string MapPriority(string bltPriority) => bltPriority switch
    {
        "Critical" => "Highest",
        "High"     => "High",
        "Low"      => "Low",
        _          => "Medium"
    };
}
