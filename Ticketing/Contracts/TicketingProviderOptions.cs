// Ticketing/Contracts/TicketingProviderOptions.cs
//
// Bound from appsettings.json → "Ticketing" section.
// Switching platforms = change "ActiveProvider" only.

namespace BLT.Agent.Ticketing.Contracts;

public class TicketingOptions
{
    public const string Section = "Ticketing";

    // ── The only value you change to switch platforms ─────────────────────────
    // Valid values: "JiraDataCenter" | "JiraCloud" | "AzureDevOps"
    public string ActiveProvider { get; set; } = "JiraDataCenter";

    // Per-provider config — only the active one is used at runtime
    public JiraDataCenterOptions JiraDataCenter { get; set; } = new();
    public JiraCloudOptions      JiraCloud      { get; set; } = new();
    public AzureDevOpsOptions    AzureDevOps    { get; set; } = new();
}

// ── JIRA Data Center ──────────────────────────────────────────────────────────
public class JiraDataCenterOptions
{
    public string BaseUrl     { get; set; } = string.Empty;  // https://jira.company.com
    public string ProjectKey  { get; set; } = "BLT";
    public string IssueType   { get; set; } = "Bug";
    public string Username    { get; set; } = string.Empty;
    public string ApiToken    { get; set; } = string.Empty;  // personal access token
    public int    TimeoutSecs { get; set; } = 30;
}

// ── JIRA Cloud ────────────────────────────────────────────────────────────────
public class JiraCloudOptions
{
    public string CloudId     { get; set; } = string.Empty;  // yourorg.atlassian.net
    public string ProjectKey  { get; set; } = "BLT";
    public string IssueType   { get; set; } = "Bug";
    public string Email       { get; set; } = string.Empty;
    public string ApiToken    { get; set; } = string.Empty;  // cloud API token
    public int    TimeoutSecs { get; set; } = 30;
}

// ── Azure DevOps ──────────────────────────────────────────────────────────────
public class AzureDevOpsOptions
{
    public string Organization { get; set; } = string.Empty;  // myorg
    public string Project      { get; set; } = string.Empty;  // MyProject
    public string WorkItemType { get; set; } = "Bug";
    public string AreaPath     { get; set; } = string.Empty;  // optional
    public string PersonalAccessToken { get; set; } = string.Empty;
    public int    TimeoutSecs  { get; set; } = 30;
}
