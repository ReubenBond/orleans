#Requires -Version 7.3

# Generates the ledger map committer and transition simulations' traces and validates each against
# test/Durable.Storage/specs/ledgers/LedgerMap.qnt (test/Durable.Storage/docs/spec/verification.md §5.1): LedgerMapCommitterSimulationTests and
# TransitionSimulationTests write one Quint module per seed under artifacts/ledger-map-traces, and
# model-check-quint.ps1 -Traces replays each with quint test and checks that each trace mutant is rejected at the step it
# changes.
[CmdletBinding()]
param(
    # The simulation seeds (DURABLE_STORAGE_SIM_SEEDS): a seed, a range first..last, or a comma-separated list of either. By default
    # 1..10, more than the per-change corpora run.
    [string] $Seeds
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'dotnet-env.ps1')

$traces = Join-Path $root 'artifacts' 'ledger-map-traces'
Remove-Item -LiteralPath $traces -Recurse -Force -ErrorAction SilentlyContinue

$project = Join-Path $root 'Orleans.Durable.Storage.Simulation.Tests' 'Orleans.Durable.Storage.Simulation.Tests.csproj'
& dotnet build $project -c SimulationRelease --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Building the simulation tests failed with exit code $LASTEXITCODE."
}

$env:DURABLE_STORAGE_LEDGER_MAP_TRACES = $traces
$env:DURABLE_STORAGE_SIM_SEEDS = if ($Seeds) { $Seeds } else { '1..10' }

& dotnet test --project $project -c SimulationRelease --no-build -- `
    --filter-method Orleans.Durable.Storage.Simulation.Tests.LedgerMapCommitterSimulationTests.EveryAnswerHoldsInTheRegistersHistoryUnderFaults `
    --filter-method Orleans.Durable.Storage.Simulation.Tests.TransitionSimulationTests.RacingTransitionsKeepTheMapsInvariantsAndEndServed
if ($LASTEXITCODE -ne 0) {
    throw "The simulations which write ledger map traces failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot 'model-check-quint.ps1') -Traces $traces
