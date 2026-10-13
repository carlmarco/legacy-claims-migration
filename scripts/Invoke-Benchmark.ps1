#Requires -Version 7.0
<#
.SYNOPSIS
    Benchmark every load strategy with interleaved repeated trials; every run must pass validation to count.
.DESCRIPTION
    Order is round-robin (A, B, C, A, B, C...), not five of A in a row, so machine drift spreads evenly across
    strategies. Each run starts from an empty target, is validated against the oracle (checksums + FK), and is
    discarded if validation fails. Writes one CSV row per run plus a machine fingerprint.
.EXAMPLE
    ./scripts/Invoke-Benchmark.ps1 -Trials 5 -Strategies batched, parallel -Workers 2, 4, 8
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 20)] [int]$Trials = 3,
    [string[]]$Strategies = @('executemany', 'batched', 'parallel'),
    [int[]]$Workers = @(4, 8),
    [switch]$DeferFks,
    [long]$Limit,      # smoke test only: runs on a slice will (correctly) fail validation
    [string]$OutDir = (Join-Path $PSScriptRoot '..' 'results' 'bench' 'matrix')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$root = Resolve-Path (Join-Path $PSScriptRoot '..')
$python = if ($IsWindows) { Join-Path $root '.venv' 'Scripts' 'python.exe' } else { Join-Path $root '.venv' 'bin' 'python' }

# Machine fingerprint: numbers mean nothing without the hardware and versions that produced them.
$fingerprint = [ordered]@{
    timestamp = (Get-Date).ToString('o')
    os        = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    cpu_count = [Environment]::ProcessorCount
    dotnet    = (dotnet --version)
    git       = (git -C $root rev-parse --short HEAD)
}
$fingerprint | ConvertTo-Json | Set-Content (Join-Path $OutDir 'machine.json')

# Configurations: parallel expands to one config per worker count.
$configs = foreach ($s in $Strategies) {
    if ($s -eq 'parallel') { foreach ($w in $Workers) { [pscustomobject]@{ Strategy = $s; Workers = $w } } }
    else { [pscustomobject]@{ Strategy = $s; Workers = 1 } }
}

$rows = for ($t = 1; $t -le $Trials; $t++) {
    foreach ($c in $configs) {
        $name = "$($c.Strategy)-w$($c.Workers)-t$t"
        Write-Host "== $name $(Get-Date -Format HH:mm:ss)"
        $statsPath = Join-Path $OutDir "$name.json"
        $stats = & (Join-Path $PSScriptRoot 'Invoke-Migration.ps1') -Strategy $c.Strategy -Workers $c.Workers -Fresh `
                    -DeferFks:$DeferFks -Limit $Limit -Out $statsPath

        $valPath = Join-Path $OutDir "$name.validation.json"
        & $python -m etl.cli validate --mapping (Join-Path $root 'datasets' 'synpuf' 'mapping.yaml') --oracle synpuf_oracle --out $valPath | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "validation failed to run for $name" }
        $v = Get-Content $valPath -Raw | ConvertFrom-Json
        # @(...) forces an array: under StrictMode, an empty Where-Object result is $null and $null.Count throws.
        $valid = @($v.checksums.PSObject.Properties.Value | Where-Object { -not $_.match }).Count -eq 0 -and
                 @($v.fk_orphans_in_target.PSObject.Properties.Value | Where-Object { $_ -ne 0 }).Count -eq 0
        if (-not $valid) { Write-Warning "$name failed validation: excluded from results" }

        [pscustomobject]@{
            run = $name; strategy = $c.Strategy; workers = $c.Workers; trial = $t; valid = $valid
            extract_load_s = $stats.extract_load_s; merge_s = $stats.merge_s; total_s = $stats.total_s
            staged_rows = $stats.staged_rows; peak_rss_mb = $stats.peak_rss_mb
        }
    }
}
$csv = Join-Path $OutDir 'runs.csv'
$rows | Export-Csv -Path $csv

# Median and range per configuration, valid runs only.
$rows | Where-Object valid | Group-Object strategy, workers | ForEach-Object {
    $t = @($_.Group.total_s | Sort-Object)
    [pscustomobject]@{
        config = $_.Name; runs = $t.Count
        median_total_s = $t[[int][Math]::Floor(($t.Count - 1) / 2)]
        min_s = $t[0]; max_s = $t[-1]
    }
} | Format-Table -AutoSize
Write-Host "runs: $csv"
