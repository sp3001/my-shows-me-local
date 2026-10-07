#Requires -Version 5.1
<#
.SYNOPSIS
  Publishes the Avalonia app and every job into ONE self-contained directory.

.DESCRIPTION
  All seven executables are published into a single output folder so they share one
  appsettings.json and one data\ folder. The DB path is resolved by
  Shows.Infrastructure/Configuration/DataPaths.cs:

    BaseDirectory = configured BaseDirectory  ->  else nearest Shows.sln walking up
                  ->  else the executable's own folder (AppContext.BaseDirectory)
    DatabasePath  = <BaseDirectory>\data\shows.db

  To make the whole folder portable ("everything from one directory, no absolute paths"),
  BaseDirectory is left EMPTY so the last rule wins and the app self-locates its own folder.
  That only works when NO Shows.sln exists above the output folder, so the default -OutDir
  is OUTSIDE the repo (a sibling folder). If you publish inside the repo, the marker walk
  finds Shows.sln and BaseDirectory silently becomes the repo root instead of your folder.

.PARAMETER OutDir
  Single publish target. Defaults to a sibling of the repo (one level up) so the app-folder
  fallback applies. Override with any path that has no Shows.sln above it.

.PARAMETER RID
  Target runtime identifier. Defaults to win-x64.

.PARAMETER SelfContained
  Switch to bundle the .NET runtime (larger output, no runtime needed on the target machine).
  Default is framework-dependent (smaller, requires the .NET 8 runtime installed).

.EXAMPLE
  .\publish-all.ps1
  .\publish-all.ps1 -OutDir 'C:\Apps\shows' -SelfContained
#>
[CmdletBinding()]
param(
    [string]   $OutDir,
    [string]   $RID = 'win-x64',
    [switch]   $SelfContained
)

$ErrorActionPreference = 'Stop'

# Repo root = folder containing this script (it sits at the repo root).
$RepoRoot   = $PSScriptRoot
$Solution   = Join-Path $RepoRoot 'Shows.sln'

# Executables to publish: the app head + every job. Library projects (Domain/Application/
# Infrastructure) are pulled in transitively and must NOT be published on their own.
$Projects = @(
    'Shows.App.Avalonia'
    'Shows.Jobs.Import'
    'Shows.Jobs.ScrapeMyShows'
    'Shows.Jobs.EpisodeRefresh'
    'Shows.Jobs.StatusRecheck'
    'Shows.Jobs.RefreshUpdated'
    'Shows.Jobs.Enrichment'
    'Shows.Jobs.ImdbImport'
)

# Default output: a SIBLING of the repo, so no Shows.sln sits above it and the app-folder
# fallback pins BaseDirectory to the publish folder itself (portable, no absolute paths).
if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $OutDir = Join-Path (Split-Path $RepoRoot -Parent) 'shows-release'
}

# Guard: warn loudly if the chosen folder is under a tree that contains Shows.sln, because
# the marker walk would then override the self-locating fallback.
$probe = Get-Item $RepoRoot
$walk  = Get-Item (Split-Path $OutDir -Parent) -ErrorAction SilentlyContinue
while ($walk) {
    if (Test-Path (Join-Path $walk.FullName 'Shows.sln')) {
        Write-Warning ("'{0}' has Shows.sln above it ('{1}'). DataPaths will use that as the " +
                       "base directory instead of the publish folder. Move -OutDir outside the " +
                       "repo (or set Shows:Data:BaseDirectory) for a portable single folder." -f $OutDir, $walk.FullName)
        break
    }
    $walk = $walk.Parent
}

Write-Host "Solution : $Solution" -ForegroundColor DarkGray
Write-Host "OutDir   : $OutDir"    -ForegroundColor DarkGray
Write-Host "RID      : $RID"       -ForegroundColor DarkGray
Write-Host "Mode     : $(if ($SelfContained) { 'self-contained' } else { 'framework-dependent' })" -ForegroundColor DarkGray
Write-Host ''

# Stop the app so its published files are not locked.
Get-Process -Name 'Shows.App.Avalonia' -ErrorAction SilentlyContinue | Stop-Process -Force

# Clean the target so removed dependencies do not linger.
if (Test-Path $OutDir) {
    Remove-Item -Recurse -Force $OutDir
}
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

$scFlag = @()
if ($SelfContained) { $scFlag = @('--self-contained', 'true') } else { $scFlag = @('--self-contained', 'false') }

foreach ($p in $Projects) {
    $proj = Join-Path $RepoRoot "$p\$p.csproj"
    Write-Host ("=== publish {0} ===" -f $p) -ForegroundColor Cyan
    # All projects share $OutDir. -p:UseAppHost=true guarantees the native .exe launcher for jobs too.
    dotnet publish $proj -c Release -r $RID $scFlag -o $OutDir `
        --nologo -v quiet -p:UseAppHost=true
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for '$p' (exit $LASTEXITCODE)."
    }
}

Write-Host ''
Write-Host '=== published executables ===' -ForegroundColor Green
# Job assemblies use short AssemblyNames (Import.exe, Enrichment.exe, ...), so list every .exe present.
Get-ChildItem $OutDir -Filter *.exe | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0}" -f $_.Name)
}

Write-Host ''
Write-Host "Shared database will be created at: $(Join-Path $OutDir 'data\shows.db')" -ForegroundColor Green
Write-Host "Run any exe from this folder; all share the same data\, log\ and cache\ beside it." -ForegroundColor Green
