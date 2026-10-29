#requires -Version 5.1
<#
.SYNOPSIS
Download and verify the default local pipeline models.
.EXAMPLE
.\download-models.ps1
.EXAMPLE
.\download-models.ps1 -ModelsDir .\cache\models
#>
[CmdletBinding()]
param(
    [string] $ModelsDir = (Join-Path $PSScriptRoot 'models'),
    [string] $Manifest = (Join-Path $PSScriptRoot 'tools\models\models.tsv'),
    [string] $Groups
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Test-ModelFile([string] $Path, [long] $Size, [string] $Sha256) {
    if (-not [System.IO.File]::Exists($Path)) { return $false }
    if ((Get-Item -LiteralPath $Path).Length -ne $Size) { return $false }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -eq $Sha256
}

try {
    $curlName = if ($env:OS -eq 'Windows_NT') { 'curl.exe' } else { 'curl' }
    $curlCommand = Get-Command $curlName -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $curl = $curlCommand.Source
    $ModelsDir = [System.IO.Path]::GetFullPath($ModelsDir)
    $entries = @()
    $seen = @{}
    $pathPattern = '^[a-zA-Z0-9_-][a-zA-Z0-9._-]*(/[a-zA-Z0-9_-][a-zA-Z0-9._-]*)*$'
    foreach ($line in Get-Content -LiteralPath $Manifest) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
        $columns = $line.Split("`t")
        if ($columns.Count -ne 4) { throw 'Invalid manifest: expected SHA-256, bytes, path, URL (TSV).' }
        $sha, $size, $relative, $url = $columns
        if ($sha -notmatch '^[a-fA-F0-9]{64}$' -or
            $size -notmatch '^[1-9][0-9]{0,14}$' -or
            $relative -notmatch $pathPattern -or $relative.EndsWith('.part', [StringComparison]::OrdinalIgnoreCase) -or
            $url -notmatch '^https?://[^\s]+$') {
            throw "Invalid manifest entry: $relative"
        }
        if ($seen.ContainsKey($relative)) { throw "Duplicate manifest path: $relative" }
        $seen[$relative] = $true
        $entries += [pscustomobject]@{ Sha256 = $sha; Size = [long] $size; Path = $relative; Url = $url }
    }
    if ($entries.Count -eq 0) { throw 'Empty model manifest.' }
    foreach ($entry in $entries) {
        $parent = $entry.Path
        while ($parent.Contains('/')) {
            $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
            if ($seen.ContainsKey($parent)) { throw "Conflicting manifest path: $parent" }
        }
    }

    if ($PSBoundParameters.ContainsKey('Groups')) {
        $allowedGroups = @('vad', 'whisper', 'sense-voice', 'paraformer', 'llm', 'tts', 'smart-turn')
        $selectedGroups = $Groups.Split(',')
        $groupSet = @{}
        foreach ($group in $selectedGroups) {
            if ([string]::IsNullOrWhiteSpace($group) -or
                $group -notin $allowedGroups -or $groupSet.ContainsKey($group)) {
                throw "Invalid group filter: $group"
            }
            $groupSet[$group] = $true
        }

        $filteredEntries = @($entries | Where-Object {
            $groupSet.ContainsKey($_.Path.Split('/')[0])
        })
        foreach ($group in $selectedGroups) {
            if (-not ($filteredEntries | Where-Object { $_.Path.StartsWith("$group/") })) {
                throw "Invalid group filter: no manifest entries for $group"
            }
        }
        $entries = $filteredEntries
    }

    foreach ($entry in $entries) {
        $target = Join-Path $ModelsDir ($entry.Path.Replace('/', [System.IO.Path]::DirectorySeparatorChar))
        $partial = "$target.part"
        if (Test-ModelFile $target $entry.Size $entry.Sha256) {
            Write-Host "[verified] $($entry.Path)"
            continue
        }
        if ([System.IO.File]::Exists($target)) { Write-Warning "Replacing corrupt model: $($entry.Path)" }
        [System.IO.Directory]::CreateDirectory([System.IO.Path]::GetDirectoryName($target)) | Out-Null
        if (-not (Test-ModelFile $partial $entry.Size $entry.Sha256)) {
            if ([System.IO.File]::Exists($partial) -and (Get-Item -LiteralPath $partial).Length -ge $entry.Size) {
                Write-Warning "Discarding invalid completed partial: $($entry.Path)"
                Remove-Item -LiteralPath $partial
            }
            Write-Host "[download/resume] $($entry.Path)"
            $curlArgs = @(
                '--fail', '--location', '--show-error', '--retry', '3',
                '--connect-timeout', '30', '--speed-limit', '1024', '--speed-time', '60',
                '--proto', '=http,https', '--proto-redir', '=http,https',
                '--output', $partial, '--url', $entry.Url
            )
            & $curl @curlArgs --continue-at -
            $code = $LASTEXITCODE
            if ($code -eq 33) {
                Write-Warning "Server does not support resuming; restarting: $($entry.Path)"
                & $curl @curlArgs
                $code = $LASTEXITCODE
            }
            if ($code -ne 0) {
                throw "curl exited $code for $($entry.Path). Partial retained; rerun this command to resume."
            }
            if (-not (Test-ModelFile $partial $entry.Size $entry.Sha256)) {
                Remove-Item -LiteralPath $partial
                throw "SHA-256/size mismatch: $($entry.Path). Invalid partial removed; rerun to download again."
            }
        }
        if ([System.IO.File]::Exists($target)) {
            [System.IO.File]::Replace($partial, $target, [System.Management.Automation.Language.NullString]::Value)
        } else {
            [System.IO.File]::Move($partial, $target)
        }
        Write-Host "[verified] $($entry.Path)"
    }
    Write-Host "All $($entries.Count) model files verified: $ModelsDir"
} catch {
    Write-Error $_ -ErrorAction Continue
    exit 1
}
