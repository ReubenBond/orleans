#Requires -Version 7.3

[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [ValidateSet('Restore', 'Build', 'Format', 'Test', 'TestDebug', 'CasPaxos', 'Coverage', 'Explore', 'ExploreSchedules', 'Emulators', 'EndToEnd')]
    [string] $Check,

    # With -Check Format, applies the formatting and analyzer fixes instead of verifying that none is needed.
    [switch] $Fix,

    # With -Check Explore, the seeds CLOG_SIM_SEEDS gives the simulation corpora: a seed, a range first..last, or a
    # comma-separated list of either (docs/spec/verification.md §7.4).
    [string] $Seeds,

    # With -Check ExploreSchedules, the conformance classes to explore: a class name, or a wildcard at its start or end, as
    # the test host's --filter-class takes it (docs/spec/verification.md §4.1, §12).
    [string] $Classes = '*ConformanceTests'
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'dotnet-env.ps1')
$dotnet = (Get-Command dotnet -ErrorAction Stop).Source

# Aspire's lock graph includes host-specific dashboard packages (#25).
if (-not $IsWindows) {
    $hostId = [Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier
    $env:ClogRestoreLockDirectory = (Join-Path $root "artifacts\validation\locks\$hostId") + [IO.Path]::DirectorySeparatorChar
    $null = New-Item -ItemType Directory -Force -Path $env:ClogRestoreLockDirectory
    $env:RestoreLockedMode = 'false'
    Write-Host "Using host-specific restore locks: $env:ClogRestoreLockDirectory"
}

function Invoke-Dotnet {
    param([string[]] $Arguments)
    Write-Host "dotnet $($Arguments -join ' ')"
    & $dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments[0]) failed with exit code $LASTEXITCODE."
    }
}

function Restore {
    param([string] $Target)
    $arguments = @('restore', $Target)
    if ($IsWindows) {
        $arguments += '--locked-mode'
    }

    Invoke-Dotnet $arguments
}

function Invoke-ValidationScript {
    param([string] $Name, [string[]] $Arguments = @())
    & pwsh -NoLogo -NoProfile -File (Join-Path $PSScriptRoot $Name) @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE."
    }
}

# Dumps, crash sequences, hang logs, platform diagnostics, and seeded-simulation histories (docs/spec/verification.md).
function Get-Evidence {
    param([string] $Directory, [string] $Name)
    Get-ChildItem -LiteralPath $Directory -File -Filter "$Name*" |
        Where-Object { $_.Extension -in @('.dmp', '.diag', '.log') }
}

function Write-Evidence {
    param([string] $Directory, [string] $Name)
    $evidence = @(Get-Evidence $Directory $Name)
    Write-Host "$Name failed. Evidence in ${Directory}:"
    foreach ($file in $evidence) {
        Write-Host ("  {0} ({1:N0} bytes)" -f $file.FullName, $file.Length)
    }
    if ($evidence.Count -eq 0) {
        Write-Host '  none'
    }
}

function Get-TestProjects {
    [xml] $solution = Get-Content -LiteralPath (Join-Path $root 'Clog.slnx') -Raw
    $projects = @($solution.SelectNodes('//Project') | Where-Object { $_.Path -like 'tests/*' } |
        ForEach-Object { $_.Path.Replace('/', [IO.Path]::DirectorySeparatorChar) })
    if ($projects.Count -eq 0) {
        throw 'The solution contains no test projects.'
    }

    return $projects
}


# A test project joins the Debug leg by setting ClogDebugLeg in its project file, which Orleans.Durable.Storage.Architecture.Tests reads too.
function Test-InDebugLeg {
    param([string] $Project)
    [xml] $xml = Get-Content -LiteralPath $Project -Raw
    return $xml.SelectNodes('//ClogDebugLeg') | Where-Object { $_.InnerText.Trim() -eq 'true' } | Select-Object -First 1
}

function Get-ProjectProperties {
    param([string] $Project)
    $json = & $dotnet msbuild $Project -nologo -getProperty:IsTestProject -getProperty:ClogTestSessionTimeout -getProperty:OrleansSimulationInstrumentedTestProject
    if ($LASTEXITCODE -ne 0) {
        throw "Evaluating the properties of $Project failed with exit code $LASTEXITCODE."
    }

    return ($json | Out-String | ConvertFrom-Json).Properties
}

# A simulation test project builds and runs in the simulation configuration of the check's configuration, which
# compiles the conditional seams in (docs/spec/build-packaging.md §1); every other project in the configuration itself.
function Get-TestConfiguration {
    param($Properties, [ValidateSet('Debug', 'Release')] [string] $Configuration)
    return $Properties.OrleansSimulationInstrumentedTestProject -eq 'true' ? "Simulation$Configuration" : $Configuration
}

function Invoke-TestProject {
    param(
        [Parameter(Mandatory)] [string] $Project,
        [Parameter(Mandatory)] [ValidateSet('Debug', 'Release', 'SimulationDebug', 'SimulationRelease')] [string] $Configuration,
        [Parameter(Mandatory)] [string] $Timeout,
        # Fails the run when any test skipped, as the emulator suites require, but for a skip whose reason is, verbatim,
        # one of these.
        [switch] $NoSkips,
        [string[]] $AllowedSkips = @(),
        # Whether explicit tests run: off, on (with the rest), or only.
        [ValidateSet('off', 'on', 'only')] [string] $Explicit = 'off',
        # Further test-host arguments, such as a trait filter.
        [string[]] $Filter = @(),
        # The fewest tests the run must find, so a test which drops out of a filtered run fails it.
        [int] $MinimumTests = 1,
        # A directory of the check's own for this run's report and evidence, so one run's do not replace another's.
        [string] $Subdirectory)
    $Project = $Project.Replace('\', [IO.Path]::DirectorySeparatorChar)
    $name = [IO.Path]::GetFileNameWithoutExtension($Project)
    $directory = Join-Path $root "artifacts\validation\$Check"
    if ($Subdirectory) {
        $directory = Join-Path $directory $Subdirectory
    }

    $null = New-Item -ItemType Directory -Force -Path $directory
    $report = Join-Path $directory "$name.xml"
    if (Test-Path -LiteralPath $report -PathType Leaf) {
        Remove-Item -LiteralPath $report
    }

    # Each run replaces the project's evidence from earlier runs.
    Get-Evidence $directory $name | Remove-Item

    $arguments = @(
        'test', '--project', $Project, '-c', $Configuration, '--no-build', '--no-restore',
        '--explicit', $Explicit, '--minimum-expected-tests', $MinimumTests,
        '--report-xunit-xml', '--report-xunit-xml-filename', "$name.xml",
        '--results-directory', $directory, '--timeout', $Timeout,
        '--diagnostic', '--diagnostic-verbosity', 'Debug', '--diagnostic-output-directory', $directory
    ) + $Filter

    try {
        Invoke-Dotnet $arguments
        Assert-Report $report $name -NoSkips:$NoSkips -AllowedSkips $AllowedSkips
    }
    catch {
        Write-Evidence $directory $name
        # A test host which crashes or stops itself leaves its controller to report exit code 7. The controller, which
        # runs no test code, ends with a terminated process's code only when another process ends it: TerminateProcess's
        # -1 on Windows (Stop-Process, Process.Kill), and 128 plus SIGKILL or SIGTERM on Unix.
        $exitCode = $LASTEXITCODE
        if ($exitCode -in ($IsWindows ? @(-1) : @(137, 143))) {
            Write-Host ("${name}: the test process tree was terminated from outside (exit code $exitCode), by Stop-Process " +
                "or Process.Kill in another process or by the session timeout ($Timeout); a crash of the test host reports exit code 7.")
        }

        throw
    }
}

function Assert-Report {
    param([string] $Report, [string] $Name, [switch] $NoSkips, [string[]] $AllowedSkips = @())
    [xml] $xml = Get-Content -LiteralPath $report -Raw
    $tests = @($xml.SelectNodes('//test'))
    $assemblies = @($xml.SelectNodes('//assembly'))
    $total = ($assemblies | ForEach-Object { [int] $_.total } | Measure-Object -Sum).Sum
    $passed = @($tests | Where-Object result -EQ 'Pass').Count
    $skips = @($tests | Where-Object { $_.result -in @('Skip', 'NotRun') })
    $skipped = $skips.Count
    $allowedTests = @($skips | Where-Object {
            $reason = $_.SelectSingleNode('reason').InnerText
            $_.result -eq 'Skip' -and $reason -cin $AllowedSkips
        })
    $allowed = $allowedTests.Count
    $reportedPassed = ($assemblies | ForEach-Object { [int] $_.passed } | Measure-Object -Sum).Sum
    $failed = ($assemblies | ForEach-Object { [int] $_.failed } | Measure-Object -Sum).Sum
    $errors = ($assemblies | ForEach-Object { [int] $_.errors } | Measure-Object -Sum).Sum
    if ($total -ne $tests.Count -or $total -lt 1 -or $passed -lt 1 `
        -or $reportedPassed -ne $passed -or $passed + $skipped -ne $total `
        -or $failed -ne 0 -or $errors -ne 0 -or $xml.SelectNodes('//errors/error').Count -ne 0 `
        -or ($NoSkips -and $skipped -ne $allowed)) {
        throw "Incomplete or unsuccessful test report: $report"
    }

    $script:AllowedSkipped += @($allowedTests | ForEach-Object { $_.name })
    $allowance = $AllowedSkips ? " ($allowed for an allowed reason)" : ''
    Write-Host "${name}: $passed passed, $skipped skipped/explicit-not-run${allowance}; report: $report"
}

function Write-EmulatorRecord {
    param([string] $Record, [string[]] $Configurations, [string[]] $Projects)
    $commit = (git -C $root rev-parse HEAD).Trim()
    $dirty = [bool] (git -C $root status --porcelain)
    # Both AppHosts take their images from apphost/shared/EmulatorImages.cs, which pins each by digest.
    $apphost = Get-Content -LiteralPath (Join-Path $root 'apphost\shared\EmulatorImages.cs') -Raw
    $pattern = 'EmulatorImage (?<name>\w+) \{ get; \} = new\("(?<image>[^"]+)", (?:"(?<tag>[^"]+)"|null), "(?<digest>[0-9a-f]{64})"\)'
    $images = @([regex]::Matches($apphost, $pattern) | ForEach-Object {
            [ordered] @{
                emulator = $_.Groups['name'].Value
                image = $_.Groups['image'].Value
                tag = $_.Groups['tag'].Success ? $_.Groups['tag'].Value : $null
                digest = "sha256:$($_.Groups['digest'].Value)"
            }
        })
    if ($images.Count -eq 0) {
        throw 'The AppHost pins no image by digest.'
    }

    $reports = @(foreach ($configuration in $Configurations) {
            foreach ($project in $Projects) {
                "artifacts/validation/Emulators/$configuration/$([IO.Path]::GetFileNameWithoutExtension($project)).xml"
            }
        })
    [ordered] @{
        commit = $commit
        uncommittedChanges = $dirty
        completed = [DateTimeOffset]::UtcNow.ToString('o')
        images = $images
        reports = $reports
        allowedSkips = @($script:AllowedSkipped)
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $Record -Encoding utf8
    Write-Host "Emulator run record: $Record (commit $commit$($dirty ? ', with uncommitted changes' : ''))"
}

Push-Location $root
try {
    switch ($Check) {
        'Restore' { Restore 'Clog.slnx' }
        'Build' { Invoke-Dotnet @('build', 'Clog.slnx', '-c', 'Release', '--no-restore') }
        # MSBuildWorkspace also opens the submodules' projects, which the solution reaches through references; their
        # code keeps upstream's formatting (docs/spec/build-packaging.md §2).
        'Format' { Invoke-Dotnet (@('format', 'Clog.slnx', '--no-restore', '--exclude', 'vendor/') + ($Fix ? @() : @('--verify-no-changes'))) }
        'Test' {
            foreach ($project in Get-TestProjects) {
                $properties = Get-ProjectProperties $project
                if ($properties.IsTestProject -eq 'true') {
                    Invoke-TestProject $project -Configuration (Get-TestConfiguration $properties 'Release') -Timeout $properties.ClogTestSessionTimeout
                }
                else {
                    # A library under tests/, such as the simulation kit, has no tests of its own.
                    Write-Host "$project is not a test project."
                }
            }
        }
        'TestDebug' {
            # Release builds compile Debug.Assert away, so the Debug leg builds its projects in Debug and runs them
            # (docs/spec/verification.md §12).
            $leg = @(Get-TestProjects | Where-Object { Test-InDebugLeg $_ })
            if ($leg.Count -eq 0) {
                throw 'No test project sets ClogDebugLeg.'
            }

            foreach ($project in $leg) {
                $properties = Get-ProjectProperties $project
                $configuration = Get-TestConfiguration $properties 'Debug'
                Invoke-Dotnet @('build', $project, '-c', $configuration, '--no-restore')
                Invoke-TestProject $project -Configuration $configuration -Timeout $properties.ClogTestSessionTimeout
            }
        }
        'CasPaxos' {
            foreach ($name in @('Orleans.Durable.Storage.CasPaxos.Tests', 'Orleans.Durable.Storage.Architecture.Tests')) {
                $project = "tests\$name\$name.csproj"
                Restore $project
                Invoke-Dotnet @('build', $project, '-c', 'Release', '--no-restore')
                Invoke-TestProject $project -Configuration 'Release' -Timeout (Get-ProjectProperties $project).ClogTestSessionTimeout
            }
        }
        'Coverage' { Invoke-ValidationScript 'coverage.ps1' }
        'Emulators' {
            # The cloud providers, and plain Orleans over the composition's archive logs, against the AppHost's emulators,
            # which the tests start through Aspire's testing host; an emulator which does not start fails its tests
            # (docs/spec/verification.md §10). Both configurations run, each with its own report, so the Debug build's
            # poisoned pages expose an upload which read its content after its lease ended. With the emulators on, a skipped
            # test is a failure, except for the live-account cases while their CLOG_*_LIVE env vars (CLOG_AZURE_LIVE,
            # CLOG_S3_LIVE, CLOG_S3_EXPRESS_LIVE, CLOG_GCS_LIVE) are unset, whose skip reasons match each live-account
            # helper's SkipReason verbatim; with them set no skip is allowed. A passing run records its commit, the
            # AppHost's image digests, and the tests whose skip it allowed beside the reports.
            $projects = @('tests\Orleans.Durable.Storage.Cloud.Tests\Orleans.Durable.Storage.Cloud.Tests.csproj', 'tests\Orleans.Durable.Storage.Interop.Tests\Orleans.Durable.Storage.Interop.Tests.csproj')
            $env:CLOG_EMULATORS = '1'
            $record = Join-Path $root 'artifacts\validation\Emulators\run.json'
            if (Test-Path -LiteralPath $record -PathType Leaf) {
                Remove-Item -LiteralPath $record
            }

            $azureOnlySkips = @()
            if ([string]::IsNullOrWhiteSpace($env:CLOG_AZURE_LIVE)) {
                $azureOnlySkips += 'The live-account fidelity run waits on CLOG_AZURE_LIVE: set it to a storage account''s blob service endpoint, on which the identity DefaultAzureCredential signs in with holds Storage Blob Data Owner.'
            }

            $cloudSkips = @($azureOnlySkips)
            if ([string]::IsNullOrWhiteSpace($env:CLOG_S3_LIVE)) {
                $cloudSkips += 'The live-account S3 fidelity run waits on CLOG_S3_LIVE: set it to a general-purpose S3 bucket name accessible with the AWS SDK''s default credentials and region.'
            }

            if ([string]::IsNullOrWhiteSpace($env:CLOG_S3_EXPRESS_LIVE)) {
                $cloudSkips += 'The live-account S3 Express fidelity run waits on CLOG_S3_EXPRESS_LIVE: set it to an S3 Express One Zone directory bucket name (ending in ''--x-s3'') accessible with the AWS SDK''s default credentials and region.'
            }

            if ([string]::IsNullOrWhiteSpace($env:CLOG_GCS_LIVE)) {
                $cloudSkips += 'The live-account Cloud Storage fidelity run waits on CLOG_GCS_LIVE: set it to a GCS bucket name accessible with Application Default Credentials.'
            }

            $allowedSkipsByProject = @{
                'tests\Orleans.Durable.Storage.Cloud.Tests\Orleans.Durable.Storage.Cloud.Tests.csproj' = $cloudSkips
                'tests\Orleans.Durable.Storage.Interop.Tests\Orleans.Durable.Storage.Interop.Tests.csproj' = $azureOnlySkips
            }
            $script:AllowedSkipped = @()
            $configurations = @('Release', 'Debug')
            foreach ($configuration in $configurations) {
                foreach ($project in $projects) {
                    Invoke-Dotnet @('build', $project, '-c', $configuration, '--no-restore')
                    Invoke-TestProject $project -Configuration $configuration -Timeout '30m' -NoSkips -AllowedSkips $allowedSkipsByProject[$project] -Subdirectory $configuration
                }
            }

            Write-EmulatorRecord $record $configurations $projects
        }
        'EndToEnd' {
            # The Orleans composition's multi-silo scenarios, each checked against its history: the smoke set the default
            # tests run, and the explicit rest, a milestone's end-to-end run (docs/spec/verification.md §12). A skipped
            # scenario fails the run, and so does one which loses its trait.
            $project = 'tests\Orleans.Durable.Storage.Tests\Orleans.Durable.Storage.Tests.csproj'
            Invoke-Dotnet @('build', $project, '-c', 'Release', '--no-restore')
            Invoke-TestProject $project -Configuration 'Release' -Timeout '2h' -Explicit 'on' -Filter @('--filter-trait', 'Category=EndToEnd') -MinimumTests 14 -NoSkips
        }
        'Explore' {
            if (-not $Seeds) {
                throw 'The Explore check takes -Seeds, for example 1..500.'
            }

            $project = 'tests\Orleans.Durable.Storage.Simulation.Tests\Orleans.Durable.Storage.Simulation.Tests.csproj'
            Invoke-Dotnet @('build', $project, '-c', 'SimulationRelease', '--no-restore')
            $env:CLOG_SIM_SEEDS = $Seeds
            Invoke-TestProject $project -Configuration 'SimulationRelease' -Timeout '24h'
        }

        'ExploreSchedules' {
            # The simulated conformance classes under the deep bound: every concurrent window explored, up to two
            # preemptions and 2,000 schedules a window, where the gate explores a stride within one (verification.md §4.1,
            # §12). Milestones and the nightly window run it.
            $project = 'tests\Orleans.Durable.Storage.Simulation.Conformance.Tests\Orleans.Durable.Storage.Simulation.Conformance.Tests.csproj'
            Invoke-Dotnet @('build', $project, '-c', 'SimulationRelease', '--no-restore')
            $env:CLOG_SCHEDULE_BOUND = 'deep'
            Invoke-TestProject $project -Configuration 'SimulationRelease' -Timeout '24h' -Filter @('--filter-class', $Classes)
        }
    }

    Write-Host "$Check completed successfully."
}
finally {
    Pop-Location
}
