<#
.SYNOPSIS
Runs coverage-guided fuzzing campaigns against Durable Storage's decoders (test/Durable.Storage/docs/spec/verification.md §6.1).

.DESCRIPTION
Publishes test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz, instruments the decoders (the Engine's WAL, Ledgers, Archive, and Journals namespaces, and the
file-system and Azure packages and the composition) with the SharpFuzz command-line tool from the repository's tool
manifest, and runs each target under
libfuzzer-dotnet, a libFuzzer driver which passes inputs to the .NET harness through shared memory. Each campaign reads
the committed corpus under test/Durable.Storage/fuzz/corpus/<target> and the seeds the golden vectors give, and writes new inputs, findings,
and its log under artifacts/fuzz/<target>. Each campaign passes libFuzzer a random seed, or -Seed, and reports it, so a
campaign replays with the same seed and corpus. Inputs run up to the longest seed or the target's own bound, whichever
is longer. With -Merge, the committed corpus is replaced by the inputs of itself and the campaign which add coverage
beyond the seeds and the regressions: the seeds stay in the golden vectors, and the regressions/ directory is kept as
it is.

Prerequisites: the pinned SDK (just sync) and the .NET 10 runtime, on which the SharpFuzz command-line tool runs;
network access for the first run's tool restore and driver download; and an x64 Windows or x64 Linux host: the pinned
libfuzzer-dotnet release ships drivers for those platforms only. Elsewhere, build libfuzzer-dotnet.cc with clang
-fsanitize=fuzzer and pass the driver with -LibFuzzer.

A finding stops its campaign and leaves the input in artifacts/fuzz/<target>/findings. Reproduce it with
  dotnet artifacts/fuzz/publish/Orleans.Durable.Storage.Fuzz.dll replay <target> <file>
then commit the input under test/Durable.Storage/fuzz/corpus/<target>/regressions and fix the decoder with a regression test.

.EXAMPLE
./eng/fuzz.ps1 -Target ledgerstore-catalog -Duration 300

.EXAMPLE
./eng/fuzz.ps1 -Target all -Duration 120 -Merge
#>
[CmdletBinding()]
param(
    # Target names, or 'all'. 'dotnet run --project test/Durable.Storage/fuzz/Orleans.Durable.Storage.Fuzz -- list' lists them.
    [Parameter(Mandatory)]
    [string[]] $Target,

    # The seconds each campaign runs (libFuzzer's -max_total_time).
    [ValidateRange(1, [int]::MaxValue)]
    [int] $Duration = 60,

    # The seconds one input may run before libFuzzer reports it as a hang (-timeout).
    [ValidateRange(1, 3600)]
    [int] $Timeout = 10,

    # Minimize the committed corpus with the campaign's new inputs.
    [switch] $Merge,

    # libFuzzer's random seed (-seed); a random one when omitted.
    [ValidateRange(1, [int]::MaxValue)]
    [int] $Seed = (Get-Random -Minimum 1 -Maximum ([int]::MaxValue)),

    # A libfuzzer-dotnet driver to use instead of the pinned release.
    [string] $LibFuzzer
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
Set-StrictMode -Version Latest

$root = Split-Path $PSScriptRoot -Parent
. (Join-Path $PSScriptRoot 'dotnet-env.ps1')
$output = Join-Path $root 'artifacts\fuzz'
$publish = Join-Path $output 'publish'
$corpusRoot = Join-Path $root 'fuzz\corpus'

function Invoke-Checked {
    param([string] $Command, [string[]] $Arguments)
    & $Command @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
    }
}

# Whether the assembly at $Path references the assembly named $Reference. The image is read into memory, so the file
# stays free for the tools which rewrite it.
function Test-References {
    param([string] $Path, [string] $Reference)
    $image = [System.Reflection.PortableExecutable.PEReader]::new([System.IO.MemoryStream]::new([System.IO.File]::ReadAllBytes($Path)))
    try {
        $metadata = [System.Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($image)
        foreach ($handle in $metadata.AssemblyReferences) {
            if ($metadata.GetString($metadata.GetAssemblyReference($handle).Name) -eq $Reference) {
                return $true
            }
        }

        return $false
    }
    finally {
        $image.Dispose()
    }
}

# Runs the driver from the published harness's directory, since libfuzzer-dotnet starts the harness by the path it is
# given, unquoted on Windows. Returns the driver's exit code.
function Invoke-Driver {
    param([string[]] $Arguments, [string] $Log)
    Push-Location -LiteralPath $publish
    try {
        & $LibFuzzer @Arguments 2>&1 | Tee-Object -FilePath $Log | Out-Host
        return $LASTEXITCODE
    }
    finally {
        Pop-Location
    }
}

# The pinned libfuzzer-dotnet release (https://github.com/Metalnem/libfuzzer-dotnet) and each driver's SHA-256.
$release = 'v2025.05.02.0904'
$drivers = @{
    'libfuzzer-dotnet-windows.exe' = '17AF5B3F6FF4D2C57B44B9A35C13051B570EB66F0557D00015DF3832709050BF'
    'libfuzzer-dotnet-ubuntu' = 'C2C2A90D94C409A4AF339A0D4F244E0442C5A5D249BE0F1252BA07871F285958'
    'libfuzzer-dotnet-debian' = 'EFD77B0E4AF48CDC75B8ACC0C2CE8B8C6BEE51521CD5566B4839A7C32832EB4F'
}

if (-not $LibFuzzer) {
    if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -ne 'X64') {
        throw 'The pinned libfuzzer-dotnet drivers are x64; pass -LibFuzzer with a driver built for this host.'
    }

    $asset = if ($IsWindows) {
        'libfuzzer-dotnet-windows.exe'
    }
    elseif ($IsLinux) {
        $osRelease = if (Test-Path -LiteralPath '/etc/os-release') { Get-Content -LiteralPath '/etc/os-release' -Raw } else { '' }
        if ($osRelease -match '(?m)^ID=debian$') { 'libfuzzer-dotnet-debian' } else { 'libfuzzer-dotnet-ubuntu' }
    }
    else {
        throw 'libfuzzer-dotnet ships drivers for Windows and Linux only; pass -LibFuzzer with a driver built for this host.'
    }

    $tools = Join-Path $root "artifacts\tools\libfuzzer-dotnet\$release"
    $null = New-Item -ItemType Directory -Force $tools
    $LibFuzzer = Join-Path $tools $asset
    if (-not (Test-Path -LiteralPath $LibFuzzer)) {
        $download = "$LibFuzzer.download"
        Invoke-WebRequest -Uri "https://github.com/Metalnem/libfuzzer-dotnet/releases/download/$release/$asset" -OutFile $download
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $drivers[$asset]) {
            Remove-Item -LiteralPath $download
            throw "The downloaded $asset does not match the pinned SHA-256."
        }

        Move-Item -LiteralPath $download -Destination $LibFuzzer
    }

    if ((Get-FileHash -LiteralPath $LibFuzzer -Algorithm SHA256).Hash -ne $drivers[$asset]) {
        throw "$LibFuzzer is not the pinned libfuzzer-dotnet $release driver."
    }

    if (-not $IsWindows) {
        & chmod +x $LibFuzzer
    }
}

$LibFuzzer = (Resolve-Path -LiteralPath $LibFuzzer).Path

# A fresh publish: SharpFuzz instruments each assembly once, in place.
if (Test-Path -LiteralPath $publish) {
    Remove-Item -LiteralPath $publish -Recurse -Force
}

Push-Location $root
try {
    Invoke-Checked 'dotnet' @('tool', 'restore')
    Invoke-Checked 'dotnet' @('publish', 'fuzz/Orleans.Durable.Storage.Fuzz/Orleans.Durable.Storage.Fuzz.csproj', '-c', 'Release', '-o', $publish)
    # The assemblies of the decoders the targets fuzz, each with the namespaces SharpFuzz instruments in it: the Engine's
    # decoding layers, and the whole of the file-system and Azure packages and of the composition.
    $instrumented = [ordered]@{
        'Orleans.Durable.Storage.Engine.dll' = @('Orleans.Durable.Storage.Engine.Wal', 'Orleans.Durable.Storage.Engine.Ledgers', 'Orleans.Durable.Storage.Engine.Archive', 'Orleans.Durable.Storage.Engine.Journals')
        'Orleans.Durable.Storage.FileSystem.dll' = @()
        'Orleans.Durable.Storage.AzureStorage.dll' = @()
        'Orleans.Durable.Storage.dll' = @()
        'Orleans.Durable.dll' = @('Orleans.Durable.Jobs')
    }
    foreach ($assembly in $instrumented.Keys) {
        $path = Join-Path $publish $assembly
        Invoke-Checked 'dotnet' (@('tool', 'run', 'sharpfuzz', $path) + $instrumented[$assembly])

        # SharpFuzz exits successfully when it cannot instrument a file; an instrumented assembly calls SharpFuzz.Common.
        if (-not (Test-References $path 'SharpFuzz.Common')) {
            throw "SharpFuzz did not instrument $path."
        }
    }
}
finally {
    Pop-Location
}

$harnessName = if ($IsWindows) { 'Orleans.Durable.Storage.Fuzz.exe' } else { 'Orleans.Durable.Storage.Fuzz' }
$targets = [ordered]@{}
$listing = & (Join-Path $publish $harnessName) list
if ($LASTEXITCODE -ne 0) {
    throw "Listing the harness's targets failed with exit code $LASTEXITCODE."
}

foreach ($line in $listing) {
    $name, $maxLength = $line -split "`t"
    $targets[$name] = [int] $maxLength
}

$selected = if ($Target -contains 'all') { @($targets.Keys) } else { $Target }
foreach ($name in $selected) {
    if (-not $targets.Contains($name)) {
        throw "There is no target '$name'. The targets are $($targets.Keys -join ', ')."
    }
}

$results = [System.Collections.Generic.List[object]]::new()
foreach ($name in $selected) {
    $work = Join-Path $output $name
    $newInputs = Join-Path $work 'corpus'
    $seeds = Join-Path $work 'seeds'
    $findings = Join-Path $work 'findings'
    $committed = Join-Path $corpusRoot $name
    foreach ($directory in @($seeds, $findings)) {
        if (Test-Path -LiteralPath $directory) {
            Remove-Item -LiteralPath $directory -Recurse -Force
        }
    }

    $null = New-Item -ItemType Directory -Force $newInputs, $findings, $committed
    Invoke-Checked (Join-Path $publish $harnessName) @('seed', $name, $seeds)

    # libFuzzer writes new inputs to the first corpus directory.
    $driverArguments = @(
        "-max_len=$($targets[$name])",
        "-timeout=$Timeout",
        "-seed=$Seed",
        "-artifact_prefix=$findings$([System.IO.Path]::DirectorySeparatorChar)",
        "--target_path=.$([System.IO.Path]::DirectorySeparatorChar)$harnessName",
        "--target_arg=$name"
    )
    $log = Join-Path $work 'campaign.log'
    Write-Host "Fuzzing $name for $Duration seconds with seed $Seed."
    $started = [System.Diagnostics.Stopwatch]::StartNew()
    $exitCode = Invoke-Driver ($driverArguments + @("-max_total_time=$Duration", '-print_final_stats=1', $newInputs, $committed, $seeds)) $log
    $elapsed = $started.Elapsed

    $text = Get-Content -LiteralPath $log -Raw
    $stat = {
        param([string] $Statistic)
        $match = [regex]::Match($text, "(?m)^stat::$Statistic`:\s+(\d+)")
        if ($match.Success) { [long] $match.Groups[1].Value } else { $null }
    }
    $found = @(Get-ChildItem -LiteralPath $findings -File)
    $results.Add([pscustomobject]@{
        Target = $name
        Seed = $Seed
        Seconds = [math]::Round($elapsed.TotalSeconds)
        Executions = & $stat 'number_of_executed_units'
        NewInputs = & $stat 'new_units_added'
        Findings = $found.Count
        ExitCode = $exitCode
    })

    if ($found.Count -ne 0) {
        $results | Format-Table -AutoSize | Out-Host
        throw "$name broke its oracle; the input is in $findings and the log is $log."
    }

    if ($exitCode -ne 0) {
        $results | Format-Table -AutoSize | Out-Host
        throw "The $name campaign failed with exit code $exitCode; see $log."
    }

    if ($Merge) {
        $merged = Join-Path $work 'merged'
        if (Test-Path -LiteralPath $merged) {
            Remove-Item -LiteralPath $merged -Recurse -Force
        }

        # libFuzzer adds to the first directory only the inputs which reach coverage it lacks, so starting it with the
        # seeds and the regressions keeps their copies, and the inputs they already cover, out of the committed corpus.
        $null = New-Item -ItemType Directory -Force $merged
        $known = @(Get-ChildItem -LiteralPath $seeds -File)
        $regressions = Join-Path $committed 'regressions'
        if (Test-Path -LiteralPath $regressions) {
            $known += @(Get-ChildItem -LiteralPath $regressions -File)
        }

        foreach ($file in $known) {
            Copy-Item -LiteralPath $file.FullName -Destination (Join-Path $merged "known-$($file.Name)")
        }

        $mergeLog = Join-Path $work 'merge.log'
        $top = Join-Path $work 'committed'
        if (Test-Path -LiteralPath $top) {
            Remove-Item -LiteralPath $top -Recurse -Force
        }

        # libFuzzer reads directories recursively, so the committed corpus enters without its regressions.
        $null = New-Item -ItemType Directory -Force $top
        Get-ChildItem -LiteralPath $committed -File | Copy-Item -Destination $top
        if ((Invoke-Driver ($driverArguments + @('-merge=1', $merged, $top, $newInputs)) $mergeLog) -ne 0) {
            throw "Merging the $name corpus failed; see $mergeLog."
        }

        Get-ChildItem -LiteralPath $merged -File -Filter 'known-*' | Remove-Item
        Get-ChildItem -LiteralPath $committed -File | Remove-Item
        Get-ChildItem -LiteralPath $merged -File | Move-Item -Destination $committed
        Write-Host "The $name corpus holds $(@(Get-ChildItem -LiteralPath $committed -File).Count) inputs."
    }
}

$results | Format-Table -AutoSize | Out-Host

# A campaign over uninstrumented decoders sees no coverage and adds no input, so campaigns which all add none are
# reported.
if (@($results | Where-Object { $_.NewInputs -gt 0 }).Count -eq 0) {
    Write-Warning 'No campaign added an input; check that SharpFuzz instrumented the decoders.'
}
