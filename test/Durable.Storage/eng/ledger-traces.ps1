#Requires -Version 7.3

# Generates the simulation's ledger traces and validates each against the ledger model with Quint/TLC
# (test/Durable.Storage/docs/spec/verification.md §5.1): the quorum, crash, and Ledger simulations, and the scripted runs whose crashed process
# answers from records it never answered (ServedLandingSimulationTests), whose reader repairs its preferred replica
# (ReadRepairSimulationTests), and whose reader learns the tail from its own replica (LearnTailSimulationTests), each at
# its own seed, write one LedgerTraceData module
# per run under artifacts/ledger-traces, and model-check-quint.ps1 -Traces checks each and its trace mutants.
[CmdletBinding()]
param(
    # The simulation seeds (CLOG_SIM_SEEDS): a seed, a range first..last, or a comma-separated list of either. By default
    # 1..15, more than the per-change corpora run.
    [string] $Seeds,
    [ValidateRange(1, 64)] [int] $Workers = 2
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'dotnet-env.ps1')

$traces = Join-Path $root 'artifacts' 'ledger-traces'
Remove-Item -LiteralPath $traces -Recurse -Force -ErrorAction SilentlyContinue

$project = Join-Path $root 'Orleans.Durable.Storage.Simulation.Tests' 'Orleans.Durable.Storage.Simulation.Tests.csproj'
& dotnet build $project -c SimulationRelease --no-restore
if ($LASTEXITCODE -ne 0) {
    throw "Building the simulation tests failed with exit code $LASTEXITCODE."
}

$env:CLOG_LEDGER_TRACES = $traces
$env:CLOG_SIM_SEEDS = if ($Seeds) { $Seeds } else { '1..15' }

& dotnet test --project $project -c SimulationRelease --no-build -- `
    --filter-class Orleans.Durable.Storage.Simulation.Tests.QuorumSimulationTests `
    --filter-class Orleans.Durable.Storage.Simulation.Tests.LedgerStoreSimulationTests `
    --filter-class Orleans.Durable.Storage.Simulation.Tests.LedgerSimulationTests `
    --filter-class Orleans.Durable.Storage.Simulation.Tests.ServedLandingSimulationTests `
    --filter-class Orleans.Durable.Storage.Simulation.Tests.ReadRepairSimulationTests `
    --filter-class Orleans.Durable.Storage.Simulation.Tests.LearnTailSimulationTests
if ($LASTEXITCODE -ne 0) {
    throw "The simulations which write ledger traces failed with exit code $LASTEXITCODE."
}

& (Join-Path $PSScriptRoot 'model-check-quint.ps1') -Traces $traces -Workers $Workers
