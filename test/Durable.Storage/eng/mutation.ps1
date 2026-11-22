#Requires -Version 7.3

<#
.SYNOPSIS
Runs Stryker.NET mutation testing (test/Durable.Storage/docs/spec/verification.md §9).

.DESCRIPTION
Mutates each selected unit, a component of the architecture tests' table, with the configuration beside the src
project which compiles it, src/<Project>/stryker-config.<Unit>.json, whose mutate patterns select the component's
source files. It uses the pinned SDK and the Microsoft.Testing.Platform runner, and writes Stryker's reports under
artifacts/mutation/<Unit>. The score counts killed and timed-out mutants over every mutant a test ran against, less
the survivors which test/Durable.Storage/eng/mutation-baseline.json records as equivalent. A core unit, one which the baseline lists,
fails when its score falls below its break threshold.

.PARAMETER Project
The units to mutate, by component name (for example Orleans.Durable.Storage.Engine.Ledgers.Store). Defaults to the core units.

.PARAMETER Since
Mutates only what changed since this commit: a unit's changed source files, or all of it when its test projects or
the shared test sources changed. Units with neither change are skipped.

.PARAMETER Concurrency
Stryker's concurrent test runners.

.PARAMETER Rescore
Scores the reports already under artifacts/mutation against the baseline, without running Stryker.
#>
[CmdletBinding()]
param(
    [string[]] $Project = @(),

    [string] $Since,

    [ValidateRange(1, 256)]
    [int] $Concurrency = [Math]::Max(1, [int][Math]::Floor([Environment]::ProcessorCount / 4)),

    [switch] $Rescore
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
# Unit configurations lie beside the src projects; paths are relative to the repository root, as git reports them.
$storage = Split-Path $PSScriptRoot -Parent
$root = Split-Path (Split-Path $storage -Parent) -Parent
$outputRoot = Join-Path $storage 'artifacts\mutation'
$baselinePath = Join-Path $PSScriptRoot 'mutation-baseline.json'
$baselineDocument = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json -AsHashtable
$baseline = $baselineDocument.projects
$countTolerance = [double] $baselineDocument.countTolerance
$shared = @($baselineDocument.equivalent)

# A unit's configuration, and the directory of the src project it lies beside.
function Get-Configuration {
    param([string] $Name)
    $paths = @(Get-ChildItem -Path (Join-Path $root 'src') -Filter "stryker-config.$Name.json" -Recurse -File)
    if ($paths.Count -ne 1) {
        throw "src has $($paths.Count) stryker-config.$Name.json files, not one."
    }

    $configuration = (Get-Content -LiteralPath $paths[0].FullName -Raw | ConvertFrom-Json -AsHashtable).'stryker-config'
    return [pscustomobject]@{ Settings = $configuration; Directory = $paths[0].DirectoryName; File = $paths[0].Name }
}

# Whether a path relative to the project matches a Stryker mutate pattern: ** spans directories, * one segment's text.
function Test-Pattern {
    param([string] $Path, [string] $Pattern)
    $expression = [regex]::Escape($Pattern).Replace('\*\*/', '(?:.*/)?').Replace('\*', '[^/]*')
    return $Path -match "^$expression$"
}

# Whether a unit's mutate patterns select a path relative to its project.
function Test-Mutated {
    param([string] $Path, [string[]] $Patterns)
    $includes = @($Patterns | Where-Object { -not $_.StartsWith('!') })
    $excludes = @($Patterns | Where-Object { $_.StartsWith('!') } | ForEach-Object { $_.Substring(1) })
    return ($includes.Count -eq 0 -or ($includes | Where-Object { Test-Pattern $Path $_ })) -and -not ($excludes | Where-Object { Test-Pattern $Path $_ })
}

function Get-RelativePath {
    param([string] $Path)
    return [IO.Path]::GetRelativePath($root, $Path).Replace('\', '/')
}

# What a run since $Since mutates of the unit: every mutant ('All') when its test projects or the shared test
# sources they link changed, the unit's source files which changed, or nothing. Stryker's own --since diffs through
# LibGit2Sharp, which resolves a git worktree to its main checkout and so compares another checkout's files.
function Get-SinceScope {
    param($Configuration, [string[]] $ChangedPaths)
    $tests = @('test/Durable.Storage/shared/')
    foreach ($testProject in $Configuration.Settings.'test-projects') {
        $directory = Split-Path (Join-Path $Configuration.Directory $testProject) -Parent
        $tests += (Get-RelativePath $directory) + '/'
    }

    if ($ChangedPaths | Where-Object { $path = $_; $tests | Where-Object { $path.StartsWith($_, [StringComparison]::Ordinal) } }) {
        return 'All'
    }

    $source = (Get-RelativePath $Configuration.Directory) + '/'
    return @($ChangedPaths | Where-Object {
            $_.StartsWith($source, [StringComparison]::Ordinal) -and $_.EndsWith('.cs', [StringComparison]::Ordinal) -and
            (Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf)
        } | ForEach-Object { $_.Substring($source.Length) } | Where-Object { Test-Mutated $_ $Configuration.Settings.mutate })
}

function Get-ChangedPaths {
    $changed = @(& git -C $root diff --name-only $Since --)
    if ($LASTEXITCODE -ne 0) {
        throw "git diff against $Since failed with exit code $LASTEXITCODE."
    }

    $untracked = @(& git -C $root ls-files --others --exclude-standard)
    return @($changed + $untracked)
}

function Format-Code {
    param([string] $Text)
    return ($Text -replace '\s+', ' ').Trim()
}

# The source text a mutant replaces: Stryker's locations are 1-based lines and columns, with an exclusive end column.
function Get-Original {
    param([string[]] $Lines, $Location)
    $start = $Location.start
    $end = $Location.end
    if ($start.line -eq $end.line) {
        return $Lines[$start.line - 1].Substring($start.column - 1, $end.column - $start.column)
    }

    $text = @($Lines[$start.line - 1].Substring($start.column - 1))
    for ($line = $start.line + 1; $line -lt $end.line; $line++) {
        $text += $Lines[$line - 1]
    }

    $text += $Lines[$end.line - 1].Substring(0, $end.column - 1)
    return $text -join "`n"
}

function Measure-Report {
    param([string] $Name, [string] $ReportPath, [bool] $Full)
    $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
    $equivalents = @($shared) + @(if ($baseline[$Name]) { $baseline[$Name].equivalent })
    $matched = @{}
    $counts = @{ Killed = 0; Timeout = 0; Survived = 0; NoCoverage = 0; Equivalent = 0; CompileError = 0 }
    $survivors = @()
    $equivalentMutants = @()
    foreach ($file in $report.files.PSObject.Properties) {
        $relative = Get-RelativePath $file.Name
        $source = $file.Value.source
        $lines = $source -split "`n"
        $lineStarts = [long[]]::new($lines.Count)
        for ($i = 1; $i -lt $lines.Count; $i++) {
            $lineStarts[$i] = $lineStarts[$i - 1] + $lines[$i - 1].Length + 1
        }

        $lines = $lines | ForEach-Object { $_.TrimEnd("`r") }
        $withinMatches = @{}
        foreach ($mutant in $file.Value.mutants) {
            $status = $mutant.status
            if ($status -eq 'CompileError') {
                $counts.CompileError++
                continue
            }

            if ($status -notin @('Killed', 'Timeout', 'Survived', 'NoCoverage')) {
                continue
            }

            if ($status -in @('Killed', 'Timeout')) {
                $counts[$status]++
                continue
            }

            $original = Format-Code (Get-Original $lines $mutant.location)
            $replacement = Format-Code $mutant.replacement
            $start = $lineStarts[$mutant.location.start.line - 1] + $mutant.location.start.column - 1
            $end = $lineStarts[$mutant.location.end.line - 1] + $mutant.location.end.column - 1
            $location = "${relative}:$($mutant.location.start.line):$($mutant.location.start.column)"
            $index = -1
            for ($i = 0; $i -lt $equivalents.Count -and $index -lt 0; $i++) {
                $e = $equivalents[$i]
                if (($e.file -and $e.file -ne $relative) -or ($e.mutator -and $e.mutator -ne $mutant.mutatorName) -or
                    ($e.ContainsKey('original') -and (Format-Code $e.original) -ne $original) -or
                    ($e.ContainsKey('replacement') -and (Format-Code $e.replacement) -ne $replacement)) {
                    continue
                }

                if ($e.within) {
                    if (-not $withinMatches.ContainsKey($i)) {
                        $withinMatches[$i] = @([regex]::Matches($source, $e.within))
                    }

                    if (-not ($withinMatches[$i] | Where-Object { $_.Index -le $start -and $end -le $_.Index + $_.Length })) {
                        continue
                    }
                }

                $index = $i
            }

            if ($index -ge 0) {
                $counts.Equivalent++
                $matched[$index] = $true
                $equivalentMutants += "$location [$($mutant.mutatorName)] $original => $replacement -- $($equivalents[$index].reason)"
                continue
            }

            $counts[$status]++
            $survivors += [pscustomobject]@{
                Status = $status
                Location = $location
                Mutator = $mutant.mutatorName
                Original = $original
                Replacement = $replacement
            }
        }
    }

    # An entry for one file which no surviving mutant matches records a mutant which a test now kills or which the
    # code no longer has; a full run requires those entries to be current.
    $stale = @(if ($Full) {
            for ($i = 0; $i -lt $equivalents.Count; $i++) {
                if ($equivalents[$i].file -and -not $matched.ContainsKey($i)) { $equivalents[$i] }
            }
        })

    $detected = $counts.Killed + $counts.Timeout
    $undetected = $counts.Survived + $counts.NoCoverage
    return [pscustomobject]@{
        Counts = $counts
        Score = if ($detected + $undetected -gt 0) { 100.0 * $detected / ($detected + $undetected) } else { $null }
        Survivors = $survivors
        Equivalents = $equivalentMutants
        Stale = $stale
    }
}

$selected = if ($Project.Count -gt 0) { @($Project | ForEach-Object { $_ -split ',' } | Where-Object { $_ }) } else { @($baseline.Keys | Sort-Object) }
$saved = @{}
foreach ($name in @('DOTNET_ROOT', 'PATH', 'ClogMutationTesting', 'CLOG_TEST_FAILURE_DUMPS')) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name)
}

$results = @()
$failures = @()
try {
    . (Join-Path $PSScriptRoot 'dotnet-env.ps1')

    # A mutation build has its own artifacts tree and test configuration (Directory.Build.props,
    # test/Durable.Storage/Directory.Build.props); a killed mutant is a failing test, which needs no failure dump.
    $env:ClogMutationTesting = 'true'
    $env:CLOG_TEST_FAILURE_DUMPS = '0'

    Push-Location $storage
    try {
        & dotnet tool restore
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet tool restore failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    $changedPaths = if ($Since) { Get-ChangedPaths } else { @() }
    foreach ($name in $selected) {
        $configuration = Get-Configuration $name
        $scope = if ($Since) { Get-SinceScope $configuration $changedPaths } else { 'All' }
        if ($scope -ne 'All' -and @($scope).Count -eq 0) {
            Write-Host "${name}: unchanged since $Since."
            continue
        }

        $output = Join-Path $outputRoot $name
        $elapsed = $null
        if (-not $Rescore) {
            if (Test-Path -LiteralPath $output) {
                Remove-Item -LiteralPath $output -Recurse -Force
            }

            $arguments = @('stryker', '--config-file', $configuration.File, '--output', $output, '--concurrency', $Concurrency, '--skip-version-check')
            if ($scope -ne 'All') {
                foreach ($file in $scope) {
                    $arguments += @('--mutate', "**/$file")
                }
            }

            Write-Host "dotnet $($arguments -join ' ') (in $(Get-RelativePath $configuration.Directory))"
            $null = New-Item -ItemType Directory -Force -Path $output
            $started = Get-Date
            $stopwatch = [Diagnostics.Stopwatch]::StartNew()
            Push-Location $configuration.Directory
            try {
                & dotnet @arguments | Tee-Object -FilePath (Join-Path $output 'stryker.log')
                $exitCode = $LASTEXITCODE
            }
            finally {
                Pop-Location

                # A mutant which a snapshot test kills leaves the test's .received file beside its snapshot; accepting
                # received files (verification §8) would take the mutant's rendering as the snapshot.
                Get-ChildItem -LiteralPath $storage -Recurse -File -Filter '*.received.*' |
                    Where-Object LastWriteTime -GE $started |
                    Remove-Item
            }

            $elapsed = $stopwatch.Elapsed
            if ($exitCode -ne 0) {
                $failures += "${name}: Stryker failed with exit code $exitCode."
                continue
            }
        }

        $reportPath = Join-Path $output 'reports\mutation-report.json'
        $measure = Measure-Report $name $reportPath ($scope -eq 'All')

        # A test whose coverage Stryker could not capture is marked dubious and runs against every mutant, whose
        # timeouts then measure that test rather than the mutant (verification §9).
        $dubious = @(Select-String -LiteralPath (Join-Path $output 'stryker.log') -Pattern 'marking as Dubious' -ErrorAction SilentlyContinue)
        if ($dubious.Count -gt 0) {
            $failures += "${name}: Stryker could not capture the coverage of $($dubious.Count) test(s); see $(Join-Path $output 'stryker.log')."
        }

        $survivorsPath = Join-Path $output 'survivors.txt'
        $measure.Survivors | ForEach-Object { "$($_.Status) $($_.Location) [$($_.Mutator)] $($_.Original) => $($_.Replacement)" } |
            Set-Content -LiteralPath $survivorsPath
        $measure.Equivalents | Set-Content -LiteralPath (Join-Path $output 'equivalent.txt')
        $break = if ($baseline[$name]) { [double]$baseline[$name].break } else { $null }
        $tested = $measure.Counts.Killed + $measure.Counts.Timeout + $measure.Counts.Survived + $measure.Counts.NoCoverage + $measure.Counts.Equivalent
        $result = [pscustomobject]@{
            Project = $name
            Score = if ($null -ne $measure.Score) { [Math]::Round($measure.Score, 2) } else { 'n/a' }
            Break = if ($null -ne $break) { $break } else { '-' }
            Killed = $measure.Counts.Killed
            Timeout = $measure.Counts.Timeout
            Survived = $measure.Counts.Survived
            NoCoverage = $measure.Counts.NoCoverage
            Equivalent = $measure.Counts.Equivalent
            Tested = $tested
            CompileError = $measure.Counts.CompileError
            Wall = if ($elapsed) { $elapsed.ToString('hh\:mm\:ss') } else { '-' }
        }
        $results += $result
        Write-Host ("{0}: score {1}% (break {2}), {3} undetected listed in {4}; wall time {5}" -f
            $name, $result.Score, $result.Break, $measure.Survivors.Count, $survivorsPath, $result.Wall)

        foreach ($stale in $measure.Stale) {
            $failures += "${name}: the baseline's equivalent mutant [$($stale.mutator)] $($stale.original) => $($stale.replacement) in $($stale.file) no longer survives."
        }

        if ($null -ne $break -and $null -ne $measure.Score -and $result.Score -lt $break) {
            $failures += "${name}: mutation score $($result.Score)% is below its break threshold of $break%."
        }

        # A core project's run which tested nothing measured nothing. Changed files may hold no mutant, but not when
        # their mutants failed to compile.
        if ($baseline[$name] -and $null -eq $measure.Score) {
            if ($scope -eq 'All' -or $measure.Counts.CompileError -gt 0) {
                $failures += "${name}: no mutant was tested ($($measure.Counts.CompileError) compile errors), so the run has no score."
            }
            else {
                Write-Warning "${name}: the changed files hold no mutant."
            }
        }

        # A full run tests about as many mutants as the baseline records, and about as many fail to compile: a parse or
        # compile gap moves both counts and, by removing mutants from the score, can raise it. Code which adds or
        # removes mutants moves them too; after review, the baseline records the new counts.
        if ($baseline[$name] -and $scope -eq 'All') {
            foreach ($count in @(@('tested', $tested), @('compileErrors', $measure.Counts.CompileError))) {
                $expected = $baseline[$name][$count[0]]
                if ($null -eq $expected) {
                    $failures += "${name}: test/Durable.Storage/eng/mutation-baseline.json records no $($count[0]) count; this run's is $($count[1])."
                }
                elseif ([Math]::Abs($count[1] - $expected) -gt $countTolerance * [Math]::Max($expected, 1)) {
                    $failures += "${name}: $($count[0]) $($count[1]) differs from the baseline's $expected by more than $(100 * $countTolerance)%; review the run, then record the count in test/Durable.Storage/eng/mutation-baseline.json."
                }
            }
        }
    }
}
finally {
    foreach ($name in $saved.Keys) {
        [Environment]::SetEnvironmentVariable($name, $saved[$name])
    }
}

if ($results.Count -gt 0) {
    $null = New-Item -ItemType Directory -Force -Path $outputRoot
    $results | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $outputRoot 'summary.json')
    $results | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Host $_ }
    throw "Mutation testing failed: $($failures.Count) finding(s)."
}

Write-Host 'Mutation testing completed successfully.'
