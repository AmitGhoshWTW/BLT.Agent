// Ticketing/Contracts/TicketModels.cs
//
// Platform-agnostic models.
// These are the only types the BLT endpoint ever touches.
// Provider implementations map these to/from their platform-specific API models.

namespace BLT.Agent.Ticketing.Contracts;

// ── Inbound request (from BLT report) ────────────────────────────────────────
public class TicketCreateRequest
{
    // Core fields — required by all platforms
    public string Title       { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string Category    { get; set; } = "Bug";
    public string Priority    { get; set; } = "Medium";   // Low / Medium / High / Critical

    // Reporter info
    public ReporterInfo Reporter { get; set; } = new();

    // Attachments
    public List<TicketAttachment> Screenshots { get; set; } = new();
    public List<TicketAttachment> LogFiles    { get; set; } = new();

    // BLT-specific metadata (stored as labels/custom fields depending on platform)
    public Dictionary<string, string> Metadata { get; set; } = new();

    // Original BLT report ID for traceability
    public string BltReportId { get; set; } = string.Empty;

    // Optional: override project/board key (falls back to provider config default)
    public string? ProjectKeyOverride { get; set; }
}

public class ReporterInfo
{
    public string Name       { get; set; } = string.Empty;
    public string Email      { get; set; } = string.Empty;
    public string Department { get; set; } = string.Empty;
}

public class TicketAttachment
{
    public string  FileName  { get; set; } = string.Empty;
    public string  Base64    { get; set; } = string.Empty;
    public string  MimeType  { get; set; } = "application/octet-stream";
    public long    FileSize  { get; set; }
}

// ── Outbound result (normalised across all platforms) ────────────────────────
public class TicketResult
{
    public bool   Success     { get; set; }
    public string TicketKey   { get; set; } = string.Empty;  // e.g. BLT-123 or #12345
    public string TicketUrl   { get; set; } = string.Empty;
    public string Provider    { get; set; } = string.Empty;
    public string? Error      { get; set; }

    // Platform-specific extra fields (don't break the contract for edge cases)
    public Dictionary<string, object> PlatformExtras { get; set; } = new();

    public static TicketResult Ok(string key, string url, string provider) =>
        new() { Success = true, TicketKey = key, TicketUrl = url, Provider = provider };

    public static TicketResult Fail(string error, string provider) =>
        new() { Success = false, Error = error, Provider = provider };
}

public class TicketStatus
{
    public string TicketKey  { get; set; } = string.Empty;
    public string Status     { get; set; } = string.Empty;  // e.g. "Open", "In Progress", "Done"
    public string Assignee   { get; set; } = string.Empty;
    public string TicketUrl  { get; set; } = string.Empty;
}
