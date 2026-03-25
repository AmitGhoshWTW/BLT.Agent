# BLT Agent

> .NET 9 Windows Service providing cross-browser sync, log file collection, and screen capture for the BLT Bug Logging Tool PWA.

---

## Table of Contents

- [Overview](#overview)
- [Architecture Role](#architecture-role)
- [Tech Stack](#tech-stack)
- [Project Structure](#project-structure)
- [Getting Started](#getting-started)
- [Installation](#installation)
- [Configuration](#configuration)
- [API Reference](#api-reference)
- [Authentication](#authentication)
- [Sync Engine](#sync-engine)
- [Database](#database)
- [Log Collection](#log-collection)
- [Monitoring & Diagnostics](#monitoring--diagnostics)
- [Deployment](#deployment)
- [Troubleshooting](#troubleshooting)

---

## Overview

The BLT Agent is a lightweight ASP.NET Core minimal API hosted as a Windows Service on the end-user workstation. It runs silently in the background on `localhost:42080` and provides three core capabilities:

- **Cross-browser sync** — Chrome and Edge on the same workstation share bug report data via a delta push/pull protocol backed by SQLite
- **Log file collection** — watches configured directories and serves log files to the PWA on demand
- **Screen capture** — relays screen capture requests from the PWA to the desktop

The agent is intentionally simple and workstation-scoped. It does not communicate with other agents or any cloud database. Each workstation is an isolated unit.

---

## Architecture Role

```
┌─────────────────────────────────────────────────────┐
│                User Workstation (VPN)                │
│                                                      │
│   Chrome PWA                    Edge PWA             │
│   Dexie.js (IndexedDB)         Dexie.js (IndexedDB) │
│        │    push/pull                │               │
│        │    every 10s               │               │
│        └──────────┬─────────────────┘               │
│                   ▼                                  │
│         ┌─────────────────────┐                     │
│         │     BLT Agent       │                     │
│         │  .NET 9 Win Service │                     │
│         │  localhost:42080    │                     │
│         │  SQLite sync.db     │                     │
│         └──────────┬──────────┘                     │
└────────────────────┼───────────────────────────────┘
                     │ HTTPS (VPN)
                     ▼
          BLT JIRA Service (.NET 10)
          Azure App Service
                     │
                     ▼
               JIRA Cloud
```

**What the Agent does NOT do:**
- Does not connect to CouchDB or any remote database
- Does not sync between different workstations
- Does not store JIRA credentials
- Does not expose any endpoint outside localhost

---

## Tech Stack

| Component | Technology |
|---|---|
| Runtime | .NET 9 |
| Web framework | ASP.NET Core minimal API |
| Hosting | `Microsoft.Extensions.Hosting.WindowsServices` |
| ORM | Entity Framework Core 9 |
| Database | SQLite via `Microsoft.EntityFrameworkCore.Sqlite` |
| Publish target | Self-contained `win-x64` |
| Install | PowerShell (`Install-BLTAgent.ps1`) |

---

## Project Structure

```
BLT.Agent-v2/
│
├── Program.cs                   # App bootstrap, DI, middleware, endpoint mapping
│
├── Models/
│   ├── RegisterRequest.cs       # POST /api/sync/register body
│   ├── PushRequest.cs           # POST /api/sync/push body
│   ├── PullRequest.cs           # POST /api/sync/pull body
│   ├── PullResponse.cs          # Pull response with records + pagination
│   ├── AddPathRequest.cs        # POST /api/log-paths body
│   └── TestPathRequest.cs       # POST /api/test-path body
│
├── Data/
│   └── AgentDbContext.cs        # EF Core DbContext — sync tables + metadata
│
├── Services/
│   ├── SyncEngine.cs            # Push/pull delta sync logic, browser registry
│   └── LogCollectorService.cs   # Log file watching, audit trail, API token
│
├── appsettings.json             # Logging + Kestrel configuration
├── BLTAgent.csproj              # Project file
└── Install-BLTAgent.ps1         # Installer script (run as Administrator)
```

---

## Getting Started

### Prerequisites

- .NET 9 SDK
- Windows 10 / Windows 11
- PowerShell 5.1+ (for installer)
- Administrator rights (for service installation)

### Build manually

```bash
dotnet restore
dotnet build -c Release
```

### Run as console app (for development/testing)

```bash
dotnet run
# Starts on http://localhost:42080
# Ctrl+C to stop
```

When running as a console app (not a Windows Service), all behaviour is identical
except Windows Service lifecycle events are skipped. Use this mode for local
development and debugging.

### Verify it is running

```bash
curl http://localhost:42080/health
```

Expected response:
```json
{
  "status": "ok",
  "agent": "BLT Agent",
  "version": "3.0.0",
  "timestamp": 1774436047985
}
```

---

## Installation

### Install as Windows Service

```powershell
# Run as Administrator from project root
.\Install-BLTAgent.ps1
```

The installer performs these steps automatically:

| Step | Action |
|---|---|
| 1 | Stops any existing BLTAgent service |
| 2 | Scaffolds a clean .NET 9 webapi project at `C:\BLTBuild\` |
| 3 | Patches `.csproj` — disables static web assets, sets `win-x64` self-contained |
| 4 | Copies source files (Models, Data, Services, Program.cs, appsettings.json) |
| 5 | Adds NuGet packages (EF Core SQLite, Windows Services hosting) |
| 6 | Publishes self-contained `win-x64` to `C:\BLTBuild\publish\` |
| 7 | Validates critical DLLs are present (sanity check) |
| 8 | Copies publish output to `C:\Program Files\BLT\Agent\` |
| 9 | Creates Windows Service with `LocalSystem` account, `Automatic` start |
| 10 | Grants HTTP port ACL — `netsh http add urlacl url=http://+:42080/ user="NT AUTHORITY\SYSTEM"` |
| 11 | Creates `C:\ProgramData\BLT\` with `FullControl` for `NT AUTHORITY\SYSTEM` |
| 12 | Starts the service and verifies via `/health` |

### Install options

```powershell
# Default install
.\Install-BLTAgent.ps1

# Install to a custom path
.\Install-BLTAgent.ps1 -InstallPath "D:\BLT\Agent"

# Uninstall (stops service, deletes it, removes URL ACL)
.\Install-BLTAgent.ps1 -Uninstall
```

### Why self-contained publish?

The installer publishes with `--self-contained true -r win-x64`. This bundles the
entire .NET 9 runtime alongside the exe. Without this, the `LocalSystem` service
account cannot find the runtime (it is installed in the developer's user profile,
not system-wide), causing a `FileNotFoundException` on startup.

`PublishTrimmed=false` is explicitly set because trimming removes reflection-based
code paths that EF Core and ASP.NET Core rely on.

### Uninstall

```powershell
.\Install-BLTAgent.ps1 -Uninstall
```

---

## Configuration

### `appsettings.json`

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore.Database.Command": "Warning"
    }
  },
  "Kestrel": {
    "Endpoints": {
      "Http": {
        "Url": "http://localhost:42080"
      }
    }
  }
}
```

### Database path

Hardcoded in `Program.cs` to `C:\ProgramData\BLT\sync.db`. This is intentional —
`CommonApplicationData` is accessible to `LocalSystem` and survives user profile
changes.

```csharp
var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    "BLT");
Directory.CreateDirectory(dataDir);
var dbPath = Path.Combine(dataDir, "sync.db");
```

### SQLite PRAGMAs applied on startup

| PRAGMA | Value | Reason |
|---|---|---|
| `journal_mode` | `WAL` | Allows concurrent reads during writes — critical for sync API |
| `foreign_keys` | `ON` | Enforces FK constraints (SQLite has them off by default) |

---

## API Reference

All endpoints except `/health` and `/api/token` require a `Bearer` token
(see [Authentication](#authentication)).

### Health

#### `GET /health`

No authentication required. Returns agent liveness status.

**Response:**
```json
{
  "status": "ok",
  "agent": "BLT Agent",
  "version": "3.0.0",
  "timestamp": 1774436047985
}
```

---

### Token

#### `GET /api/token`

Responds to `127.0.0.1` and `::1` only. Returns 403 for any other IP.
Used by the PWA on startup to fetch the Bearer token.

**Response:**
```json
{ "token": "a3f9b2c1d4e5f6a7b8c9d0e1f2a3b4c5d6e7f8a9b0c1d2e3f4a5b6c7d8e9f0a1" }
```

---

### Sync

#### `POST /api/sync/register`

Registers a browser instance with the agent. Must be called before push/pull.

**Request:**
```json
{
  "browserId": "BLT-ABC123-CHROME",
  "browserName": "Google Chrome"
}
```

**Response:**
```json
{
  "registered": true,
  "browserId": "BLT-ABC123-CHROME"
}
```

---

#### `POST /api/sync/push`

Pushes local records from the PWA to the agent's SQLite database.

**Request:**
```json
{
  "browserId": "BLT-ABC123-CHROME",
  "browserName": "Google Chrome",
  "records": [
    {
      "id": "report:1774436047985-abc123",
      "recordType": "report",
      "updatedAt": "2026-03-25T12:00:00Z",
      "version": 1,
      "isDeleted": false,
      "payloadJson": "{...full report object...}"
    }
  ]
}
```

**Response:**
```json
{
  "accepted": 1,
  "conflictIds": []
}
```

---

#### `POST /api/sync/pull`

Pulls records from the agent that are newer than `sinceSeq`. Delta only —
never returns the full dataset.

**Request:**
```json
{
  "browserId": "BLT-ABC123-CHROME",
  "sinceSeq": 47,
  "limit": 200
}
```

**Response:**
```json
{
  "records": [
    {
      "seq": 48,
      "id": "report:1774436047985-xyz789",
      "recordType": "report",
      "payloadJson": "{...}",
      "updatedAt": "2026-03-25T12:05:00Z"
    }
  ],
  "lastSeq": 48,
  "hasMore": false,
  "total": 1
}
```

---

#### `GET /api/sync/status/{browserId}`

Returns sync state for a specific browser.

**Response:**
```json
{
  "browserId": "BLT-ABC123-CHROME",
  "browserName": "Google Chrome",
  "lastSyncTime": "2026-03-25T12:05:00Z",
  "totalPushed": 42,
  "totalPulled": 18
}
```

---

#### `GET /api/sync/browsers`

Lists all registered browsers.

**Response:**
```json
[
  {
    "browserId": "BLT-ABC123-CHROME",
    "browserName": "Google Chrome",
    "registeredAt": "2026-03-25T08:00:00Z",
    "lastSeenAt": "2026-03-25T12:05:00Z"
  }
]
```

---

#### `GET /api/sync/changelog?since=0&limit=100`

Returns the audit log of all sync operations. Used for monitoring.

**Query parameters:**

| Parameter | Type | Default | Description |
|---|---|---|---|
| `since` | long | 0 | Return entries after this sequence number |
| `limit` | int | 100 | Max entries to return |

**Response:**
```json
{
  "rows": [
    {
      "seq": 51,
      "browserId": "BLT-ABC123-CHROME",
      "operation": "pull",
      "timestamp": "2026-03-25T12:05:00Z"
    }
  ],
  "total": 51
}
```

---

#### `DELETE /api/sync/clearall`

Wipes all sync records, browser registrations, and changelog entries.
Use for testing only.

**Response:**
```json
{ "cleared": true }
```

---

### Agent

#### `GET /api/info`

Returns full agent information including configuration and runtime state.

---

#### `GET /api/log-paths`

Returns list of configured log file directories being watched.

**Response:**
```json
{
  "paths": [
    "C:\\Logs\\AppName",
    "C:\\Users\\username\\AppData\\Local\\AppName\\logs"
  ]
}
```

---

#### `POST /api/log-paths`

Adds a new log directory to the watch list.

**Request:**
```json
{ "path": "C:\\Logs\\NewApp" }
```

**Response:**
```json
{
  "success": true,
  "paths": ["C:\\Logs\\AppName", "C:\\Logs\\NewApp"]
}
```

---

#### `DELETE /api/log-paths`

Removes a log directory from the watch list.

**Request:**
```json
{ "path": "C:\\Logs\\NewApp" }
```

---

#### `GET /api/collect-logs`

Triggers immediate collection of log files from all configured paths.

**Response:**
```json
{
  "files": [
    {
      "id": "log-abc123",
      "filename": "app.log",
      "path": "C:\\Logs\\AppName\\app.log",
      "size": 204800,
      "modified": "2026-03-25T11:00:00Z",
      "fileData": "base64encodedcontent..."
    }
  ],
  "collected": 3,
  "failed": 0
}
```

---

#### `POST /api/test-path`

Tests whether a given path is accessible and readable.

**Request:**
```json
{ "path": "C:\\Logs\\AppName" }
```

**Response:**
```json
{
  "accessible": true,
  "exists": true,
  "fileCount": 4,
  "error": null
}
```

---

#### `GET /api/run-capture?exePath=...&args=...`

Runs an external executable for screen capture. `exePath` and `args` are optional.
Consider whitelisting allowed executables in production.

---

### Developer

#### `GET /api/dev/query?sql=SELECT...`

Raw SQL query against `sync.db`. **Disabled in production** (`IsProduction()` check).
Returns JSON array of rows.

```powershell
# Example — list all tables
Invoke-RestMethod "http://localhost:42080/api/dev/query?sql=SELECT+name+FROM+sqlite_master+WHERE+type='table'"
```

---

## Authentication

The agent generates a **64-character random token** at startup, held in memory
by `LogCollectorService` (singleton). The token changes every time the service
restarts.

### Token lifecycle

```
Service starts
  → LogCollectorService generates random token
  → Token held in memory only (never written to disk)

PWA starts
  → GET http://localhost:42080/api/token
  → Token returned (localhost-only, no auth required)
  → Token cached in syncManager._agentToken

Every sync call
  → Authorization: Bearer {token}
  → Middleware validates against LogCollectorService.ApiToken

Service restarts
  → New token generated
  → PWA receives 401 on next sync
  → syncManager clears _agentToken, re-fetches, retries once
```

### Security note

The `/api/token` endpoint is restricted by IP in middleware:

```csharp
app.MapGet("/api/token", (HttpContext ctx, LogCollectorService a) =>
{
    var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "";
    if (ip != "127.0.0.1" && ip != "::1" && ip != "::ffff:127.0.0.1")
        return Results.Json(new { error = "Forbidden" }, statusCode: 403);
    return Results.Ok(new { token = a.ApiToken });
});
```

All sync endpoints are protected by middleware that validates the Bearer token
and writes an audit entry for every unauthorised attempt.

---

## Sync Engine

`SyncEngine.cs` implements the delta sync protocol.

### Data model

**SyncRecords table** — stores all pushed bug report data

| Column | Type | Description |
|---|---|---|
| `seq` | long (autoincrement) | Global sequence number — drives delta pull |
| `browserId` | string | Which browser pushed this record |
| `docId` | string | PouchDB-style document ID (e.g. `report:1774436047985`) |
| `recordType` | string | `report`, `screenshot`, or `logfile` |
| `payloadJson` | text | Full serialised record as JSON |
| `updatedAt` | datetime | Last modified timestamp |
| `isDeleted` | bool | Soft delete flag |

**Browsers table** — browser registry

| Column | Description |
|---|---|
| `browserId` | Unique browser identifier generated by PWA |
| `browserName` | Human-readable name (Chrome, Edge, etc.) |
| `registeredAt` | First seen |
| `lastSeenAt` | Updated on every push/pull |

**SyncMetadata table** — per-browser sync state

| Column | Description |
|---|---|
| `browserId` | FK to Browsers |
| `lastSeq` | Highest seq seen by this browser |
| `lastSyncTime` | Timestamp of last successful sync |

### Delta pull algorithm

```
Client sends:  { browserId, sinceSeq: 47 }
Agent queries: SELECT * FROM SyncRecords WHERE seq > 47 LIMIT 200
Agent returns: records[] + lastSeq + hasMore
Client stores: lastSeq → use as sinceSeq on next pull
```

### Conflict resolution

Last-Write-Wins based on `updatedAt`. When the same `docId` is pushed from two
different browsers, the record with the later `updatedAt` is kept.

---

## Database

### Location

```
C:\ProgramData\BLT\sync.db       ← main data file
C:\ProgramData\BLT\sync.db-wal   ← WAL journal (temporary, checkpointed automatically)
C:\ProgramData\BLT\sync.db-shm   ← shared memory index
```

All three files must be copied together for a consistent backup.

### Schema creation

`EnsureCreatedAsync()` is called on startup — creates all tables from the EF Core
model if the database does not exist. No migrations are used; schema is created
fresh on first run.

### Inspect the database directly

```powershell
# Using the dev query endpoint (non-production only)
$t = (Invoke-RestMethod http://localhost:42080/api/token).token

# List all tables
Invoke-RestMethod "http://localhost:42080/api/dev/query?sql=SELECT+name+FROM+sqlite_master+WHERE+type='table'" `
    -Headers @{ Authorization = "Bearer $t" }

# Count records by type
Invoke-RestMethod "http://localhost:42080/api/dev/query?sql=SELECT+recordType,COUNT(*)+as+count+FROM+SyncRecords+GROUP+BY+recordType" `
    -Headers @{ Authorization = "Bearer $t" }

# Check sequence numbers
Invoke-RestMethod "http://localhost:42080/api/dev/query?sql=SELECT+MIN(seq),MAX(seq),COUNT(*)+FROM+SyncRecords" `
    -Headers @{ Authorization = "Bearer $t" }
```

---

## Log Collection

`LogCollectorService.cs` manages the list of log file directories and serves their
contents to the PWA.

### Adding log paths

Via API (PWA or PowerShell):
```powershell
$t = (Invoke-RestMethod http://localhost:42080/api/token).token

Invoke-RestMethod -Method POST http://localhost:42080/api/log-paths `
    -Headers @{ Authorization = "Bearer $t"; "Content-Type" = "application/json" } `
    -Body '{ "path": "C:\\Logs\\MyApp" }'
```

### Collecting logs immediately

```powershell
$t = (Invoke-RestMethod http://localhost:42080/api/token).token
Invoke-RestMethod http://localhost:42080/api/collect-logs `
    -Headers @{ Authorization = "Bearer $t" }
```

### Audit log

Every push, pull, register, and unauthorised attempt is written to the
`Changelog` table in `sync.db`. View via:

```powershell
$t = (Invoke-RestMethod http://localhost:42080/api/token).token
Invoke-RestMethod "http://localhost:42080/api/sync/changelog?since=0&limit=50" `
    -Headers @{ Authorization = "Bearer $t" } | ConvertTo-Json
```

---

## Monitoring & Diagnostics

### Windows Event Viewer

All `ILogger` output goes to Windows Application Event Log under `.NET Runtime`:

```
eventvwr.msc
  → Windows Logs → Application
    → Filter by source: .NET Runtime
```

### PowerShell — service status

```powershell
# Quick status
Get-Service BLTAgent

# Full details including PID
Get-Service BLTAgent | Select-Object *

# Recent errors
Get-EventLog -LogName Application -Source ".NET Runtime" -Newest 10 |
    Select-Object TimeGenerated, Message | Format-List
```

### PowerShell — live sync monitor

```powershell
$token = (Invoke-RestMethod http://localhost:42080/api/token).token
$lastSeen = 0

while ($true) {
    try {
        $log = Invoke-RestMethod `
            "http://localhost:42080/api/sync/changelog?since=$lastSeen&limit=10" `
            -Headers @{ Authorization = "Bearer $token" }

        if ($log.rows.Count -gt 0) {
            $log.rows | ForEach-Object {
                Write-Host "[$($_.timestamp)] $($_.operation.ToUpper().PadRight(8)) browser=$($_.browserId)" `
                    -ForegroundColor Cyan
            }
            $lastSeen = ($log.rows | Measure-Object seq -Maximum).Maximum
        }
    } catch {
        Write-Host "$(Get-Date -f HH:mm:ss) Agent not reachable" -ForegroundColor Yellow
    }
    Start-Sleep -Seconds 3
}
```

### Quick connectivity test

```powershell
# Health
Invoke-RestMethod http://localhost:42080/health

# Port listening
netstat -ano | findstr :42080

# Token endpoint
Invoke-RestMethod http://localhost:42080/api/token

# Registered browsers
$t = (Invoke-RestMethod http://localhost:42080/api/token).token
Invoke-RestMethod http://localhost:42080/api/sync/browsers `
    -Headers @{ Authorization = "Bearer $t" } | ConvertTo-Json
```

---

## Deployment

### Installer script

The `Install-BLTAgent.ps1` script handles everything. It should be distributed
alongside the compiled source files:

```
BLT.Agent-v2/
├── Install-BLTAgent.ps1     ← run this as Administrator
├── Program.cs
├── appsettings.json
├── Models/
├── Data/
└── Services/
```

### Publish manually (if needed)

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o ./publish
```

### Service account

The service runs as `LocalSystem`. This gives it:
- Full access to `C:\ProgramData\BLT\` (created by installer)
- Ability to bind `localhost:42080` (URL ACL granted by installer)
- Access to any log file path that `SYSTEM` can read

If log paths require a specific domain account, change the service account:

```powershell
sc.exe config BLTAgent obj= "DOMAIN\ServiceAccount" password= "password"
```

### Firewall

No firewall rules are needed. The agent binds to `localhost` only — no inbound
connections from the network are possible.

---

## Troubleshooting

### Service fails to start — `Cannot start service BLTAgent`

```powershell
# Get the real error
Get-EventLog -LogName Application -Source ".NET Runtime" -Newest 5 | Format-List Message
Get-EventLog -LogName System -Source "Service Control Manager" -Newest 5 | Format-List Message
```

**Common causes and fixes:**

| Error message | Cause | Fix |
|---|---|---|
| `FileNotFoundException: System.ServiceProcess.ServiceController` | Framework-dependent publish | Re-run `Install-BLTAgent.ps1` — it now publishes self-contained |
| `Access is denied` on port binding | URL ACL missing | `netsh http add urlacl url=http://+:42080/ user="NT AUTHORITY\SYSTEM"` |
| `appsettings.json not found` | Working directory wrong | Add `Directory.SetCurrentDirectory(exeDir)` to `Program.cs` |
| Timeout after 30s | Any unhandled exception on startup | Check Event Viewer for `.NET Runtime` errors |

### Service running but port not responding

```powershell
# Confirm port is bound
netstat -ano | findstr :42080

# Confirm correct PID owns it
tasklist | findstr <PID>

# Try alternative address
Invoke-RestMethod http://127.0.0.1:42080/health
Invoke-RestMethod http://[::1]:42080/health
```

### 401 Unauthorized on sync endpoints

The agent restarted and generated a new token. The PWA auto-recovers in one
retry cycle. If the problem persists:

```powershell
# Verify token endpoint works
Invoke-RestMethod http://localhost:42080/api/token

# Test sync endpoint directly
$t = (Invoke-RestMethod http://localhost:42080/api/token).token
Invoke-RestMethod -Method POST http://localhost:42080/api/sync/pull `
    -Headers @{ Authorization = "Bearer $t"; "Content-Type" = "application/json" } `
    -Body '{ "browserId": "test", "sinceSeq": 0 }'
```

### Database locked errors

SQLite WAL mode allows concurrent reads, but if two writes collide:

```powershell
# Check for stale WAL file
ls C:\ProgramData\BLT\

# If sync.db-wal is very large (>50MB) — restart the service to force checkpoint
Restart-Service BLTAgent
```

### Reset all sync data (testing only)

```powershell
$t = (Invoke-RestMethod http://localhost:42080/api/token).token
Invoke-RestMethod -Method DELETE http://localhost:42080/api/sync/clearall `
    -Headers @{ Authorization = "Bearer $t" }
```

Or stop the service and delete the database file:

```powershell
Stop-Service BLTAgent
Remove-Item "C:\ProgramData\BLT\sync.db*"
Start-Service BLTAgent
# Database recreated fresh on next start
```

---

## Security Notes

- The agent binds to `localhost` only — not accessible from the network
- Bearer token is random 64-char hex, regenerated on every service start
- `/api/token` endpoint enforces IP check — only `127.0.0.1` / `::1`
- All unauthorised access attempts are written to the `Changelog` audit table
- All traffic operates within the corporate VPN perimeter
- No credentials of any kind are stored on disk by the agent
- `/api/dev/query` raw SQL endpoint is disabled in production builds


*BLT Agent | .NET 9 Windows Service | v3.x | March 2026*