#Requires -Version 7.0
<#
.SYNOPSIS
    Run the C# migration engine: migrate (full) or sync (incremental), with optional dry run.
.EXAMPLE
    ./scripts/Invoke-Migration.ps1 -Strategy parallel -Workers 8 -Fresh
.EXAMPLE
    ./scripts/Invoke-Migration.ps1 -Sync -DryRun
#>
[CmdletBinding()]
param(
    [string]$Mapping = (Join-Path $PSScriptRoot '..' 'datasets' 'synpuf' 'mapping.yaml'),
    [ValidateSet('naive', 'executemany', 'batched', 'parallel')]
    [string]$Strategy = 'batched',
    [ValidateRange(1, 256)] [int]$Workers = [Math]::Max(1, [Environment]::ProcessorCount / 2),
    [ValidateRange(1, 64)] [int]$Writers = 2,
    [switch]$Sync,
    [switch]$DryRun,
    [switch]$Fresh,
    [switch]$DeferFks,
    [long]$Limit,      # rows per legacy table: smoke tests only (validation will fail on a slice)
    [string]$Out
)

$ErrorActionPreference = 'Stop'   # any cmdlet error stops the script instead of scrolling past
Set-StrictMode -Version Latest     # typos in variable names are errors, not silent $nulls

$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$cli = Join-Path $root 'src' 'Etl.Cli' 'bin' 'Release' 'net10.0' 'Etl.Cli.dll'
if (-not (Test-Path $cli)) {
    dotnet build -c Release (Join-Path $root 'src' 'Etl.Cli') | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "build failed (exit $LASTEXITCODE)" }   # native commands don't throw; check the code
}

$cliArgs = @(if ($Sync) { 'sync' } else { 'migrate' }), '--mapping', $Mapping, '--strategy', $Strategy,
           '--workers', $Workers, '--writers', $Writers
if ($DryRun)   { $cliArgs += '--dry-run' }
if ($Fresh)    { $cliArgs += '--fresh' }
if ($DeferFks) { $cliArgs += '--defer-fks' }
if ($Out)      { $cliArgs += '--out', $Out }
if ($Limit)    { $cliArgs += '--limit', $Limit }

Write-Verbose "dotnet $cli $($cliArgs -join ' ')"
$json = dotnet $cli @cliArgs
if ($LASTEXITCODE -ne 0) { throw "migration failed (exit $LASTEXITCODE); see the JSON log lines on stderr" }

# Return an object, not text: callers can pipe it (| Select-Object total_s, staged_rows).
$json | Out-String | ConvertFrom-Json
