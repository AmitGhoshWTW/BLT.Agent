// Ticketing/Providers/AzureDevOpsProvider.cs
//
// Azure DevOps Work Items implementation.
// Uses ADO REST API v7.0 with Personal Access Token auth.
// Creates Bug work items with BLT metadata stored as custom fields/tags.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using BLT.Agent.Ticketing.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BLT.Agent.Ticketing.Providers;

public sealed class AzureDevOpsProvider : ITicketingProvider
{
    private readonly AzureDevOpsOptions _opts;
    private readonly HttpClient         _http;
    private readonly ILogger            _log;

    public string ProviderName => "Azure DevOps";

    public AzureDevOpsProvider(
        IOptions<TicketingOptions> options,
        IHttpClientFactory httpFactory,
        ILogger<AzureDevOpsProvider> log)
    {
        _opts = options.Value.AzureDevOps;
        _http = httpFactory.CreateClient(nameof(AzureDevOpsProvider));
        _log  = log;

        // ADO auth: Basic base64(:PAT) — username is empty, PAT is the password
        var creds = Convert.ToBase64String(
            Encoding.UTF8.GetBytes($":{_opts.PersonalAccessToken}"));
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", creds);
        _http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        _http.BaseAddress = new Uri(
            $"https://dev.azure.com/{_opts.Organization}/{_opts.Project}/");
        _http.Timeout = TimeSpan.FromSeconds(_opts.TimeoutSecs);
    }

    public async Task<TicketResult> CreateTicketAsync(
        TicketCreateRequest request, CancellationToken ct = default)
    {
        try
        {
            // ADO uses JSON Patch document for work item creation
            var patches = new List<object>
            {
                Patch("System.Title",       request.Title),
                Patch("System.Description", BuildHtmlDescription(request)),
                Patch("Microsoft.VSTS.Common.Priority", MapPriority(request.Priority)),
                Patch("System.Tags",        $"BLT; {request.Category}; blt:{request.BltReportId}")
                //Patch("System.AssignedTo",  request.Reporter.Email)
            };

            // Only add AreaPath if explicitly configured — empty string causes TF401347
            if (!string.IsNullOrWhiteSpace(_opts.AreaPath))
                patches.Add(Patch("System.AreaPath", _opts.AreaPath));

            var json    = JsonSerializer.Serialize(patches);
            var content = new StringContent(json, Encoding.UTF8,
                "application/json-patch+json");   // ADO requires this specific content type

            var url      = $"_apis/wit/workitems/${Uri.EscapeDataString(_opts.WorkItemType)}?api-version=7.0";
            var response = await _http.PostAsync(url, content, ct);

            if (!response.IsSuccessStatusCode)
            {
                var err = await response.Content.ReadAsStringAsync(ct);
                _log.LogError("[ADO] Create failed {Status}: {Error}",
                    response.StatusCode, err);
                return TicketResult.Fail($"HTTP {(int)response.StatusCode}: {err}", ProviderName);
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var id  = doc.RootElement.GetProperty("id").GetInt32();
            var ticketUrl = doc.RootElement
                               .GetProperty("_links")
                               .GetProperty("html")
                               .GetProperty("href")
                               .GetString() ?? string.Empty;
            var key = $"#{id}";

            _log.LogInformation("[ADO] Work item created: {Key} {Url}", key, ticketUrl);

            // Attach screenshots as work item attachments
            await AttachFilesAsync(id, request.Screenshots, ct);
            await AttachFilesAsync(id, request.LogFiles, ct);

            return TicketResult.Ok(key, ticketUrl, ProviderName);
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "[ADO] CreateTicketAsync failed");
            return TicketResult.Fail(ex.Message, ProviderName);
        }
    }

    public async Task<TicketStatus?> GetTicketStatusAsync(
        string ticketKey, CancellationToken ct = default)
    {
        try
        {
            // ticketKey is "#123" — extract the number
            var id = ticketKey.TrimStart('#');
            var resp = await _http.GetAsync(
                $"_apis/wit/workitems/{id}?api-version=7.0", ct);
            if (!resp.IsSuccessStatusCode) return null;

            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            var fields   = doc.RootElement.GetProperty("fields");
            var status   = fields.GetProperty("System.State").GetString() ?? string.Empty;
            var assignee = fields.TryGetProperty("System.AssignedTo", out var a)
                ? a.TryGetProperty("displayName", out var dn)
                    ? dn.GetString() ?? "Unassigned"
                    : "Unassigned"
                : "Unassigned";

            return new TicketStatus
            {
                TicketKey = ticketKey,
                Status    = status,
                Assignee  = assignee,
                TicketUrl = $"https://dev.azure.com/{_opts.Organization}/{_opts.Project}/_workitems/edit/{id}"
            };
        }
        catch { return null; }
    }

    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        try
        {
            // Use work item types endpoint — scoped to the project, returns 200 if
            // org + project + PAT are all correct
            var resp = await _http.GetAsync(
                "_apis/wit/workitemtypes?api-version=7.0", ct);
            return resp.IsSuccessStatusCode;
        }
        catch { return false; }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task AttachFilesAsync(
        int workItemId, List<TicketAttachment> files, CancellationToken ct)
    {
        if (!files.Any()) return;
        foreach (var file in files)
        {
            try
            {
                // Step 1: upload attachment blob
                var bytes   = Convert.FromBase64String(file.Base64);
                var upload  = new ByteArrayContent(bytes);
                upload.Headers.ContentType =
                    new MediaTypeHeaderValue("application/octet-stream");

                var uploadResp = await _http.PostAsync(
                    $"_apis/wit/attachments?fileName={Uri.EscapeDataString(file.FileName)}&api-version=7.0",
                    upload, ct);

                if (!uploadResp.IsSuccessStatusCode) continue;

                var uploadBody = await uploadResp.Content.ReadAsStringAsync(ct);
                using var uploadDoc = JsonDocument.Parse(uploadBody);
                var attachUrl = uploadDoc.RootElement
                                         .GetProperty("url")
                                         .GetString();
                if (attachUrl is null) continue;

                // Step 2: link attachment to work item
                var patchBody = JsonSerializer.Serialize(new[]
                {
                    new
                    {
                        op    = "add",
                        path  = "/relations/-",
                        value = new
                        {
                            rel        = "AttachedFile",
                            url        = attachUrl,
                            attributes = new { comment = $"BLT attachment: {file.FileName}" }
                        }
                    }
                });

                var patchContent = new StringContent(
                    patchBody, Encoding.UTF8, "application/json-patch+json");
                await _http.PatchAsync(
                    $"_apis/wit/workitems/{workItemId}?api-version=7.0",
                    patchContent, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "[ADO] Attachment failed: {File}", file.FileName);
            }
        }
    }

    private static object Patch(string field, object value) => new
    {
        op    = "add",
        path  = $"/fields/{field}",
        value
    };

    private static string BuildHtmlDescription(TicketCreateRequest r)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<p>{r.Description}</p><hr/>");
        sb.AppendLine($"<p><b>Reporter:</b> {r.Reporter.Name} ({r.Reporter.Email})</p>");
        sb.AppendLine($"<p><b>Department:</b> {r.Reporter.Department}</p>");
        sb.AppendLine($"<p><b>BLT Report ID:</b> {r.BltReportId}</p>");
        foreach (var kv in r.Metadata)
            sb.AppendLine($"<p><b>{kv.Key}:</b> {kv.Value}</p>");
        return sb.ToString();
    }

    private static int MapPriority(string bltPriority) => bltPriority switch
    {
        "Critical" => 1,
        "High"     => 2,
        "Low"      => 4,
        _          => 3
    };
}
