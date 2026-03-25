using System.Text.Json.Serialization;

namespace BLT.Agent.Models;

public class RegisterRequest
{
    [JsonPropertyName("browserId")]   public string BrowserId   { get; set; } = string.Empty;
    [JsonPropertyName("browserName")] public string BrowserName { get; set; } = string.Empty;
}

public class SyncRecordDto
{
    [JsonPropertyName("id")]          public string Id          { get; set; } = string.Empty;
    [JsonPropertyName("recordType")]  public string RecordType  { get; set; } = string.Empty;
    [JsonPropertyName("updatedAt")]   public string UpdatedAt   { get; set; } = string.Empty;
    [JsonPropertyName("version")]     public int    Version     { get; set; } = 1;
    [JsonPropertyName("isDeleted")]   public bool   IsDeleted   { get; set; }
    [JsonPropertyName("payloadJson")] public string PayloadJson { get; set; } = string.Empty;
}

public class PushRequest
{
    [JsonPropertyName("browserId")]   public string BrowserId   { get; set; } = string.Empty;
    [JsonPropertyName("browserName")] public string BrowserName { get; set; } = string.Empty;
    [JsonPropertyName("records")]     public List<SyncRecordDto> Records { get; set; } = [];
}

public class PushResponse
{
    public int          Accepted    { get; set; }
    public int          Conflicts   { get; set; }
    public int          Errors      { get; set; }
    public List<string> ConflictIds { get; set; } = [];
    public List<string> ErrorIds    { get; set; } = [];
}

public class PullRequest
{
    [JsonPropertyName("browserId")] public string BrowserId { get; set; } = string.Empty;
    [JsonPropertyName("sinceSeq")]  public long   SinceSeq  { get; set; } = 0;
    [JsonPropertyName("limit")]     public int    Limit     { get; set; } = 200;
}

public class PullResponse
{
    public long   LastSeq { get; set; }
    public int    Total   { get; set; }
    public bool   HasMore { get; set; }
    public List<SyncRecordDto> Records { get; set; } = [];
}

public class LogPathStatus
{
    public string Path         { get; set; } = string.Empty;
    public string ExpandedPath { get; set; } = string.Empty;
    public bool   Exists       { get; set; }
    public bool   Readable     { get; set; }
}

public class CollectedFile
{
    public string OriginalPath { get; set; } = string.Empty;
    public string ExpandedPath { get; set; } = string.Empty;
    public string Filename     { get; set; } = string.Empty;
    public string Content      { get; set; } = string.Empty;
    public long   Size         { get; set; }
    public long   LastModified { get; set; }
    public long   CollectedAt  { get; set; }
}

public class CollectError
{
    public string Path  { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
}

public class CollectLogsResponse
{
    public bool   Success   { get; set; }
    public int    Collected { get; set; }
    public List<CollectedFile> Files  { get; set; } = [];
    public List<CollectError>  Errors { get; set; } = [];
}

public class TestPathRequest
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
}

public class TestPathResponse
{
    public string  Path         { get; set; } = string.Empty;
    public string  ExpandedPath { get; set; } = string.Empty;
    public bool    Exists       { get; set; }
    public bool    Readable     { get; set; }
    public bool    IsFile       { get; set; }
    public bool    IsDirectory  { get; set; }
    public long    Size         { get; set; }
    public long?   LastModified { get; set; }
    public string? Error        { get; set; }
}

public class AddPathRequest
{
    [JsonPropertyName("path")] public string Path { get; set; } = string.Empty;
}

public class CaptureResult
{
    public bool    Success  { get; set; }
    public int     ExitCode { get; set; }
    public string? Stdout   { get; set; }
    public string? Stderr   { get; set; }
    public string? Error    { get; set; }
}

public class AgentConfig
{
    [JsonPropertyName("logPaths")] public List<string> LogPaths { get; set; } = [];
    [JsonPropertyName("apiToken")] public string       ApiToken { get; set; } = string.Empty;
}
