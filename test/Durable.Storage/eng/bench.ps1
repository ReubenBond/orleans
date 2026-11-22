#Requires -Version 7.3
<#
.SYNOPSIS
Runs the BenchmarkDotNet microbenchmarks which a filter selects, with the pinned SDK.

.DESCRIPTION
Builds test/Durable.Storage/benchmarks/Orleans.Durable.Storage.Benchmarks in Release without restoring (just restore does), and passes the filter and any
further options to BenchmarkDotNet. Validate a new benchmark with --job Dry, and compare against an explicit
baseline (AGENTS.md, "Performance").

.EXAMPLE
./eng/bench.ps1 '*Wal*' --job Dry
#>
[CmdletBinding()]
param(
    # The BenchmarkDotNet filter which selects the benchmarks; '*' selects them all.
    [Parameter(Mandatory, Position = 0)]
    [string] $Filter,

    # Further BenchmarkDotNet options, such as --job Dry.
    [Parameter(ValueFromRemainingArguments)]
    [string[]] $Options = @()
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'dotnet-env.ps1')
$project = Join-Path (Split-Path $PSScriptRoot -Parent) 'benchmarks' 'Orleans.Durable.Storage.Benchmarks'
& dotnet run --project $project -c Release --no-restore -- --filter $Filter @Options
exit $LASTEXITCODE
