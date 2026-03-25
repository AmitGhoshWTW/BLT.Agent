#Requires -RunAsAdministrator

param(
    [switch]$Uninstall,
    [string]$InstallPath = "C:\Program Files\BLT\Agent",
    [string]$ServiceName = "BLTAgent"
)

$ErrorActionPreference = "Stop"

# Use C:\BLTBuild - avoids special chars in user temp paths
$BuildDir   = "C:\BLTBuild\src"
$PublishDir = "C:\BLTBuild\publish"
$SourceDir  = $PSScriptRoot

Write-Host ""
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host "  BLT Agent .NET 9 Installer" -ForegroundColor Cyan
Write-Host "==========================================" -ForegroundColor Cyan
Write-Host ""

if ($Uninstall) {
    Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $ServiceName | Out-Null
    # Remove URL ACL if present
    netsh http delete urlacl url=http://+:42080/ | Out-Null
    Write-Host "Service removed" -ForegroundColor Green
    exit 0
}

# 1 - Stop old service
Write-Host "[ 1/5 ] Stopping existing service..." -ForegroundColor Yellow
Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
Write-Host "   OK" -ForegroundColor Green

# 2 - Scaffold fresh project into clean path (no $ in path)
Write-Host "[ 2/5 ] Scaffolding clean project..." -ForegroundColor Yellow
if (Test-Path "C:\BLTBuild") { Remove-Item "C:\BLTBuild" -Recurse -Force }
New-Item -ItemType Directory -Path $BuildDir   | Out-Null
New-Item -ItemType Directory -Path $PublishDir | Out-Null

Push-Location $BuildDir
dotnet new webapi -n BLTAgent --no-openapi --framework net9.0 --force 2>&1 | Out-Null
Pop-Location

Get-ChildItem "$BuildDir\BLTAgent" | Move-Item -Destination $BuildDir -Force
Remove-Item "$BuildDir\BLTAgent" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "   OK" -ForegroundColor Green

# 3 - Patch csproj: disable static web assets + self-contained win-x64
Write-Host "[ 3/5 ] Patching and copying source files..." -ForegroundColor Yellow

$csproj = (Get-ChildItem $BuildDir -Filter "*.csproj" | Select-Object -First 1).FullName
[xml]$xml = Get-Content $csproj
$pg = $xml.Project.PropertyGroup | Select-Object -First 1

# Disable static web assets (existing)
$n1 = $xml.CreateElement("StaticWebAssetsEnabled");           $n1.InnerText = "false"; $pg.AppendChild($n1) | Out-Null
$n2 = $xml.CreateElement("EnableDefaultStaticWebAssetItems"); $n2.InnerText = "false"; $pg.AppendChild($n2) | Out-Null

# -- FIX: Self-contained win-x64 ---------------------------------------------
# Bundles the .NET runtime alongside the exe so LocalSystem can find all DLLs
# without relying on any PATH or user-profile runtime installation.
$n3 = $xml.CreateElement("RuntimeIdentifier"); $n3.InnerText = "win-x64"; $pg.AppendChild($n3) | Out-Null
$n4 = $xml.CreateElement("SelfContained");     $n4.InnerText = "true";    $pg.AppendChild($n4) | Out-Null
$n5 = $xml.CreateElement("PublishTrimmed");    $n5.InnerText = "false";   $pg.AppendChild($n5) | Out-Null  # trimming breaks EF Core reflection
# ----------------------------------------------------------------------------

$xml.Save($csproj)

# Remove scaffolded files we replace
Remove-Item "$BuildDir\Program.cs" -Force -ErrorAction SilentlyContinue
Get-ChildItem $BuildDir -Filter "appsettings*.json" | Remove-Item -Force
Get-ChildItem $BuildDir -Filter "*.http"            | Remove-Item -Force -ErrorAction SilentlyContinue
Get-ChildItem $BuildDir -Filter "*.cs"              | Remove-Item -Force -ErrorAction SilentlyContinue

# Copy our source
foreach ($f in @("Models","Data","Services")) {
    $s = Join-Path $SourceDir $f
    if (Test-Path $s) { Copy-Item $s (Join-Path $BuildDir $f) -Recurse -Force }
}
Copy-Item (Join-Path $SourceDir "Program.cs")       "$BuildDir\Program.cs"       -Force
Copy-Item (Join-Path $SourceDir "appsettings.json") "$BuildDir\appsettings.json" -Force
if (Test-Path (Join-Path $SourceDir "nuget.config")) {
    Copy-Item (Join-Path $SourceDir "nuget.config") "$BuildDir\nuget.config" -Force
}

# Add packages
Push-Location $BuildDir
dotnet add package Microsoft.EntityFrameworkCore.Sqlite         --version 9.0.0
dotnet add package Microsoft.Extensions.Hosting.WindowsServices --version 9.0.0
Pop-Location

Write-Host "   OK" -ForegroundColor Green

# 4 - Build self-contained
Write-Host "[ 4/5 ] Building (self-contained win-x64)..." -ForegroundColor Yellow

# -- FIX: --self-contained true -r win-x64 -----------------------------------
# RuntimeIdentifier is already in the csproj, but passing -r here too keeps
# the CLI and project in sync and avoids any tooling ambiguity.
dotnet publish "$BuildDir" `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishTrimmed=false `
    -o "$PublishDir" `
    --nologo
# ----------------------------------------------------------------------------

if ($LASTEXITCODE -ne 0) { Write-Error "Build failed"; exit 1 }

$exe = Get-ChildItem $PublishDir -Filter "*.exe" | Select-Object -First 1
if ($null -ne $exe -and $exe.Name -ne "BLTAgent.exe") {
    Rename-Item $exe.FullName "BLTAgent.exe"
}

# Sanity check - self-contained publish must include these DLLs
$mustExist = @(
    "System.ServiceProcess.ServiceController.dll",
    "Microsoft.Extensions.Hosting.WindowsServices.dll"
)
foreach ($dll in $mustExist) {
    if (-not (Test-Path (Join-Path $PublishDir $dll))) {
        Write-Error "Missing expected DLL: $dll - publish may not be self-contained"
        exit 1
    }
}
Write-Host "   Self-contained DLL check passed" -ForegroundColor Green
Write-Host "   Build complete" -ForegroundColor Green

# 5 - Install service
Write-Host "[ 5/5 ] Installing Windows Service..." -ForegroundColor Yellow

# Copy publish output to install path
if (-not (Test-Path $InstallPath)) { New-Item -ItemType Directory -Path $InstallPath -Force | Out-Null }
Get-ChildItem $PublishDir | Copy-Item -Destination $InstallPath -Recurse -Force -ErrorAction SilentlyContinue

# Create or reconfigure service
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    sc.exe config $ServiceName binPath= "`"$InstallPath\BLTAgent.exe`"" | Out-Null
} else {
    New-Service -Name $ServiceName `
        -DisplayName    "BLT Agent" `
        -Description    "BLT Bug Logging Tool - log collection and cross-browser sync" `
        -BinaryPathName "`"$InstallPath\BLTAgent.exe`"" `
        -StartupType    Automatic | Out-Null
}

# -- FIX: Grant LocalSystem rights to bind port 42080 ------------------------
# Without this, Kestrel throws "Access Denied" binding http://+:42080
# when running as a Windows Service (any account, including LocalSystem).
Write-Host "   Granting HTTP port ACL to SYSTEM..." -ForegroundColor Gray
netsh http delete urlacl url=http://+:42080/ 2>&1 | Out-Null   # remove stale entry if any
netsh http add    urlacl url=http://+:42080/ user="NT AUTHORITY\SYSTEM" 2>&1 | Out-Null
Write-Host "   Port ACL granted" -ForegroundColor Green
# ----------------------------------------------------------------------------

# Ensure data directory exists and SYSTEM can write to it
$DataDir = "C:\ProgramData\BLT"
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
$acl  = Get-Acl $DataDir
$rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
    "NT AUTHORITY\SYSTEM", "FullControl",
    "ContainerInherit,ObjectInherit", "None", "Allow"
)
$acl.SetAccessRule($rule)
Set-Acl $DataDir $acl
Write-Host "   Data dir permissions set: $DataDir" -ForegroundColor Green

# Start
Start-Sleep -Seconds 1
Start-Service $ServiceName
Start-Sleep -Seconds 5

if ((Get-Service $ServiceName).Status -ne "Running") {
    Write-Host ""
    Write-Host "Service failed to start. Run this to see the real error:" -ForegroundColor Red
    Write-Host "  Get-EventLog -LogName Application -Newest 5 | Format-List Message" -ForegroundColor Yellow
    exit 1
}

Start-Sleep -Seconds 2
try {
    $h = Invoke-RestMethod "http://localhost:42080/health"
    Write-Host ""
    Write-Host "==========================================" -ForegroundColor Cyan
    Write-Host "  BLT Agent installed successfully" -ForegroundColor Green
    Write-Host "  Port   : http://localhost:42080"  -ForegroundColor White
    Write-Host "  Version: $($h.version)"           -ForegroundColor White
    Write-Host "  Status : $($h.status)"            -ForegroundColor White
    Write-Host "==========================================" -ForegroundColor Cyan
} catch {
    Write-Host "Running but health check failed - service may still be starting" -ForegroundColor Yellow
    Write-Host "Try: Invoke-RestMethod http://localhost:42080/health"             -ForegroundColor Gray
}