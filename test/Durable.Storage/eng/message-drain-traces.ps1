#Requires -Version 7.3

# Generates message-drain simulation traces (both LSM and Archive Logs, plus negative-control mutant traces)
# and validates each against test/Durable.Storage/specs/message-drain/MessageDrain.qnt (verification.md §5.1).
[CmdletBinding()]
param(
    # The simulation seeds (DURABLE_STORAGE_SIM_SEEDS): a seed, a range first..last, or a comma-separated list of either.
    [string] $Seeds
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'dotnet-env.ps1')

$traces = Join-Path $root 'artifacts' 'message-drain-traces'
Remove-Item -LiteralPath $traces -Recurse -Force -ErrorAction SilentlyContinue

$project = Join-Path $root 'Orleans.Durable.Storage.Simulation.Tests' 'Orleans.Durable.Storage.Simulation.Tests.csproj'
& dotnet build $project -c SimulationRelease --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Building the simulation tests failed with exit code $LASTEXITCODE."
}

$env:DURABLE_STORAGE_MESSAGE_DRAIN_TRACES = $traces
$env:DURABLE_STORAGE_SIM_SEEDS = if ($Seeds) { $Seeds } else { '1..5' }

& dotnet test --project $project -c SimulationRelease --no-build -- --filter-class Orleans.Durable.Storage.Simulation.Tests.LocalDrainTraceTests
if ($LASTEXITCODE -ne 0) {
    throw "The simulation which writes message-drain traces failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot 'model-check-quint.ps1') -Traces $traces
