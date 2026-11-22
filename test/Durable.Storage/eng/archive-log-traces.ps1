#Requires -Version 7.3

# Generates the archive-log drain simulation's traces and validates each against test/Durable.Storage/specs/archive-logs/ArchiveLogs.qnt
# (test/Durable.Storage/docs/spec/verification.md §5.1): ArchiveLogTraceTests writes one Quint module per seed under
# artifacts/archive-log-traces, and model-check-quint.ps1 -Traces replays each with quint test.
[CmdletBinding()]
param(
    # The simulation seeds (DURABLE_STORAGE_SIM_SEEDS): a seed, a range first..last, or a comma-separated list of either. By default
    # 1..20, more than the per-change corpus runs.
    [string] $Seeds
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'dotnet-env.ps1')

$traces = Join-Path $root 'artifacts' 'archive-log-traces'
Remove-Item -LiteralPath $traces -Recurse -Force -ErrorAction SilentlyContinue

$project = Join-Path $root 'Orleans.Durable.Storage.Simulation.Tests' 'Orleans.Durable.Storage.Simulation.Tests.csproj'
& dotnet build $project -c SimulationRelease --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Building the simulation tests failed with exit code $LASTEXITCODE."
}

$env:DURABLE_STORAGE_ARCHIVE_LOG_TRACES = $traces
$env:DURABLE_STORAGE_SIM_SEEDS = if ($Seeds) { $Seeds } else { '1..20' }

& dotnet test --project $project -c SimulationRelease --no-build -- --filter-class Orleans.Durable.Storage.Simulation.Tests.ArchiveLogTraceTests
if ($LASTEXITCODE -ne 0) {
    throw "The simulation which writes archive-log traces failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot 'model-check-quint.ps1') -Traces $traces
