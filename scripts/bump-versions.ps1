# R13.1 (08-08 consolidation roadmap): single-pass release version bump — closes the
# template/README version drift. Before this script existed, `dotnet new sleipnir-server`
# pinned Sleipnir.Server 1.0.0, so every newly scaffolded project shipped WITHOUT the
# fixes that landed in later releases (the template never rode a release since).
#
# Updates every version PIN in one pass:
#   1. csproj under templates/ + samples/       — PackageReference Include="Sleipnir*"|"Trame*" Version="x.y.z"
#   2. package.json under templates/ + samples/ — "sleipnir-client": "^x.y.z" (and codegen) plus
#      the localfeed .tgz artifact references (sleipnir-client-x.y.z.tgz)
#   3. Repo-root markdown docs and package READMEs — PackageReference snippets / npm pins in fenced code
# Prose version mentions ("as of 1.2.0", CHANGELOG anchors) are intentionally NOT touched —
# only pin-shaped references match.
#
# Usage:  pwsh scripts/bump-versions.ps1 -Version 1.4.3 [-NpmVersion 1.4.2] [-DryRun]
# After running: review the diff, then tag the release (the NuGet lockstep stamps the
# packages; the pins here tell templates/samples/doc examples which release to consume).

[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-.+)?$')]
    [string]$Version,

    # npm pins are decoupled from the NuGet lockstep: sleipnir-client /
    # sleipnir-codegen publish independently (dispatch-only, no tag stamping),
    # so the published npm version can lag the NuGet tag (e.g. NuGet 1.4.2 while
    # npm is still 1.4.1). Pass it explicitly; defaults to -Version when both
    # sides ride the same lockstep.
    [ValidatePattern('^\d+\.\d+\.\d+(?:-.+)?$')]
    [string]$NpmVersion = $Version,

    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

# Pin patterns. Each rule is a (pattern, replacement) pair — the replacement's group
# references are per-pattern (they have different group counts!). Explicit capture
# groups keep the change inside pin shapes; prose ("since 1.2.0") never matches.
$csprojPin = @{
    Pattern = '(<PackageReference\s+Include="(?:Sleipnir|Trame)[^"]*"\s+Version=")\d+\.\d+\.\d+(")'
    Replace = "`${1}$Version`${2}"
}
# npm pin: any sleipnir-* dependency across package.json; keeps an existing caret.
$npmPin = @{
    Pattern = '("(?:sleipnir-[a-z-]+)":\s*")(\^)?(\d+\.\d+\.\d+)(")'
    Replace = "`${1}`${2}$NpmVersion`${4}"
}
# localfeed artifact: the version lives in the FILENAME (file:...sleipnir-client-1.0.0.tgz).
$tgzPin = @{
    Pattern = '(sleipnir-[a-z-]+-)\d+\.\d+\.\d+(\.tgz)'
    Replace = "`${1}$Version`${2}"
}

$docRules = @($csprojPin, $npmPin, $tgzPin)

# Package READMEs (recursive inside their own project dir only).
$packageDirs = @(
    'SleipnirCommon', 'SleipnirCore', 'SleipnirHub', 'SleipnirRest', 'SleipnirWebSocket',
    'SleipnirClient', 'SleipnirServer', 'SleipnirDeveloperUi', 'SleipnirTelemetry',
    'SleipnirTelemetryHeimdall', 'Sleipnir.SourceGenerator', 'Sleipnir.Server.Codegen',
    'Sleipnir.Client.Linq', 'Sleipnir.Client.Linq.Codegen'
)

# Build the target file list once: (path, rules).
$targets = @{}

function Add-Target([string]$Path, [object[]]$Rules) {
    $targets[$Path] = $Rules
}

# 1+2. templates/ + samples/ (recursive).
foreach ($root in @('templates', 'samples')) {
    $dir = Join-Path $repoRoot $root
    if (-not (Test-Path $dir)) { continue }
    foreach ($f in (Get-ChildItem $dir -Recurse -File)) {
        if ($f.FullName -match '\\(node_modules|bin|obj)\\') { continue }
        if ($f.Name -eq 'package.json') {
            Add-Target $f.FullName @($npmPin, $tgzPin)
        }
        elseif ($f.Extension -in '.csproj') {
            Add-Target $f.FullName @($csprojPin)
        }
    }
}

# 3a. Root-level markdown docs (non-recursive).
foreach ($f in (Get-ChildItem $repoRoot -File -Filter '*.md')) {
    Add-Target $f.FullName $docRules
}

# 3b. Package READMEs (the README.md directly inside each package dir).
foreach ($pkg in $packageDirs) {
    $p = Join-Path $repoRoot (Join-Path $pkg 'README.md')
    if (Test-Path $p) { Add-Target $p $docRules }
}

$changed = 0
foreach ($file in ($targets.Keys | Sort-Object)) {
    $text = Get-Content $file -Raw
    $new = $text
    foreach ($rule in $targets[$file]) {
        $new = [regex]::Replace($new, $rule.Pattern, $rule.Replace)
    }
    if ($new -ne $text) {
        $rel = [IO.Path]::GetRelativePath($repoRoot, $file)
        Write-Host "$(if ($DryRun) { '[dry-run] ' } else { '' })$rel" -ForegroundColor Yellow
        $changed++
        if (-not $DryRun) {
            Set-Content -Path $file -Value $new -NoNewline -Encoding utf8
        }
    }
}

if ($changed -eq 0) {
    Write-Host "No version pins matched - either already at $Version or the pin shapes drifted." -ForegroundColor Red
    Write-Host "Check: templates/**, samples/**, package READMEs, root *.md"
}
else {
    Write-Host ""
    Write-Host "$changed file(s) $(if ($DryRun) { 'would have been ' } else { '' })bumped to $Version."
    Write-Host "Next: review git diff, then tag (v$Version) - the NuGet lockstep stamps the packages;"
    Write-Host "these pins keep dotnet new templates and doc examples on the released version."
    if (-not $DryRun) { Write-Host "NOTE: localfeed .tgz artifacts are pack-time outputs (not tracked) - re-pack if you run the template test harness." }
}