param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [ValidateRange(0, [double]::MaxValue)]
    [double] $CrapThreshold = 30,

    [ValidateRange(1, 1000)]
    [int] $Top = 20
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path $PSScriptRoot -Parent  # test/Durable.Storage
$resultsDirectory = Join-Path $repositoryRoot 'artifacts\TestResults'
$reportDirectory = Join-Path $repositoryRoot 'artifacts\coverage-report'
$testProjects = @(
    @{
        Path = 'Orleans.Durable.Storage.Engine.Buffers.Tests\Orleans.Durable.Storage.Engine.Buffers.Tests.csproj'
        Prefix = 'Orleans.Durable.Storage.Engine.Buffers.Tests'
        Include = '[Orleans.Durable.Storage.Engine]Orleans.Durable.Storage.Engine.Buffers*'
    },
    @{
        Path = 'Orleans.Durable.Storage.Engine.Wal.Tests\Orleans.Durable.Storage.Engine.Wal.Tests.csproj'
        Prefix = 'Orleans.Durable.Storage.Engine.Wal.Tests'
        Include = '[Orleans.Durable.Storage.Engine]Orleans.Durable.Storage.Engine.Wal*'
    },
    @{
        Path = 'Orleans.Durable.Storage.Engine.Ledgers.Tests\Orleans.Durable.Storage.Engine.Ledgers.Tests.csproj'
        Prefix = 'Orleans.Durable.Storage.Engine.Ledgers.Tests'
        Include = '[Orleans.Durable.Storage.Engine]Orleans.Durable.Storage.Engine.Ledgers*'
    },
    @{
        Path = 'Orleans.Durable.Storage.CasPaxos.Tests\Orleans.Durable.Storage.CasPaxos.Tests.csproj'
        Prefix = 'Orleans.Durable.Storage.CasPaxos.Tests'
        Include = '[Orleans.Durable.Storage.CasPaxos]*'
    }
)

if (Test-Path -LiteralPath $resultsDirectory) {
    Remove-Item -LiteralPath $resultsDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null

Push-Location $repositoryRoot
try {
    foreach ($testProject in $testProjects) {
        $testArguments = @(
            'run',
            '--project',
            $testProject.Path,
            '-c',
            $Configuration,
            '--no-build',
            '--',
            '--results-directory',
            'artifacts\TestResults',
            '--coverlet',
            '--coverlet-output-format',
            'cobertura',
            '--coverlet-file-prefix',
            $testProject.Prefix,
            '--coverlet-include',
            $testProject.Include
        )
        & dotnet @testArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Coverage test run for $($testProject.Path) failed with exit code $LASTEXITCODE."
        }
    }
}
finally {
    Pop-Location
}

$coverageFiles = @(Get-ChildItem -LiteralPath $resultsDirectory -Filter '*.cobertura.*.xml')
foreach ($testProject in $testProjects) {
    if (@($coverageFiles | Where-Object { $_.Name.StartsWith("$($testProject.Prefix).", [StringComparison]::Ordinal) }).Count -eq 0) {
        throw "Coverlet did not create a Cobertura report for $($testProject.Path) in $resultsDirectory."
    }
}

$methods = foreach ($coverageFile in $coverageFiles) {
    [xml] $coverage = Get-Content -LiteralPath $coverageFile.FullName -Raw
    foreach ($package in $coverage.coverage.packages.package) {
        foreach ($class in $package.classes.class) {
            foreach ($method in $class.methods.method) {
                $lineRate = [double]::Parse(
                    $method.'line-rate',
                    [Globalization.CultureInfo]::InvariantCulture)
                $complexity = [double]::Parse(
                    $method.complexity,
                    [Globalization.CultureInfo]::InvariantCulture)
                $crap = ($complexity * $complexity * [Math]::Pow(1 - $lineRate, 3)) + $complexity

                [pscustomobject]@{
                    Class = $class.name
                    Method = "$($method.name)$($method.signature)"
                    Complexity = [int] $complexity
                    Coverage = [Math]::Round($lineRate * 100, 1)
                    Crap = [Math]::Round($crap, 2)
                }
            }
        }
    }
}

$hotspots = @(
    $methods
    | Where-Object Crap -GE $CrapThreshold
    | Sort-Object Crap -Descending
    | Select-Object -First $Top
)

Write-Host ''
Write-Host "CRAP threshold: $CrapThreshold"
if ($hotspots.Count -eq 0) {
    Write-Host 'No CRAP hotspots found.'
}
else {
    $hotspots | Format-Table -AutoSize
}

& dotnet tool restore
if ($LASTEXITCODE -ne 0) {
    throw "Tool restore failed with exit code $LASTEXITCODE."
}

$reportPattern = Join-Path $resultsDirectory '*.cobertura.*.xml'
& dotnet reportgenerator `
    "-reports:$reportPattern" `
    "-targetdir:$reportDirectory" `
    '-reporttypes:Html;Cobertura;TextSummary'

if ($LASTEXITCODE -ne 0) {
    throw "Coverage report generation failed with exit code $LASTEXITCODE."
}
