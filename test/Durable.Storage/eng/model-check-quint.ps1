[CmdletBinding()]
param(
    # The cases to run: Quick runs small positive cases, every mutant, and every witness; Default adds the standard
    # larger bounds and liveness checks; Extended adds extended bounds.
    [ValidateSet('Quick', 'Default', 'Extended')] [string] $Tier = 'Default',
    [switch] $Extended,
    # Runs the cases measured in hours, which only the scheduled workflow runs, and which check-full leaves out.
    [switch] $Nightly,
    # Runs the selected models only when their spec directories or the Quint runner changed since this ref.
    [string] $Since,
    [ValidateRange(1, 64)] [int] $Workers = 2,
    [string[]] $Model = @('all'),
    # Runs only cases whose names match this wildcard pattern.
    [string] $Only = '*',
    # Replays each trace module in this directory against its model with quint test, and checks that every trace mutant
    # is rejected at the step it changes, instead of model checking (test/Durable.Storage/docs/spec/verification.md §5.1).
    [string] $Traces
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
if ($Extended) {
    $Tier = 'Extended'
}
elseif ($Tier -eq 'Extended') {
    $Extended = $true
}

$root = Split-Path $PSScriptRoot -Parent
# Each model lives beside the spec it models: ledger protocol, ledgers (map and drop), LSM, and archive logs.
$ledgerProtocol = Join-Path $root 'specs' 'ledger-protocol'
$ledgers = Join-Path $root 'specs' 'ledgers'
$specs = @{
    QuorumCoverageSealing = $ledgerProtocol; AtomicLedger = $ledgerProtocol; LedgerProtocol = $ledgerProtocol
    ObjectLedger = $ledgerProtocol; LedgerDrop = $ledgers; LedgerMap = $ledgers; LedgerLease = $ledgers
    Archive = (Join-Path $root 'specs' 'archive'); ArchiveLogs = (Join-Path $root 'specs' 'archive-logs')
}
$Model = @($Model | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
foreach ($m in $Model) {
    if ($m -ne 'all' -and -not $specs.ContainsKey($m)) {
        throw "Unknown model '$m'. Valid models: all, $($specs.Keys -join ', ')."
    }
}
$selectedModels = if ($Model -contains 'all') { @($specs.Keys) } else { $Model }
if ($Since -and -not $Traces) {
    # git names paths from the repository's root, which holds test/Durable.Storage.
    $repository = Split-Path (Split-Path $root -Parent) -Parent
    $base = git -C $root merge-base $Since HEAD
    if ($LASTEXITCODE -ne 0) {
        throw "No merge base with $Since."
    }

    $specPrefixes = @($selectedModels | ForEach-Object {
        [System.IO.Path]::GetRelativePath($repository, $specs[$_]).Replace('\', '/') + '/'
    } | Select-Object -Unique)
    $changed = @(git -C $root diff --name-only $base) + @(git -C $repository ls-files --others --exclude-standard)
    $relevant = $changed | Where-Object {
        $path = $_
        $path -eq 'test/Durable.Storage/eng/model-check-quint.ps1' -or $path -like 'test/Durable.Storage/eng/quint/*' -or
            ($specPrefixes | Where-Object { $path.StartsWith($_) })
    }
    if (-not $relevant) {
        Write-Host "Neither $($specPrefixes -join ', ') nor test/Durable.Storage/eng/model-check-quint.ps1 changed since $Since; the selected models are unchanged."
        exit 0
    }
}
$output = Join-Path $root 'artifacts' 'model-checks' 'quint'
$tools = Join-Path $root 'artifacts' 'tools' 'quint'
$null = New-Item -ItemType Directory -Force $output, $tools
$null = Get-Command java, node, npm -ErrorAction Stop

# Quint is pinned by test/Durable.Storage/eng/quint/package-lock.json and installed outside the source tree.
$lock = Join-Path $PSScriptRoot 'quint' 'package-lock.json'
$stamp = Join-Path $tools '.lock-sha256'
$lockHash = (Get-FileHash -LiteralPath $lock -Algorithm SHA256).Hash
if (-not (Test-Path -LiteralPath $stamp) -or (Get-Content -LiteralPath $stamp -Raw).Trim() -ne $lockHash) {
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'quint' 'package.json'), $lock -Destination $tools -Force
    Push-Location $tools
    try {
        & npm ci --no-audit --no-fund --loglevel=error
        if ($LASTEXITCODE -ne 0) {
            throw 'npm ci failed for the pinned Quint toolchain.'
        }
    }
    finally {
        Pop-Location
    }

    Set-Content -LiteralPath $stamp -Value $lockHash
}

$quint = Join-Path $tools 'node_modules' '@informalsystems' 'quint' 'dist' 'src' 'cli.js'

# Trace validation runs each trace's replay on Quint's own simulator, whose TypeScript backend takes a run of hundreds of
# actions, which nest past the Rust backend's recursion limit (test/Durable.Storage/docs/spec/verification.md §5.1). An archive-log trace
# (ArchiveLogsTrace*) must replay, and a copy which repeats its first drainStart, while its drainer is in that segment,
# must fail, so a replay which accepted any trace would not pass. A ledger map trace (LedgerMapTrace*) and an LSM trace
# (ArchiveTrace*) must replay, and each of their models' trace mutants must be rejected at the step it changes.
$ledgerTraceRoots = @()
if ($Traces) {
    $ledgerTraceRoots = @(Get-ChildItem -LiteralPath $Traces -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'LedgerTraceData.qnt') } |
        Where-Object { $_.Name -like $Only } | Sort-Object Name)
}

if ($Traces -and $ledgerTraceRoots.Count -gt 0) {
    $traceOutput = Join-Path $root 'artifacts' 'model-checks' 'ledger-traces'
    Remove-Item -LiteralPath $traceOutput -Recurse -Force -ErrorAction SilentlyContinue
    $null = New-Item -ItemType Directory -Force $traceOutput
    $mutantDirectory = Join-Path $Traces 'mutants'
    Remove-Item -LiteralPath $mutantDirectory -Recurse -Force -ErrorAction SilentlyContinue
    $null = New-Item -ItemType Directory -Force $mutantDirectory

    $traceResults = [System.Collections.Generic.List[object]]::new()
    $validatedTraces = [System.Collections.Generic.List[object]]::new()

    function Invoke-LedgerReplay([string] $Path, [string] $Module) {
        $lines = & node $quint test $Path --main=$Module --match '^traceReplay$' --backend typescript --max-samples=1 2>&1 | ForEach-Object { "$_" }
        [pscustomobject]@{ Passed = $LASTEXITCODE -eq 0; Output = $lines -join "`n" }
    }

    function Parse-LedgerStep([string] $Line) {
        if ($Line -notmatch '^\s+\.then\((\w+)\((.*)\)\)\s*$') {
            return $null
        }

        $name = $Matches[1]
        $raw = $Matches[2]
        $args = [System.Collections.Generic.List[string]]::new()
        if ($raw.Length -gt 0) {
            $depth = 0
            $inStr = $false
            $start = 0
            for ($k = 0; $k -lt $raw.Length; $k++) {
                $ch = $raw[$k]
                if ($ch -eq '"') {
                    $inStr = -not $inStr
                }
                elseif (-not $inStr) {
                    if ($ch -eq '(' -or $ch -eq '[') { $depth++ }
                    elseif ($ch -eq ')' -or $ch -eq ']') { $depth-- }
                    elseif ($ch -eq ',' -and $depth -eq 0) {
                        $args.Add($raw.Substring($start, $k - $start).Trim())
                        $start = $k + 1
                    }
                }
            }

            $args.Add($raw.Substring($start).Trim())
        }

        [pscustomobject]@{ Name = $name; Args = [string[]] $args.ToArray() }
    }

    function Format-LedgerStep([string] $Name, [string[]] $StepArgs) {
        "    .then($Name($($StepArgs -join ', ')))"
    }

    function Set-LedgerStep([string[]] $Lines, [int] $At, [string] $Name, [string[]] $StepArgs) {
        $copy = [string[]] $Lines.Clone()
        $copy[$At] = Format-LedgerStep $Name $StepArgs
        @{ Lines = $copy; At = $At }
    }

    function Get-LedgerIntSet([string] $Rendered) {
        $inner = ($Rendered -replace '^Set\(', '') -replace '\)$', ''
        @($inner.Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { [long] $_.Trim() })
    }

    function Format-LedgerIntSet([long[]] $Members) {
        'Set(' + (($Members | Sort-Object) -join ', ') + ')'
    }

    function Get-LedgerIntList([string] $Rendered) {
        $inner = ($Rendered -replace '^\[', '') -replace '\]$', ''
        @($inner.Split(',', [StringSplitOptions]::RemoveEmptyEntries) | ForEach-Object { [long] $_.Trim() })
    }

    function Format-LedgerIntList([long[]] $Members) {
        '[' + (($Members | Sort-Object) -join ', ') + ']'
    }

    function Write-LedgerTruncated([string] $Module, [string[]] $Lines, [int] $Through) {
        $text = ((@($Lines[0..$Through]) + '}') -join "`n") -replace '(?m)^module \w+ \{', "module $Module {"
        $path = Join-Path $mutantDirectory "$Module.qnt"
        Set-Content -LiteralPath $path -Value $text -Encoding utf8 -NoNewline
        $path
    }

    $failures = [System.Collections.Generic.List[string]]::new()
    $traceWorkers = [Math]::Max($Workers, [Math]::Min(8, [Environment]::ProcessorCount))
    $traceChecks = @($ledgerTraceRoots | ForEach-Object -Parallel {
        $name = $_.Name
        $path = Join-Path $_.FullName 'LedgerTraceData.qnt'
        $lines = [string[]] @(Get-Content -LiteralPath $path)
        $steps = @($lines | Where-Object { $_ -match '^\s+\.then\(' }).Count
        $clock = [System.Diagnostics.Stopwatch]::StartNew()
        $out = & node $using:quint test $path --main=LedgerTraceData --match '^traceReplay$' --backend typescript --max-samples=1 2>&1 | ForEach-Object { "$_" }
        $passed = $LASTEXITCODE -eq 0
        $seconds = [Math]::Round($clock.Elapsed.TotalSeconds)
        $outputText = $out -join "`n"
        $directory = Join-Path $using:traceOutput $name
        $null = New-Item -ItemType Directory -Force $directory
        Set-Content -LiteralPath (Join-Path $directory 'quint.log') -Value $outputText -Encoding utf8
        [pscustomobject]@{
            Name = $name
            Passed = $passed
            Steps = $steps
            Seconds = $seconds
            Lines = $lines
            Text = ($lines -join "`n")
            Output = $outputText
        }
    } -ThrottleLimit $traceWorkers)

    foreach ($check in ($traceChecks | Sort-Object Name)) {
        if (-not $check.Passed) {
            $msg = "$($check.Name): trace does not replay against LedgerProtocolTrace.qnt.`n$($check.Output)"
            Write-Host "$($check.Name): trace does not replay against LedgerProtocolTrace.qnt."
            $failures.Add($msg)
        }
        else {
            $validatedTraces.Add([pscustomobject]@{ Name = $check.Name; Steps = $check.Steps; Lines = $check.Lines; Text = $check.Text })
            $traceResults.Add([pscustomobject]@{
                Name = $check.Name
                Outcome = 'Validated'
                Events = $check.Steps
                Seconds = $check.Seconds
            })
            Write-Host "$($check.Name): Validated ($($check.Steps) steps, $($check.Seconds) s)"
        }
    }

    # The 19 trace mutants, each a change the ledger protocol's guarantees forbid at the step it changes.
    $ledgerTraceMutants = [ordered]@{
        'mutant-recovered-end' = {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if ($s -and $s.Name -eq 'stepRecovered') {
                    $a = [string[]] $s.Args.Clone()
                    $a[2] = ([long] $a[2] + 1).ToString()
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-close-at-another-end' = {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if ($s -and $s.Name -eq 'stepClose') {
                    $a = [string[]] $s.Args.Clone()
                    $a[1] = ([long] $a[1] + 1).ToString()
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-written-as-sealed' = {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if ($s -and $s.Name -eq 'stepServeWrite' -and $s.Args[3] -eq '"Written"') {
                    $a = [string[]] $s.Args.Clone()
                    $a[3] = '"Sealed"'
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-revoked-data' = {
            param([string[]] $lines)
            $answered = @{}
            $revocable = @{}
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if (-not $s) { continue }
                if ($s.Name -eq 'stepRestart') {
                    $revocable[$s.Args[0]] = @($answered[$s.Args[0]])
                }
                elseif ($s.Name -eq 'stepServeRead') {
                    $r = $s.Args[1]; $p = $s.Args[2]; $ans = $s.Args[4]
                    if ($ans -eq '"Batch"' -and $revocable[$r] -contains $p) {
                        $a = [string[]] $s.Args.Clone(); $a[4] = '"Missing"'
                        return Set-LedgerStep $lines $i $s.Name $a
                    }

                    if ($ans -eq '"Batch"') { $answered[$r] = @($answered[$r]) + $p }
                }
                elseif ($s.Name -eq 'stepServePeek') {
                    $r = $s.Args[0]; $p = $s.Args[1]; $ans = $s.Args[3]
                    if ($ans -eq '"Batch"' -and $revocable[$r] -contains $p) {
                        $a = [string[]] $s.Args.Clone(); $a[3] = '"Missing"'
                        return Set-LedgerStep $lines $i $s.Name $a
                    }

                    if ($ans -eq '"Batch"') { $answered[$r] = @($answered[$r]) + $p }
                }
                elseif ($s.Name -eq 'stepServeWrite' -and $s.Args[3] -eq '"Written"') {
                    $answered[$s.Args[0]] = @($answered[$s.Args[0]]) + $s.Args[1]
                }
                elseif ($s.Name -eq 'stepServeCopy' -and $s.Args[5] -eq '"Written"') {
                    $answered[$s.Args[2]] = @($answered[$s.Args[2]]) + $s.Args[3]
                }
            }
        }
        'mutant-revoked-seal' = {
            param([string[]] $lines)
            $sealedAtRestart = @{}
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if (-not $s) { continue }
                if ($s.Name -eq 'stepRestart' -and $s.Args[2] -eq 'true') {
                    $sealedAtRestart[$s.Args[0]] = $true
                }
                elseif ($s.Name -eq 'stepServeWrite' -and $sealedAtRestart[$s.Args[0]] -and $s.Args[3] -eq '"Sealed"') {
                    $a = [string[]] $s.Args.Clone(); $a[3] = '"Written"'
                    return Set-LedgerStep $lines $i $s.Name $a
                }
                elseif ($s.Name -eq 'stepServeCheck' -and $sealedAtRestart[$s.Args[1]] -and $s.Args[4] -eq 'true') {
                    $a = [string[]] $s.Args.Clone(); $a[4] = 'false'
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-early-mark' = {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if ($s -and $s.Name -eq 'stepReceiveRead' -and $s.Args[4] -eq '"Missing"' -and $s.Args[3] -eq 'false') {
                    $a = [string[]] $s.Args.Clone(); $a[3] = 'true'
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-raised-tail' = {
            param([string[]] $lines)
            $highest = 0L
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if (-not $s) { continue }
                $vals = switch ($s.Name) {
                    'stepAcquire' { @($s.Args[1]) }
                    'stepCallWrite' { @($s.Args[2]) }
                    'stepCallCopy' { @($s.Args[4]) }
                    'stepTail' { @($s.Args[0]) }
                    'stepServeWrite' { @($s.Args[2]) }
                    'stepServeSeal' { @($s.Args[3]) }
                    'stepServeCopy' { @($s.Args[4]) }
                    'stepServeCheck' { @($s.Args[3]) }
                    'stepServePeek' { @($s.Args[2]) }
                    'stepServeTail' { @($s.Args[1]) }
                    'stepServeLearn' { @($s.Args[2], $s.Args[4]) }
                    'stepReceiveSeal' { @($s.Args[3]) }
                    'stepRecovered' { @($s.Args[1]) }
                    'stepCheck' { @($s.Args[1]) }
                    'stepChecked' { @($s.Args[1]) }
                    default { @() }
                }

                foreach ($v in $vals) {
                    $n = [long] $v
                    if ($n -gt $highest) { $highest = $n }
                }
            }

            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if ($s -and $s.Name -eq 'stepServeSeal' -and $s.Args[2] -eq '"Tail"') {
                    $a = [string[]] $s.Args.Clone(); $a[3] = ($highest + 1).ToString()
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-eager-explicit-tail' = {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if ($s -and $s.Name -eq 'stepTail') {
                    $a = [string[]] $s.Args.Clone(); $a[0] = ([long] $a[0] + 1).ToString()
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-eager-repair-tail' = {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if ($s -and $s.Name -eq 'stepCallCopy' -and $s.Args[0] -eq 'true') {
                    $a = [string[]] $s.Args.Clone(); $a[4] = ([long] $a[4] + 1).ToString()
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-eager-learn' = {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if ($s -and $s.Name -eq 'stepServeLearn' -and $s.Args[3] -eq '"Tail"') {
                    $a = [string[]] $s.Args.Clone(); $a[4] = ([long] $a[4] + 1).ToString()
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-restart-drops-data' = {
            param([string[]] $lines)
            $intact = @{}
            $damaged = @{}
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if (-not $s) { continue }
                if ($s.Name -in 'stepServeWrite', 'stepServeCopy', 'stepServeRead', 'stepServePeek') {
                    $r, $p, $ans = switch ($s.Name) {
                        'stepServeWrite' { $s.Args[0], [long] $s.Args[1], $s.Args[3] }
                        'stepServeCopy' { $s.Args[2], [long] $s.Args[3], $s.Args[5] }
                        'stepServeRead' { $s.Args[1], [long] $s.Args[2], $s.Args[4] }
                        'stepServePeek' { $s.Args[0], [long] $s.Args[1], $s.Args[3] }
                    }

                    if ($ans -eq '"LedgerReplicaCorruptException"') {
                        $damaged[$r] = @($damaged[$r]) + $p
                    }
                    elseif (($s.Name -in 'stepServeWrite', 'stepServeCopy' -and $ans -eq '"Written"') -or
                            ($s.Name -in 'stepServeRead', 'stepServePeek' -and $ans -eq '"Batch"')) {
                        $intact[$r] = @($intact[$r]) + $p
                    }
                }
                elseif ($s.Name -eq 'stepRestart' -and $s.Args[1] -eq 'true') {
                    $r = $s.Args[0]
                    $data = Get-LedgerIntSet $s.Args[3]
                    $damaged[$r] = @($damaged[$r]) + (Get-LedgerIntList $s.Args[4])
                    $dropped = $data | Where-Object { $intact[$r] -contains $_ -and $damaged[$r] -notcontains $_ } | Select-Object -First 1
                    if ($null -ne $dropped) {
                        $a = [string[]] $s.Args.Clone()
                        $a[3] = Format-LedgerIntSet @($data | Where-Object { $_ -ne $dropped })
                        return Set-LedgerStep $lines $i $s.Name $a
                    }
                }
            }
        }
        'mutant-restart-adds-data' = {
            param([string[]] $lines)
            $written = [System.Collections.Generic.HashSet[long]]::new()
            foreach ($line in $lines) {
                $s = Parse-LedgerStep $line
                if (-not $s) { continue }
                if ($s.Name -eq 'stepAcquire') { $null = $written.Add([long] $s.Args[0]) }
                elseif ($s.Name -eq 'stepCallWrite') { $null = $written.Add([long] $s.Args[1]) }
                elseif ($s.Name -eq 'stepCallCopy') { $null = $written.Add([long] $s.Args[3]) }
            }

            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if ($s -and $s.Name -eq 'stepRestart' -and $s.Args[1] -eq 'true') {
                    $data = Get-LedgerIntSet $s.Args[3]
                    $unwritten = 0L
                    while ($written.Contains($unwritten)) { $unwritten++ }
                    $a = [string[]] $s.Args.Clone()
                    $a[3] = Format-LedgerIntSet @($data + $unwritten)
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-restart-damage-as-missing' = {
            param([string[]] $lines)
            $damaged = @{}
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if (-not $s) { continue }
                if ($s.Name -in 'stepServeWrite', 'stepServeCopy', 'stepServeRead', 'stepServePeek') {
                    $r, $p, $ans = switch ($s.Name) {
                        'stepServeWrite' { $s.Args[0], [long] $s.Args[1], $s.Args[3] }
                        'stepServeCopy' { $s.Args[2], [long] $s.Args[3], $s.Args[5] }
                        'stepServeRead' { $s.Args[1], [long] $s.Args[2], $s.Args[4] }
                        'stepServePeek' { $s.Args[0], [long] $s.Args[1], $s.Args[3] }
                    }

                    if ($ans -eq '"LedgerReplicaCorruptException"') {
                        $damaged[$r] = @($damaged[$r]) + $p
                    }
                    elseif ($s.Name -in 'stepServeWrite', 'stepServeCopy' -and $ans -eq '"Written"') {
                        $damaged[$r] = @($damaged[$r] | Where-Object { $_ -ne $p })
                    }
                }
                elseif ($s.Name -eq 'stepRestart' -and $s.Args[1] -eq 'true') {
                    $r = $s.Args[0]
                    $data = Get-LedgerIntSet $s.Args[3]
                    $faulty = Get-LedgerIntList $s.Args[4]
                    $lost = @($data) + @($faulty) | Where-Object { $damaged[$r] -contains $_ } | Select-Object -First 1
                    if ($null -ne $lost) {
                        $a = [string[]] $s.Args.Clone()
                        $a[3] = Format-LedgerIntSet @($data | Where-Object { $_ -ne $lost })
                        $a[4] = Format-LedgerIntList @($faulty | Where-Object { $_ -ne $lost })
                        return Set-LedgerStep $lines $i $s.Name $a
                    }
                }
            }
        }
        'mutant-restart-unseals' = {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if ($s -and $s.Name -eq 'stepRestart' -and $s.Args[2] -eq 'true' -and $s.Args[7] -eq '[]') {
                    $a = [string[]] $s.Args.Clone(); $a[2] = 'false'
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-restart-seals' = {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if ($s -and $s.Name -eq 'stepRestart' -and $s.Args[1] -eq 'true' -and $s.Args[2] -eq 'false') {
                    $a = [string[]] $s.Args.Clone(); $a[2] = 'true'
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-sealed-without-seal' = {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if ($s -and $s.Name -like 'stepServe*' -and $s.Args[$s.Args.Count - 1] -ne '[]') {
                    $a = [string[]] $s.Args.Clone(); $a[$a.Count - 1] = '[]'
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-data-without-write' = {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if ($s -and $s.Name -like 'stepServe*' -and ($s.Args[$s.Args.Count - 3] -ne '[]' -or $s.Args[$s.Args.Count - 2] -ne '[]')) {
                    $a = [string[]] $s.Args.Clone()
                    $a[$a.Count - 3] = '[]'
                    $a[$a.Count - 2] = '[]'
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-corrupt-quorum' = {
            param([string[]] $lines, [string] $text)
            $quorum = [int] [regex]::Match($text, '(?m)^\s*TraceQuorum = (\d+)').Groups[1].Value
            if ($quorum -lt 2) { return }

            $corrupt = [System.Collections.Generic.List[string]]::new()
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if (-not $s -or $s.Name -notlike 'stepServe*') { continue }
                $r, $ans, $ansIndex = switch ($s.Name) {
                    'stepServeWrite' { $s.Args[0], $s.Args[3], 3 }
                    'stepServeSeal' { $s.Args[1], $s.Args[2], 2 }
                    'stepServeRead' { $s.Args[1], $s.Args[4], 4 }
                    'stepServeCopy' { $s.Args[2], $s.Args[5], 5 }
                    'stepServeCheck' { $s.Args[1], $s.Args[2], 2 }
                    'stepServePeek' { $s.Args[0], $s.Args[3], 3 }
                    'stepServeLearn' { $s.Args[1], $s.Args[3], 3 }
                    default { $null, $null, -1 }
                }

                if ($ans -eq '"LedgerReplicaCorruptException"' -and -not $corrupt.Contains($r)) {
                    $corrupt.Add($r)
                }
                elseif ($corrupt.Count -ge $quorum - 1 -and -not $corrupt.Contains($r) -and $s.Name -in 'stepServeRead', 'stepServePeek' -and $ans -eq '"Batch"') {
                    $a = [string[]] $s.Args.Clone(); $a[$ansIndex] = '"LedgerReplicaCorruptException"'
                    return Set-LedgerStep $lines $i $s.Name $a
                }
            }
        }
        'mutant-corrupt-from-undamaged' = {
            param([string[]] $lines, [string] $text)
            if ($text -notmatch '(?m)^\s*TraceFaults = Set\("corrupt"\)') { return }

            $damaged = [System.Collections.Generic.HashSet[string]]::new()
            for ($i = 0; $i -lt $lines.Count; $i++) {
                $s = Parse-LedgerStep $lines[$i]
                if (-not $s) { continue }
                if ($s.Name -eq 'stepDamage') {
                    $null = $damaged.Add($s.Args[0])
                }
                elseif ($s.Name -eq 'stepRestart' -and (Get-LedgerIntList $s.Args[4]).Count -gt 0) {
                    return
                }
                elseif ($s.Name -like 'stepServe*') {
                    $r, $ans, $ansIndex = switch ($s.Name) {
                        'stepServeWrite' { $s.Args[0], $s.Args[3], 3 }
                        'stepServeSeal' { $s.Args[1], $s.Args[2], 2 }
                        'stepServeRead' { $s.Args[1], $s.Args[4], 4 }
                        'stepServeCopy' { $s.Args[2], $s.Args[5], 5 }
                        'stepServeCheck' { $s.Args[1], $s.Args[2], 2 }
                        'stepServePeek' { $s.Args[0], $s.Args[3], 3 }
                        'stepServeLearn' { $s.Args[1], $s.Args[3], 3 }
                        default { $null, $null, -1 }
                    }

                    if ($ans -eq '"LedgerReplicaCorruptException"') { return }
                    if (-not $damaged.Contains($r) -and $s.Name -in 'stepServeRead', 'stepServePeek' -and $ans -eq '"Batch"') {
                        $a = [string[]] $s.Args.Clone(); $a[$ansIndex] = '"LedgerReplicaCorruptException"'
                        return Set-LedgerStep $lines $i $s.Name $a
                    }
                }
            }
        }
    }

    # Select the earliest matching step across validated traces for each mutant, then check all mutants in parallel.
    $mutantJobs = [System.Collections.Generic.List[object]]::new()
    foreach ($mutant in $ledgerTraceMutants.GetEnumerator()) {
        $best = $null
        $bestTrace = $null
        $bestStep = [int]::MaxValue
        foreach ($trace in $validatedTraces) {
            $candidate = & $mutant.Value $trace.Lines $trace.Text
            if ($candidate) {
                $run = [Array]::FindIndex($candidate.Lines, [Predicate[string]] { param($line) $line -match '^\s+run traceReplay' })
                $stepNumber = $candidate.At - $run
                if ($stepNumber -lt $bestStep) {
                    $best = $candidate
                    $bestTrace = $trace
                    $bestStep = $stepNumber
                }
            }
        }

        if (-not $best) {
            $msg = "$($mutant.Key): no validated ledger trace holds a step to change."
            Write-Host $msg
            $failures.Add($msg)
            continue
        }

        $module = 'Ledger' + ((Get-Culture).TextInfo.ToTitleCase($mutant.Key.Replace('-', ' ')).Replace(' ', ''))
        $mutantJobs.Add([pscustomobject]@{
            Name = $mutant.Key
            Trace = $bestTrace.Name
            Step = $bestStep
            StepLine = $best.Lines[$best.At].Trim()
            BeforeModule = "$($module)Before"
            BeforePath = Write-LedgerTruncated "$($module)Before" $best.Lines ($best.At - 1)
            AtModule = "$($module)At"
            AtPath = Write-LedgerTruncated "$($module)At" $best.Lines $best.At
        })
    }

    $mutantChecks = @($mutantJobs | ForEach-Object -Parallel {
        $job = $_
        $beforeOut = & node $using:quint test $job.BeforePath --main=$($job.BeforeModule) --match '^traceReplay$' --backend typescript --max-samples=1 2>&1 | ForEach-Object { "$_" }
        $beforePassed = $LASTEXITCODE -eq 0
        $atOut = & node $using:quint test $job.AtPath --main=$($job.AtModule) --match '^traceReplay$' --backend typescript --max-samples=1 2>&1 | ForEach-Object { "$_" }
        $atPassed = $LASTEXITCODE -eq 0
        $beforeText = $beforeOut -join "`n"
        $atText = $atOut -join "`n"
        $directory = Join-Path $using:traceOutput $job.Name
        $null = New-Item -ItemType Directory -Force $directory
        Set-Content -LiteralPath (Join-Path $directory 'before.log') -Value $beforeText -Encoding utf8
        Set-Content -LiteralPath (Join-Path $directory 'at.log') -Value $atText -Encoding utf8
        $refused = $atText.Contains('QNT508') -and ($atText.Contains('Cannot continue') -or $atText.Contains('Expect condition does not hold'))
        [pscustomobject]@{
            Name = $job.Name
            Trace = $job.Trace
            Step = $job.Step
            StepLine = $job.StepLine
            Ok = $beforePassed -and -not $atPassed -and $refused
            BeforeOutput = $beforeText
            AtOutput = $atText
        }
    } -ThrottleLimit $traceWorkers)

    foreach ($mc in $mutantChecks) {
        if (-not $mc.Ok) {
            $msg = "$($mc.Name): the model did not reject $($mc.Trace)'s step $($mc.Step) ($($mc.StepLine)) at that step.`n$($mc.BeforeOutput)`n$($mc.AtOutput)"
            Write-Host "$($mc.Name): the model did not reject $($mc.Trace)'s step $($mc.Step) ($($mc.StepLine)) at that step."
            $failures.Add($msg)
        }
        else {
            $traceResults.Add([pscustomobject]@{ Name = $mc.Name; Outcome = 'Rejected'; Trace = $mc.Trace; Event = $mc.Step })
            Write-Host "$($mc.Name): Rejected at $($mc.Trace)'s step $($mc.Step)"
        }
    }

    $traceResults | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $traceOutput 'results.json')
    if ($failures.Count -gt 0) {
        throw "$($failures.Count) trace checks failed.`n$($failures -join "`n")"
    }

    Write-Host "Checked $($traceResults.Count) ledger traces and trace mutants. Logs: $traceOutput"
    exit 0
}

if ($Traces -and $ledgerTraceRoots.Count -eq 0) {
    function Invoke-Replay([string] $Path, [string] $Module) {
        $lines = & node $quint test $Path --main=$Module --match '^traceReplay$' --backend typescript 2>&1 | ForEach-Object { "$_" }
        [pscustomobject]@{ Passed = $LASTEXITCODE -eq 0; Output = $lines -join "`n" }
    }

    $all = @(Get-ChildItem -LiteralPath $Traces -Filter '*.qnt')
    $modules = @($all | Where-Object { $_.BaseName -like 'ArchiveLogsTrace*' -and $_.BaseName -notlike '*Repeated' -and $_.BaseName -notlike '*Unheld*' })
    $ledgerMaps = @($all | Where-Object { $_.BaseName -like 'LedgerMapTrace*' })
    $archives = @($all | Where-Object { $_.BaseName -match '^ArchiveTrace\d+$' })
    if ($modules.Count + $ledgerMaps.Count + $archives.Count -eq 0) {
        throw "No trace modules lie in $Traces."
    }

    # The negative controls: a marks-unheld drain's trace marks a segment whose write never landed, which must not replay.
    $controls = @($all | Where-Object { $_.BaseName -like 'ArchiveLogsTrace*Unheld*' })
    foreach ($control in $controls) {
        if ((Invoke-Replay $control.FullName $control.BaseName).Passed) {
            throw "The marks-unheld trace $($control.Name) replays: trace validation accepts a segment marked without its write."
        }

        Write-Host "$($control.BaseName): a marks-unheld drain's trace fails, as it must"
    }

    foreach ($module in $modules) {
        $replay = Invoke-Replay $module.FullName $module.BaseName
        if (-not $replay.Passed) {
            throw "The trace $($module.Name) does not replay against its model.`n$($replay.Output)"
        }

        $text = Get-Content -LiteralPath $module.FullName -Raw
        $first = [regex]::Match($text, '(?m)^\s+\.then\(drainStart\([^)]*\)\)\.expect\([^\r\n]*\)\r?\n')
        if (-not $first.Success) {
            throw "The trace $($module.Name) holds no drainStart to repeat, so its negative control would check nothing."
        }

        $repeated = Join-Path $Traces "$($module.BaseName)Repeated.qnt"
        Set-Content -LiteralPath $repeated -Value $text.Insert($first.Index, $first.Value).Replace("module $($module.BaseName) {", "module $($module.BaseName)Repeated {") -NoNewline
        if ((Invoke-Replay $repeated "$($module.BaseName)Repeated").Passed) {
            throw "The trace $($module.Name), with its first drainStart repeated, still replays: the replay checks nothing."
        }

        $actions = [regex]::Matches($text, '\.then\(').Count
        Write-Host "$($module.BaseName): replays $actions actions; with a drainStart repeated, fails"
    }

    if ($modules.Count -gt 0) {
        Write-Host "Validated $($modules.Count) archive-log traces against their model; $($controls.Count) negative controls fail."
    }

    if ($ledgerMaps.Count + $archives.Count -eq 0) {
        exit 0
    }

    # Replays each of a model's traces, which must pass, and returns them, each with its lines, for its trace mutants.
    function Invoke-Validation([System.IO.FileInfo[]] $Modules, [string] $Kind, [string] $Model) {
        $validated = [System.Collections.Generic.List[object]]::new()
        foreach ($module in $Modules) {
            $clock = [System.Diagnostics.Stopwatch]::StartNew()
            $replay = Invoke-Replay $module.FullName $module.BaseName
            if (-not $replay.Passed) {
                throw "The $Kind trace $($module.Name) does not replay against $Model.`n$($replay.Output)"
            }

            $lines = [string[]] @(Get-Content -LiteralPath $module.FullName)
            $validated.Add([pscustomobject]@{ Name = $module.BaseName; Lines = $lines })
            $steps = @($lines | Where-Object { $_ -match '^\s+\.then\(' }).Count
            Write-Host "$($module.BaseName): replays $steps steps ($([Math]::Round($clock.Elapsed.TotalSeconds)) s)"
        }

        , $validated
    }

    # A trace mutant changes one step of the first validated trace which holds what it looks for, into a step the run did
    # not take, or moves a step the run took to where it did not take it. Under the mutants directory, the mutated replay
    # through the step before the change must pass, and the replay through the changed step must fail with Quint's
    # QNT508: the model refuses the changed step itself, by its action, which the model disables, or by its expectation,
    # which the state after it breaks, as each mutant names.
    $mutantDirectory = Join-Path $Traces 'mutants'
    Remove-Item -LiteralPath $mutantDirectory -Recurse -Force -ErrorAction SilentlyContinue
    $null = New-Item -ItemType Directory -Force $mutantDirectory

    function Write-Truncated([string] $Module, [string[]] $Lines, [int] $Through) {
        $text = ((@($Lines[0..$Through]) + '}') -join "`n") -replace '(?m)^module \w+ \{', "module $Module {"
        $path = Join-Path $mutantDirectory "$Module.qnt"
        Set-Content -LiteralPath $path -Value $text.Replace('from "../../specs/', 'from "../../../specs/') -NoNewline
        $path
    }

    function Set-Step([string[]] $Lines, [int] $At, [string] $Step) {
        $copy = [string[]] $Lines.Clone()
        $copy[$At] = $Step
        @{ Lines = $copy; At = $At }
    }

    function Add-Step([string[]] $Lines, [int] $At, [string] $Step) {
        $copy = [System.Collections.Generic.List[string]]::new($Lines)
        $copy.Insert($At, $Step)
        @{ Lines = $copy.ToArray(); At = $At }
    }

    # The line of the first step which closes ledger $Id at or after line $From, or -1.
    function Find-Close([string[]] $Lines, [string] $Id, [int] $From = 0) {
        for ($i = $From; $i -lt $Lines.Count; $i++) {
            if ($Lines[$i] -match "^\s+\.then\((?:(?:takeover|split)\($Id\)|merge\($Id, \d+\)|merge\(\d+, $Id\))\)") {
                return $i
            }
        }

        -1
    }

    function Invoke-TraceMutant([object[]] $Validated, [string] $Kind, [string] $Name, [string] $Invariant, [string] $Refusal, [scriptblock] $Change) {
        $changed = $null
        foreach ($trace in $Validated) {
            $changed = & $Change $trace.Lines
            if ($changed) {
                break
            }
        }

        if (-not $changed) {
            throw "$Name`: no validated $Kind trace holds a step to change."
        }

        $run = [Array]::FindIndex($changed.Lines, [Predicate[string]] { param($line) $line -match '^\s+run traceReplay' })
        $module = $trace.Name + ((Get-Culture).TextInfo.ToTitleCase($Name.Replace('-', ' ')).Replace(' ', ''))
        $before = Invoke-Replay (Write-Truncated "$($module)Before" $changed.Lines ($changed.At - 1)) "$($module)Before"
        $at = Invoke-Replay (Write-Truncated "$($module)At" $changed.Lines $changed.At) "$($module)At"
        $step = "$($trace.Name)'s step $($changed.At - $run), $($changed.Lines[$changed.At].Trim())"
        $message = if ($Refusal -eq 'action') { 'Cannot continue' } else { 'Expect condition does not hold' }
        if (-not $before.Passed -or $at.Passed -or -not $at.Output.Contains('QNT508') -or -not $at.Output.Contains($message)) {
            throw "$Name`: the model did not refuse $step by its $Refusal at that step.`n$($before.Output)`n$($at.Output)"
        }

        Write-Host "$Name ($Invariant): rejected by its $Refusal at $step"
    }

    # The trace mutants, each with the invariant whose guarantee forbids the changed step and how the model refuses it.
    $ledgerMapMutants = [ordered]@{
        # A takeover leaves its ledger open beside its successor: two open owners of the range (LG-I1, LG-I2).
        'two-owners' = 'OneOwner', 'expectation', {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match '^\s+\.then\(takeover\((\d+)\)\)\.expect\(.*mapIs\(') {
                    return Set-Step $lines $i ([regex]::Replace($lines[$i], "\(($($Matches[1])), (\d+), (\d+), false\)", '($1, $2, $3, true)'))
                }
            }
        }
        # An install takes the ID after next_ledger_id, skipping one (LG-I3).
        'skip-id' = 'IdsDense', 'expectation', {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match 'mapIs\(.*, (\d+)\) \}\) // installs') {
                    return Set-Step $lines $i ([regex]::Replace($lines[$i], ', (\d+)\) \}\) // installs', { param($m) ", $([int] $m.Groups[1].Value + 1)) }) // installs" }))
                }
            }
        }
        # A takeover's successor takes its predecessor's ID, so their tags no longer order the key's history (LG-I3, LG-I4).
        'reuse-id' = 'HistoryOrdered', 'expectation', {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match '^\s+\.then\(takeover\((\d+)\)\)\.expect\(.*mapIs\(.*// installs (\d+)$') {
                    return Set-Step $lines $i ([regex]::Replace($lines[$i], "\($($Matches[2]), (\d+), (\d+), true\)", "($($Matches[1]), `$1, `$2, true)"))
                }
            }
        }
        # A write lands in a ledger before the map write which installs it (ledgers.md §8.1, §8.5).
        'write-before-install' = 'AckedInHistory', 'action', {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match '^\s+\.then\(write\((\d+), (\d+), (?:true|false)\)\)' -and [int] $Matches[1] -gt 1) {
                    $id = $Matches[1]
                    $key = $Matches[2]
                    for ($j = 0; $j -lt $i; $j++) {
                        if ($lines[$j] -match '// installs (.*)$' -and ($Matches[1] -split ', ') -contains $id) {
                            return Add-Step $lines $j "    .then(write($id, $key, false)).expect(Invariants)"
                        }
                    }
                }
            }
        }
        # A write lands in a ledger after the write which closes it, past its seal and its recorded T (LG-I4, LG-I5).
        'write-after-seal' = 'HistoryOrdered', 'action', {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match '^\s+\.then\(write\((\d+), (\d+), (?:true|false)\)\)') {
                    $id = $Matches[1]
                    $key = $Matches[2]
                    if ((Find-Close $lines $id $i) -ge 0) {
                        return Add-Step $lines ((Find-Close $lines $id $i) + 1) "    .then(write($id, $key, false)).expect(Invariants)"
                    }
                }
            }
        }
        # A ledger with writes is dropped before the Archive covers it (LSM-I3).
        'drop-undrained' = 'AckedInHistory', 'action', {
            param([string[]] $lines)
            for ($i = 0; $i + 1 -lt $lines.Count; $i++) {
                if ($lines[$i] -match '^\s+\.then\(drain\((\d+), \d+\)\)' -and $lines[$i + 1] -match "^\s+\.then\(drop\($($Matches[1])\)\)") {
                    $copy = [System.Collections.Generic.List[string]]::new($lines)
                    $copy.RemoveAt($i)
                    return @{ Lines = $copy.ToArray(); At = $i }
                }
            }
        }
        # A closed ledger leaves the map while ledger 1, whose root range overlaps it and whose ID is lower, is listed
        # (LG-I10): its drop, with its drain, moves before ledger 1's.
        'drop-out-of-order' = 'DropsInLineageOrder', 'action', {
            param([string[]] $lines)
            for ($d = 0; $d -lt $lines.Count; $d++) {
                if ($lines[$d] -notmatch '^\s+\.then\(drop\(1\)\)') {
                    continue
                }

                $start = if ($lines[$d - 1] -match '^\s+\.then\(drain\(1, ') { $d - 1 } else { $d }
                for ($e = $d + 1; $e -lt $lines.Count; $e++) {
                    if ($lines[$e] -match '^\s+\.then\(drop\((\d+)\)\)' -and ($close = Find-Close $lines $Matches[1]) -ge 0 -and $close -lt $start) {
                        $first = if ($lines[$e - 1] -match "^\s+\.then\(drain\($($Matches[1]), ") { $e - 1 } else { $e }
                        $copy = [System.Collections.Generic.List[string]]::new($lines)
                        $block = $copy.GetRange($first, $e - $first + 1)
                        $copy.RemoveRange($first, $e - $first + 1)
                        $copy.InsertRange($start, $block)
                        return @{ Lines = $copy.ToArray(); At = $start + $block.Count - 1 }
                    }
                }
            }
        }
        # A batch defines a key its segment's dictionary already holds, which a writer whose dictionary lost step with its
        # decided batches writes (LG-I12).
        'referencing-as-defining' = 'ReferencesResolve', 'expectation', {
            param([string[]] $lines)
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match 'wrote\((\d+), (\d+), (\d+), false\)') {
                    return Set-Step $lines $i ($lines[$i].Replace($Matches[0], "wrote($($Matches[1]), $($Matches[2]), $($Matches[3]), true)"))
                }
            }
        }
    }

    if ($ledgerMaps.Count -gt 0) {
        $validated = Invoke-Validation $ledgerMaps 'ledger map' 'LedgerMap.qnt'
        foreach ($mutant in $ledgerMapMutants.GetEnumerator()) {
            Invoke-TraceMutant $validated 'ledger map' $mutant.Key $mutant.Value[0] $mutant.Value[1] $mutant.Value[2]
        }

        Write-Host "Validated $($ledgerMaps.Count) ledger map traces against LedgerMap.qnt; $($ledgerMapMutants.Count) trace mutants rejected at their changed steps."
    }

    if ($archives.Count -eq 0) {
        exit 0
    }

    # An LSM trace's rows of its key, in a set's text, and back.
    function Get-Rows([string] $Text) {
        @([regex]::Matches($Text, 'tag: (\d+), kind: "(\w)"') | ForEach-Object { [pscustomobject]@{ Tag = [int] $_.Groups[1].Value; Kind = $_.Groups[2].Value } })
    }

    function Format-Rows([object[]] $Rows) {
        ($Rows | Sort-Object Tag | ForEach-Object { "{ tag: $($_.Tag), kind: `"$($_.Kind)`" }" }) -join ', '
    }

    # Walks an LSM trace's steps, tracking the model's state before each, its objects with their rows (a drain's with its
    # ledger and segment), its references, each ledger's watermark, and the watermarks the last commit expected, and
    # returns the first change $Match makes of a step, given the step and that state.
    function Find-ArchiveStep([string[]] $Lines, [scriptblock] $Match) {
        $objects = @{}
        $next = 1
        $refs = @()
        $watermarks = @{ '1' = 0; '2' = 0 }
        $expected = @{ '1' = 0; '2' = 0 }
        for ($i = 0; $i -lt $Lines.Count; $i++) {
            $line = $Lines[$i]
            if ($line -notmatch '^\s+\.then\(') {
                continue
            }

            $found = & $Match $i $line $objects $refs $watermarks $expected
            if ($found) {
                return $found
            }

            if ($line -match '^\s+\.then\(drainUpload\((\d), (\d+)\)\).*\.rows == Set\(([^)]*)\)') {
                $objects[$next++] = @{ Ledger = $Matches[1]; A = $watermarks[$Matches[1]]; B = [int] $Matches[2]; Rows = Get-Rows $Matches[3] }
            }
            elseif ($line -match '^\s+\.then\(compactKeep\(Set\(([^)]*)\), Set\(([^)]*)\)\)\)') {
                $objects[$next++] = @{ Rows = Get-Rows $Matches[2] }
            }
            elseif ($line -match '^\s+\.then\(drainCommit\((\d+)\)\)') {
                $drain = $objects[[int] $Matches[1]]
                $watermarks[$drain.Ledger] = $drain.B
            }

            if ($line -match 's\.refs == Set\(([^)]*)\)') {
                $refs = @($Matches[1] -split ', ' | Where-Object { $_ } | ForEach-Object { [int] $_ })
            }

            if ($line -match 's\.watermark == Map\(1 -> (\d+), 2 -> (\d+)\)') {
                $expected = @{ '1' = [int] $Matches[1]; '2' = [int] $Matches[2] }
            }
        }
    }

    $archiveMutants = [ordered]@{
        # The sweeper deletes an object the manifest references (LSM-I6).
        'referenced-delete' = 'ReferencedExist', 'action', {
            param([string[]] $lines)
            Find-ArchiveStep $lines {
                param($i, $line, $objects, $refs)
                if ($line -match '^\s+\.then\(sweepDelete\(\d+\)\)' -and $refs.Count -gt 0) {
                    Set-Step $lines $i ($line -replace 'sweepDelete\(\d+\)', "sweepDelete($(($refs | Measure-Object -Minimum).Minimum))")
                }
            }
        }
        # A compaction's output loses its inputs' newest row, an Append or a Replace, which nothing supersedes (LSM-I5).
        'over-dropping-compaction' = 'FoldPreserved', 'action', {
            param([string[]] $lines)
            Find-ArchiveStep $lines {
                param($i, $line, $objects)
                if ($line -match '^\s+\.then\(compactKeep\(Set\(([^)]*)\), Set\(([^)]*)\)\)\)') {
                    $kept = $Matches[2]
                    $rows = Get-Rows $kept
                    $newest = @($Matches[1] -split ', ' | ForEach-Object { $objects[[int] $_].Rows } | Sort-Object Tag -Descending)[0]
                    if ($newest -and $newest.Kind -in 'A', 'R' -and ($rows | Where-Object Tag -EQ $newest.Tag)) {
                        Set-Step $lines $i ($line.Replace("Set($kept)", "Set($(Format-Rows @($rows | Where-Object Tag -NE $newest.Tag)))"))
                    }
                }
            }
        }
        # A commit leaves the manifest with a watermark below the one before it (LSM-I1).
        'lowered-watermark' = 'WatermarksMonotone', 'expectation', {
            param([string[]] $lines)
            Find-ArchiveStep $lines {
                param($i, $line, $objects, $refs, $watermarks, $expected)
                foreach ($ledger in '1', '2') {
                    if ($line -match "s\.watermark == Map\(" -and $expected[$ledger] -gt 0) {
                        return Set-Step $lines $i ($line -replace "$ledger -> \d+", "$ledger -> 0")
                    }
                }
            }
        }
        # A drain ends inside its segment, one slot past the watermark (LSM-I2).
        'mid-segment-drain' = 'WatermarksAtSegmentStarts', 'action', {
            param([string[]] $lines)
            Find-ArchiveStep $lines {
                param($i, $line, $objects, $refs, $watermarks)
                if ($line -match '^\s+\.then\(drainUpload\((\d), (\d+)\)\)' -and [int] $Matches[2] - $watermarks[$Matches[1]] -ge 2) {
                    Set-Step $lines $i ($line -replace 'drainUpload\((\d), \d+\)', "drainUpload(`$1, $($watermarks[$Matches[1]] + 1))")
                }
            }
        }
        # A history read answers without its newest row (LSM-I4, LSM-I5, HI-I1).
        'wrong-read' = 'ReadsCorrect', 'expectation', {
            param([string[]] $lines)
            Find-ArchiveStep $lines {
                param($i, $line)
                if ($line -match '^\s+\.then\(readFinish\(\d+\)\)\.expect\(and \{ Invariants, Set\(([^)]+)\) ==') {
                    $answer = $Matches[1]
                    $rows = Get-Rows $answer
                    $newest = ($rows | Sort-Object Tag -Descending)[0]
                    Set-Step $lines $i ($line.Replace("Set($answer) ==", "Set($(Format-Rows @($rows | Where-Object Tag -NE $newest.Tag))) =="))
                }
            }
        }
    }

    $validated = Invoke-Validation $archives 'LSM' 'Archive.qnt'
    foreach ($mutant in $archiveMutants.GetEnumerator()) {
        Invoke-TraceMutant $validated 'LSM' $mutant.Key $mutant.Value[0] $mutant.Value[1] $mutant.Value[2]
    }

    Write-Host "Validated $($archives.Count) LSM traces against Archive.qnt; $($archiveMutants.Count) trace mutants rejected at their changed steps."
    exit 0
}

# Quint's TLC backend translates through Apalache and runs the TLC bundled in its
# archive. The archive is pinned and verified here.
$apalacheVersion = '0.62.1'
$apalacheHash = '38202A8C4EBEB3AB73E0FE5A7E4BB948442D46AD5DE8A7FB45C9E5CAC2BEB2A6'
$env:QUINT_HOME = Join-Path $root 'artifacts' 'tools' 'quint-home'
$apalacheDist = Join-Path $env:QUINT_HOME "apalache-dist-$apalacheVersion"
$apalacheJar = Join-Path $apalacheDist 'apalache' 'lib' 'apalache.jar'
if (-not (Test-Path -LiteralPath $apalacheJar)) {
    $null = New-Item -ItemType Directory -Force $apalacheDist
    $archive = Join-Path $apalacheDist 'apalache.tgz'
    Invoke-WebRequest `
        -Uri "https://github.com/apalache-mc/apalache/releases/download/v$apalacheVersion/apalache.tgz" `
        -OutFile $archive
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $apalacheHash) {
        throw 'The downloaded Apalache archive does not match the pinned SHA-256.'
    }

    & tar -xzf $archive -C $apalacheDist
    if ($LASTEXITCODE -ne 0) {
        throw 'Extracting the Apalache archive failed.'
    }

    Remove-Item -LiteralPath $archive
}

# Quint connects to a running Apalache server before trying to launch one, and
# Node cannot launch Apalache's Windows batch file, so the runner owns the server.
$listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$serverLog = Join-Path $output 'apalache-server.log'
$server = Start-Process java -ArgumentList '-Xmx1g', '-jar', "`"$apalacheJar`"", 'server', "--port=$port" `
    -PassThru -NoNewWindow -WorkingDirectory $output `
    -RedirectStandardOutput $serverLog -RedirectStandardError "$serverLog.err"

function Wait-Server {
    $deadline = [DateTime]::UtcNow.AddSeconds(120)
    while ([DateTime]::UtcNow -lt $deadline) {
        if ($server.HasExited) {
            throw "The Apalache server exited with code $($server.ExitCode). See $serverLog."
        }

        $client = [System.Net.Sockets.TcpClient]::new()
        try {
            $client.Connect([System.Net.IPAddress]::Loopback, $port)
            return
        }
        catch {
            Start-Sleep -Milliseconds 500
        }
        finally {
            $client.Dispose()
        }
    }

    throw 'The Apalache server did not start listening within 120 seconds.'
}

$results = [System.Collections.Generic.List[object]]::new()

# Runs one case. A positive case must exhaust its state space. A case naming
# -ExpectedInvariant or -ExpectedProperty checks only that property and must
# produce TLC's counterexample for it.
function Invoke-Case {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Module,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Constants,
        [string[]] $Invariants = @(),
        [string[]] $Temporal = @(),
        [string] $ExpectedInvariant,
        [string] $ExpectedProperty
    )

    if (($Model -notcontains 'all' -and $Model -notcontains $Module) -or $Name -notlike $Only) {
        return
    }

    if ($ExpectedInvariant) {
        $Invariants = @($ExpectedInvariant)
        $Temporal = @()
    }
    elseif ($ExpectedProperty) {
        $Invariants = @()
        $Temporal = @($ExpectedProperty)
    }

    $directory = Join-Path $output $Name
    $null = New-Item -ItemType Directory -Force $directory
    $spec = [System.IO.Path]::GetRelativePath($directory, (Join-Path $specs[$Module] $Module)).Replace('\', '/')
    $bindings = ($Constants.GetEnumerator() | ForEach-Object { "$($_.Key) = $($_.Value)" }) -join ', '
    $instance = Join-Path $directory 'MC.qnt'
    Set-Content -LiteralPath $instance -Encoding ascii -Value @"
module MC {
  import $Module($bindings).* from "$spec"
}
"@
    $tlcConfig = Join-Path $directory 'tlc.json'
    Set-Content -LiteralPath $tlcConfig -Encoding ascii -Value "{ `"workers`": $Workers, `"maxHeap`": `"-Xmx2G`" }"

    $arguments = @(
        $quint, 'verify', $instance, '--main=MC', '--backend=tlc', '--verbosity=3',
        "--tlc-config=$tlcConfig", "--server-endpoint=localhost:$port", "--apalache-version=$apalacheVersion")
    if ($Invariants.Count -gt 0) {
        $arguments += "--invariant=$($Invariants -join ',')"
    }

    if ($Temporal.Count -gt 0) {
        $arguments += "--temporal=$($Temporal -join ',')"
    }

    $log = Join-Path $directory 'TLC.log'
    $lines = & node @arguments 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $log
    $exitCode = $LASTEXITCODE
    $text = $lines -join "`n"
    $counts = [regex]::Match(
        $text,
        '(?m)^(\d+) states generated, (\d+) distinct states found, (\d+) states left on queue\.$')

    if ($ExpectedInvariant) {
        $violated = ($text.Contains('Invariant q_inv is violated.') -and $text.Contains('The behavior up to this point is:')) `
            -or $text.Contains('Invariant q_inv is violated by the initial state:')
        if ($exitCode -eq 0 -or -not $violated) {
            throw "$Name did not produce the expected $ExpectedInvariant counterexample.`n$text"
        }

        $outcome = 'Expected counterexample'
    }
    elseif ($ExpectedProperty) {
        if ($exitCode -eq 0 -or -not $text.Contains('Temporal property q_temporalProps was violated.') `
            -or -not $text.Contains('The following behavior constitutes a counter-example:')) {
            throw "$Name did not produce the expected $ExpectedProperty counterexample.`n$text"
        }

        $outcome = 'Expected counterexample'
    }
    else {
        if ($exitCode -ne 0 -or -not $text.Contains('Model checking completed. No error has been found.') `
            -or -not $counts.Success -or $counts.Groups[3].Value -ne '0') {
            throw "$Name did not exhaust its reachable state space successfully.`n$text"
        }

        $outcome = 'Exhausted'
    }

    $distinct = if ($counts.Success) { $counts.Groups[2].Value } else { '?' }
    Write-Host "$Name`: $outcome ($distinct distinct states)"
    $results.Add([pscustomobject]@{
        Name = $Name
        Module = $Module
        Outcome = $outcome
        GeneratedStates = if ($counts.Success) { [long] $counts.Groups[1].Value } else { $null }
        DistinctStates = if ($counts.Success) { [long] $counts.Groups[2].Value } else { $null }
        ExpectedInvariant = $ExpectedInvariant
        ExpectedProperty = $ExpectedProperty
        Log = $log
    })
}

# Runs one bounded random simulation of a case with Quint's simulator from a fixed seed. Without -ExpectedInvariant,
# it must find no violation (a per-check sample of a case too large to exhaust on every change); with -ExpectedInvariant,
# it must find a violation of that invariant (for a counterexample deeper than exhaustive search reaches in reasonable time).
function Invoke-Simulation {
    param(
        [Parameter(Mandatory)] [string] $Name,
        [Parameter(Mandatory)] [string] $Module,
        [Parameter(Mandatory)] [System.Collections.IDictionary] $Constants,
        [string[]] $Invariants = @(),
        [string] $ExpectedInvariant,
        [Parameter(Mandatory)] [int] $Samples,
        [Parameter(Mandatory)] [int] $Steps,
        [Parameter(Mandatory)] [int] $Seed
    )

    if (($Model -notcontains 'all' -and $Model -notcontains $Module) -or $Name -notlike $Only) {
        return
    }

    if ($ExpectedInvariant) {
        $Invariants = @($ExpectedInvariant)
    }

    $directory = Join-Path $output $Name
    $null = New-Item -ItemType Directory -Force $directory
    $spec = [System.IO.Path]::GetRelativePath($directory, (Join-Path $specs[$Module] $Module)).Replace('\', '/')
    $bindings = ($Constants.GetEnumerator() | ForEach-Object { "$($_.Key) = $($_.Value)" }) -join ', '
    $instance = Join-Path $directory 'MC.qnt'
    Set-Content -LiteralPath $instance -Encoding ascii -Value @"
module MC {
  import $Module($bindings).* from "$spec"
}
"@
    $log = Join-Path $directory 'simulation.log'
    $invariantArg = if ($Invariants.Count -eq 1) { $Invariants[0] } else { "and { $($Invariants -join ', ') }" }
    $lines = & node $quint run $instance --main=MC "--max-samples=$Samples" "--max-steps=$Steps" "--seed=$Seed" `
        "--invariant=$invariantArg" 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $log
    $text = $lines -join "`n"
    if ($ExpectedInvariant) {
        if ($LASTEXITCODE -eq 0 -or -not $text.Contains('[violation]')) {
            throw "$Name did not produce the expected $ExpectedInvariant simulated counterexample.`n$text"
        }

        Write-Host "$Name`: Expected simulated counterexample (seed $Seed)"
        $results.Add([pscustomobject]@{ Name = $Name; Module = $Module; Outcome = 'Expected counterexample'; GeneratedStates = $null; DistinctStates = $null; ExpectedInvariant = $ExpectedInvariant; ExpectedProperty = $null; Log = $log })
    }
    else {
        if ($LASTEXITCODE -ne 0 -or -not $text.Contains('[ok] No violation found')) {
            throw "$Name found a violation or did not run.`n$text"
        }

        Write-Host "$Name`: No violation in $Samples samples of $Steps steps (seed $Seed)"
        $results.Add([pscustomobject]@{ Name = $Name; Module = $Module; Outcome = 'Sampled'; GeneratedStates = $null; DistinctStates = $null; ExpectedInvariant = $null; ExpectedProperty = $null; Log = $log })
    }
}

function With([System.Collections.IDictionary] $Base, [hashtable] $Overrides) {
    $merged = [ordered]@{}
    foreach ($entry in $Base.GetEnumerator()) {
        $merged[$entry.Key] = $entry.Value
    }

    foreach ($entry in $Overrides.GetEnumerator()) {
        if (-not $merged.Contains($entry.Key)) {
            throw "Unknown constant $($entry.Key)."
        }

        $merged[$entry.Key] = $entry.Value
    }

    return $merged
}

function Mutant([System.Collections.IDictionary] $Base, [string] $Mutation) {
    return With $Base @{ Mutation = "`"$Mutation`"" }
}

try {
    Wait-Server

    # QuorumCoverageSealing: the four quorum-coverage definitions agree over every cohort and quorum of AllReplicas.
    $coverage = [ordered]@{ AllReplicas = 'Set(0, 1, 2, 3)'; Mutation = '"none"' }
    Invoke-Case 'quorum-coverage-4' 'QuorumCoverageSealing' $coverage @('Equivalence')
    Invoke-Case 'quorum-coverage-mutant-off-by-one' 'QuorumCoverageSealing' (Mutant $coverage 'off-by-one') `
        -ExpectedInvariant 'Equivalence'
    if ($Extended) {
        Invoke-Case 'quorum-coverage-5' 'QuorumCoverageSealing' `
            (With $coverage @{ AllReplicas = 'Set(0, 1, 2, 3, 4)' }) @('Equivalence')
    }

    # AtomicLedger: abstract single-ledger storage contract with seal, tail checks, and fetchers.
    $atomic = [ordered]@{ Slots = '2'; Readers = 'Set(0)'; Fetchers = 'Set(0)'; Mutation = '"none"' }
    $atomicInvariants = @('TypeOK', 'OpenCheckSound', 'CheckTailBelowEnd', 'BelowTailDecided')
    Invoke-Case 'atomic-ledger' 'AtomicLedger' $atomic $atomicInvariants
    foreach ($case in @(
        @('atomic-late-open', 'OpenCheckSound'),
        @('atomic-check-issued', 'CheckTailBelowEnd'),
        @('atomic-learn-issued', 'BelowTailDecided'))) {
        Invoke-Case "atomic-ledger-mutant-$($case[0])" 'AtomicLedger' (Mutant $atomic $case[0]) `
            -ExpectedInvariant $case[1]
    }

    # LedgerProtocol: BookKeeper-style quorum ledger protocol (safety, refinement to AtomicLedger, liveness, mutants, witnesses).
    $lpBase = [ordered]@{
        Replicas = 'Set(0, 1, 2)'
        Quorum = '2'
        Slots = '2'
        Window = '1'
        Recoverers = 'Set(0)'
        Readers = 'Set()'
        Fetchers = 'Set()'
        TailWrites = 'false'
        Live = 'Set()'
        Faults = 'Set()'
        Duplicates = '0'
        SeparateAnswers = 'false'
        Mutation = '"none"'
    }
    $lpSmall = With $lpBase @{ Slots = '1' }
    $lpTwo = With $lpBase @{ Replicas = 'Set(0, 1)'; Quorum = '2'; Slots = '1' }
    $lpTwoS2 = With $lpTwo @{ Slots = '2' }
    $lpTwoQ1 = With $lpTwo @{ Quorum = '1' }
    $lpConcurrent = With $lpTwo @{ Recoverers = 'Set(0, 1)' }
    $lpConcurrentQ1 = With $lpConcurrent @{ Quorum = '1' }
    $lpLive = With $lpSmall @{ Live = 'Set(0, 1)' }
    $lpLiveConcurrent = With $lpConcurrentQ1 @{ Live = 'Set(0, 1)' }
    $lpReaderTwo = With $lpTwo @{ Readers = 'Set(0)' }
    $lpReader = With $lpSmall @{ Readers = 'Set(0)' }
    $lpFetcher = With $lpSmall @{ Fetchers = 'Set(0)'; TailWrites = 'true' }
    $lpFetcherTwo = With $lpTwo @{ Fetchers = 'Set(0)'; TailWrites = 'true' }
    $lpAnswers = With $lpTwoQ1 @{ SeparateAnswers = 'true' }
    $lpAnswersQ2 = With $lpTwo @{ SeparateAnswers = 'true' }
    $lpFaults = With $lpSmall @{ Faults = 'Set("lose-node", "corrupt")' }
    $lpFaultsTwo = With $lpTwo @{ Faults = 'Set("lose-node", "corrupt")' }
    $lpTwoWindow = With $lpTwoQ1 @{ Slots = '3'; Window = '1' }
    $lpK2Two = With $lpTwo @{ Slots = '2'; Window = '2' }

    $lpInvariants = @(
        'TypeOK',
        'AcknowledgedSurvive',
        'NoAcknowledgementAtOrAboveEnd',
        'EndSealed',
        'TombstoneExcludesReplica',
        'NoRevokedAnswer',
        'ReadersAgree',
        'ReadersSeeClose',
        'OpenCheckSound',
        'CheckTailBelowEnd',
        'RecoveryBounded',
        'RetainedBelowEnd',
        'RecoveriesConverge',
        'ReadableBelowEnd',
        'BelowTailDecided',
        'RefinesAtomicLedger'
    )

    # Quick-tier positive cases: fast exhaustive configurations covering single and concurrent recovery,
    # Q < N and Q = N quorums, readers (tail checks + explicit tail writes), fetchers (single-replica reads +
    # drains + faults), window K = 2, separate answers, duplicates, and read failures.
    Invoke-Case 'ledger-protocol-n2-q2-s1' 'LedgerProtocol' $lpTwo $lpInvariants
    Invoke-Case 'ledger-protocol-n2-q1-s1' 'LedgerProtocol' $lpTwoQ1 $lpInvariants
    Invoke-Case 'ledger-protocol-n3-q1-s1' 'LedgerProtocol' (With $lpSmall @{ Quorum = '1' }) $lpInvariants
    Invoke-Case 'ledger-protocol-n2-q1-concurrent' 'LedgerProtocol' $lpConcurrentQ1 $lpInvariants
    Invoke-Case 'ledger-protocol-n2-q1-concurrent-reader' 'LedgerProtocol' (With $lpConcurrentQ1 @{ Readers = 'Set(0)' }) $lpInvariants
    Invoke-Case 'ledger-protocol-n2-q1-concurrent-duplicates' 'LedgerProtocol' (With $lpConcurrentQ1 @{ Duplicates = '1' }) $lpInvariants
    Invoke-Case 'ledger-protocol-n2-q2-reader' 'LedgerProtocol' $lpReaderTwo $lpInvariants
    Invoke-Case 'ledger-protocol-n2-q2-reader-tail-writes' 'LedgerProtocol' (With $lpReaderTwo @{ TailWrites = 'true' }) $lpInvariants
    Invoke-Case 'ledger-protocol-n2-q2-fetcher' 'LedgerProtocol' $lpFetcherTwo $lpInvariants
    Invoke-Case 'ledger-protocol-n2-q2-fetcher-faults' 'LedgerProtocol' (With $lpFetcherTwo @{ Faults = 'Set("lose-node", "corrupt")' }) $lpInvariants
    Invoke-Case 'ledger-protocol-n2-q2-read-failures' 'LedgerProtocol' (With $lpTwo @{ Faults = 'Set("read-failure")' }) $lpInvariants
    Invoke-Case 'ledger-protocol-n2-q2-k2' 'LedgerProtocol' $lpK2Two $lpInvariants
    Invoke-Case 'ledger-protocol-n2-q1-answers' 'LedgerProtocol' $lpAnswers $lpInvariants

    # Every safety, refinement, replica-durability, reader, and fetcher mutant runs in Quick, Default, and Extended.
    $lpMutants = @(
        # Recovery and AtomicLedger refinement boundaries.
        @('short-negatives', $lpSmall, 'AcknowledgedSurvive'),
        @('short-negatives', $lpSmall, 'RefinesAtomicLedger'),
        @('no-rereplication', $lpSmall, 'ReadersAgree'),
        @('no-rereplication', (With $lpSmall @{ Faults = 'Set("lose-node")' }), 'ReadableBelowEnd'),
        @('unconditional-close', $lpConcurrent, 'RecoveriesConverge'),
        @('unconditional-close', $lpConcurrent, 'RefinesAtomicLedger'),
        @('seal-short', $lpTwoWindow, 'RecoveryBounded'),
        @('retain-recovered', $lpConcurrent, 'RetainedBelowEnd'),
        @('retain-recovered', (With $lpConcurrent @{ Readers = 'Set(0)' }), 'CheckTailBelowEnd'),
        @('learn-own-end', $lpConcurrent, 'RetainedBelowEnd'),
        @('unsealed-negatives', $lpTwo, 'NoAcknowledgementAtOrAboveEnd'),
        @('no-tombstone', $lpTwo, 'NoAcknowledgementAtOrAboveEnd'),
        @('no-tombstone', $lpTwo, 'EndSealed'),
        @('no-tombstone', $lpTwo, 'OpenCheckSound'),
        @('no-tombstone', $lpTwo, 'RefinesAtomicLedger'),
        @('drop-open', $lpTwo, 'RefinesAtomicLedger'),
        @('check-ignores-seal', $lpReaderTwo, 'RefinesAtomicLedger'),
        @('check-stored-tail', $lpReaderTwo, 'RefinesAtomicLedger'),
        @('fetch-stored-tail', $lpFetcherTwo, 'RefinesAtomicLedger'),
        # Replicas, durability, tombstones, and faults.
        @('count-sealed', $lpTwo, 'NoAcknowledgementAtOrAboveEnd'),
        @('sealed-accepts', $lpTwo, 'NoAcknowledgementAtOrAboveEnd'),
        @('sealed-accepts', $lpTwo, 'AcknowledgedSurvive'),
        @('seal-no-create', $lpTwo, 'NoAcknowledgementAtOrAboveEnd'),
        @('seal-no-create', $lpTwo, 'OpenCheckSound'),
        @('copy-unseals', $lpTwo, 'OpenCheckSound'),
        @('copy-unseals', $lpTwo, 'EndSealed'),
        @('reopen-h1', $lpTwo, 'RetainedBelowEnd'),
        @('count-answers', (With $lpTwo @{ Duplicates = '1' }), 'AcknowledgedSurvive'),
        @('seal-volatile', $lpTwo, 'NoAcknowledgementAtOrAboveEnd'),
        @('seal-volatile', $lpTwo, 'NoRevokedAnswer'),
        @('write-volatile', $lpTwo, 'NoRevokedAnswer'),
        @('read-volatile', $lpTwo, 'NoRevokedAnswer'),
        @('copy-volatile', $lpTwo, 'NoRevokedAnswer'),
        @('create-volatile', $lpTwo, 'NoRevokedAnswer'),
        @('repeat-seal-volatile', $lpConcurrent, 'NoRevokedAnswer'),
        @('write-sealed-volatile', $lpTwo, 'NoRevokedAnswer'),
        @('check-sealed-volatile', $lpReaderTwo, 'NoRevokedAnswer'),
        @('restart-drops-seal', $lpTwo, 'EndSealed'),
        @('restart-drops-seal', $lpTwo, 'NoAcknowledgementAtOrAboveEnd'),
        @('corrupt-as-missing', (With $lpTwo @{ Faults = 'Set("corrupt")' }), 'AcknowledgedSurvive'),
        @('split-create', $lpTwo, 'EndSealed'),
        @('split-create', $lpTwo, 'NoAcknowledgementAtOrAboveEnd'),
        @('copy-ignores-tombstone', $lpTwoS2, 'NoAcknowledgementAtOrAboveEnd'),
        @('count-dropped', $lpTwo, 'NoAcknowledgementAtOrAboveEnd'),
        # Readers' tail checks and fetchers.
        @('open-check-short', $lpReaderTwo, 'ReadersSeeClose'),
        @('sealed-quorum', $lpReaderTwo, 'ReadersSeeClose'),
        @('check-ignores-seal', $lpReaderTwo, 'ReadersSeeClose'),
        @('check-counts-dropped', $lpReaderTwo, 'ReadersSeeClose'),
        @('check-stored-tail', $lpReaderTwo, 'CheckTailBelowEnd'),
        @('eager-tail', (With $lpFetcherTwo @{ Window = '2'; Slots = '2' }), 'BelowTailDecided'),
        @('drain-max-stored', $lpFetcherTwo, 'BelowTailDecided'),
        @('fetch-stored-tail', $lpFetcherTwo, 'BelowTailDecided')
    )
    foreach ($case in $lpMutants) {
        Invoke-Case "ledger-protocol-mutant-$($case[0])-$($case[2])" 'LedgerProtocol' (Mutant $case[1] $case[0]) `
            -ExpectedInvariant $case[2]
    }

    # At K = 2, seal-short violates RecoveryBounded only with five positions, deeper than exhaustive search reaches in
    # reasonable time, so a seeded random simulation finds its counterexample.
    Invoke-Simulation 'ledger-protocol-mutant-seal-short-k2-RecoveryBounded' 'LedgerProtocol' `
        (Mutant (With $lpBase @{ Window = '2'; Slots = '5' }) 'seal-short') `
        -ExpectedInvariant 'RecoveryBounded' -Samples 50000 -Steps 100 -Seed 1

    # Liveness mutants (under weak fairness over Live replicas):
    # - no-late-copy: a replica whose read had not answered when the entry was decided gets no copy,
    #   leaving close waiting on Q holders forever.
    # - ignore-dropped: a recovery that ignores Dropped answers loops forever after the ledger drops.
    Invoke-Case 'ledger-protocol-mutant-no-late-copy-RecoveriesEnd' 'LedgerProtocol' (Mutant $lpLive 'no-late-copy') `
        -ExpectedProperty 'RecoveriesEnd'
    Invoke-Case 'ledger-protocol-mutant-ignore-dropped-RecoveriesEnd' 'LedgerProtocol' (Mutant $lpLiveConcurrent 'ignore-dropped') `
        -ExpectedProperty 'RecoveriesEnd'

    # Assumption witnesses: breaking WAL durability (amnesia) or tombstone durability (tombstone-loss)
    # breaks the protocol's safety invariants.
    Invoke-Case 'ledger-protocol-assumption-amnesia' 'LedgerProtocol' `
        (With $lpTwo @{ Faults = 'Set("amnesia")' }) -ExpectedInvariant 'AcknowledgedSurvive'
    Invoke-Case 'ledger-protocol-assumption-tombstone-loss' 'LedgerProtocol' `
        (With $lpTwo @{ Faults = 'Set("tombstone-loss")' }) -ExpectedInvariant 'NoAcknowledgementAtOrAboveEnd'

    # Reachability witnesses: confirm the model reaches every overlapping protocol scenario (each negated
    # invariant must fail with a counterexample).
    $lpWitnesses = @(
        @('NeverDivergentEnds', $lpConcurrent),
        @('NeverAcknowledgementAfterSealedAnswer', $lpSmall),
        @('NeverDataAtOrAboveEnd', $lpTwo),
        @('NeverClosedAboveAcked', $lpTwo),
        @('NeverAcknowledgedAfterClose', $lpSmall),
        @('NeverCheckAfterDrop', $lpReaderTwo),
        @('NeverStrayAfterDrop', $lpTwo),
        @('NeverStrayDroppedAgain', $lpTwo),
        @('NeverOpenCheckAtEnd', (With $lpReaderTwo @{ TailWrites = 'true' })),
        @('NeverFoundAtBound', $lpTwoS2),
        @('NeverCopyPendingAtEnd', $lpTwoS2),
        @('NeverClosedAfterNodeLoss', (With $lpSmall @{ Faults = 'Set("lose-node")' })),
        @('NeverRepairedCorruption', (With $lpTwo @{ Faults = 'Set("corrupt")' })),
        @('NeverRepeatedWritten', (With $lpTwo @{ Duplicates = '1' })),
        @('NeverFresherRepeat', (With $lpK2Two @{ Duplicates = '1' })),
        @('NeverTakenWhileOpen', $lpFetcherTwo),
        @('NeverRepairLanded', $lpFetcher),
        @('NeverServedOpenThenClosed', $lpFetcherTwo),
        @('NeverEndsAtExactTail', (With $lpTwo @{ TailWrites = 'true' })),
        @('NeverCoverageWithAnswerInFlight', $lpAnswersQ2),
        @('NeverDeferredSend', $lpK2Two),
        @('NeverMarkedReadWhileUnmarkedInFlight', (With $lpAnswersQ2 @{ Duplicates = '1' })),
        @('NeverSealTailFromAdmittedWrite', $lpTwoS2),
        @('NeverCorruptUnwritten', $lpFaultsTwo),
        @('NeverClosedWithFewCopiesAboveEnd', $lpK2Two)
    )
    foreach ($case in $lpWitnesses) {
        Invoke-Case "ledger-protocol-witness-$($case[0])" 'LedgerProtocol' $case[1] `
            -ExpectedInvariant $case[0]
    }

    # Default tier adds positive liveness verification and larger safety configurations.
    if ($Tier -ne 'Quick') {
        Invoke-Case 'ledger-protocol-n3-q2-s1' 'LedgerProtocol' $lpSmall $lpInvariants
        Invoke-Case 'ledger-protocol-n2-q2-answers' 'LedgerProtocol' $lpAnswersQ2 $lpInvariants
        Invoke-Case 'ledger-protocol-n2-q1-answers-duplicates' 'LedgerProtocol' (With $lpAnswers @{ Duplicates = '1' }) $lpInvariants
        Invoke-Case 'ledger-protocol-liveness' 'LedgerProtocol' $lpLive -Temporal @('RecoveriesEnd')
        Invoke-Case 'ledger-protocol-liveness-concurrent' 'LedgerProtocol' $lpLiveConcurrent -Temporal @('RecoveriesEnd')
        Invoke-Case 'ledger-protocol-liveness-read-failures' 'LedgerProtocol' `
            (With $lpLive @{ Faults = 'Set("read-failure")' }) -Temporal @('RecoveriesEnd')
    }

    if ($Extended) {
        Invoke-Case 'ledger-protocol-n3-q2' 'LedgerProtocol' $lpBase $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q3' 'LedgerProtocol' (With $lpBase @{ Quorum = '3' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-reader' 'LedgerProtocol' $lpReader $lpInvariants
        Invoke-Case 'ledger-protocol-n2-q2-concurrent' 'LedgerProtocol' (With $lpConcurrent @{ Slots = '2' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n2-q2-concurrent-reader' 'LedgerProtocol' (With $lpConcurrent @{ Readers = 'Set(0)' }) $lpInvariants
        Invoke-Case 'ledger-protocol-witness-NeverCheckTailAboveCandidate' 'LedgerProtocol' `
            (With $lpConcurrent @{ Readers = 'Set(0)' }) -ExpectedInvariant 'NeverCheckTailAboveCandidate'
        Invoke-Case 'ledger-protocol-n3-q2-faults' 'LedgerProtocol' $lpFaults $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-read-failures' 'LedgerProtocol' (With $lpSmall @{ Faults = 'Set("read-failure")' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-duplicates' 'LedgerProtocol' (With $lpSmall @{ Duplicates = '1' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n2-q2-duplicates-k2' 'LedgerProtocol' (With $lpK2Two @{ Duplicates = '1' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-fetcher' 'LedgerProtocol' $lpFetcher $lpInvariants
        Invoke-Case 'ledger-protocol-n2-q2-fetcher-k2' 'LedgerProtocol' (With $lpFetcherTwo @{ Window = '2'; Slots = '2' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-reader-tail-writes' 'LedgerProtocol' (With $lpReader @{ TailWrites = 'true' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-k2' 'LedgerProtocol' (With $lpBase @{ Window = '2' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-slots3' 'LedgerProtocol' (With $lpBase @{ Slots = '3' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-faults-slots2' 'LedgerProtocol' (With $lpFaults @{ Slots = '2' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-read-failures-slots2' 'LedgerProtocol' (With $lpBase @{ Faults = 'Set("read-failure")' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-concurrent' 'LedgerProtocol' (With $lpSmall @{ Recoverers = 'Set(0, 1)' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n4-q2' 'LedgerProtocol' (With $lpSmall @{ Replicas = 'Set(0, 1, 2, 3)' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n5-q3' 'LedgerProtocol' (With $lpSmall @{ Replicas = 'Set(0, 1, 2, 3, 4)'; Quorum = '3' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-duplicates-2' 'LedgerProtocol' (With $lpSmall @{ Duplicates = '2' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n2-q2-concurrent-duplicates' 'LedgerProtocol' (With $lpConcurrent @{ Duplicates = '1' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-duplicates-k2' 'LedgerProtocol' (With $lpBase @{ Window = '2'; Duplicates = '1' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-fetcher-corrupt' 'LedgerProtocol' (With $lpFetcher @{ Faults = 'Set("corrupt")'; TailWrites = 'false' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-fetcher-k2' 'LedgerProtocol' (With $lpFetcher @{ Window = '2'; Slots = '2' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n2-q2-answers-duplicates' 'LedgerProtocol' (With $lpAnswersQ2 @{ Duplicates = '1' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-answers' 'LedgerProtocol' (With $lpSmall @{ SeparateAnswers = 'true' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n3-q2-answers-duplicates' 'LedgerProtocol' (With $lpSmall @{ SeparateAnswers = 'true'; Duplicates = '1' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n2-q2-concurrent-answers' 'LedgerProtocol' (With $lpConcurrent @{ SeparateAnswers = 'true' }) $lpInvariants
        Invoke-Case 'ledger-protocol-n2-q2-reader-answers' 'LedgerProtocol' (With $lpReaderTwo @{ SeparateAnswers = 'true' }) $lpInvariants
        Invoke-Case 'ledger-protocol-witness-NeverFoundAtBound-k2' 'LedgerProtocol' `
            (With $lpBase @{ Window = '2'; Slots = '4' }) -ExpectedInvariant 'NeverFoundAtBound'
    }

    # ObjectLedger: per-slot and append object-store ledger model refining AtomicLedger.qnt (object-ledgers.md §5, §7, §8, §10).
    $olBase = [ordered]@{
        Layout = '"per-slot"'
        Slots = '1'
        Window = '1'
        Recoverers = 'Set(0)'
        Readers = 'Set(0)'
        Fetchers = 'Set(0)'
        Mutation = '"none"'
    }
    $olS2K1 = With $olBase @{ Slots = '2'; Window = '1' }
    $olS2K2 = With $olBase @{ Slots = '2'; Window = '2' }
    $olS2K2NoReader = With $olS2K2 @{ Readers = 'Set()' }
    $olS2K2Core = With $olS2K2 @{ Readers = 'Set()'; Fetchers = 'Set()' }
    $olConcurrent = With $olBase @{ Recoverers = 'Set(0, 1)'; Readers = 'Set()' }
    $olConcurrentK2 = With $olS2K2Core @{ Recoverers = 'Set(0, 1)' }

    $olAppendBase = With $olBase @{ Layout = '"append"' }
    $olAppendS2K2 = With $olS2K2 @{ Layout = '"append"' }
    $olAppendS2K2NoReader = With $olS2K2NoReader @{ Layout = '"append"' }
    $olAppendS2K2Core = With $olS2K2Core @{ Layout = '"append"' }
    $olAppendConcurrent = With $olConcurrent @{ Layout = '"append"' }
    $olAppendConcurrentK2 = With $olConcurrentK2 @{ Layout = '"append"' }

    $olInvariants = @(
        'TypeOK',
        'AcknowledgedSurvive',
        'NoAcknowledgementAtOrAboveEnd',
        'DecidedBelowEnd',
        'EndSealed',
        'SealsPermanent',
        'RecoveriesAgree',
        'NoRevokedAnswer',
        'OpenCheckSound',
        'CheckTailBelowEnd',
        'BelowTailDecided',
        'NoFalseCorruption',
        'OnlySweeperDeletes',
        'StoreBatchesIssued',
        'AppendExtentShape',
        'RefinesAtomicLedger'
    )

    Invoke-Case 'object-ledger-s1-k1' 'ObjectLedger' $olBase $olInvariants
    Invoke-Case 'object-ledger-s2-k1' 'ObjectLedger' $olS2K1 $olInvariants
    Invoke-Case 'object-ledger-s2-k2-core' 'ObjectLedger' $olS2K2NoReader $olInvariants
    Invoke-Case 'object-ledger-s1-k1-concurrent' 'ObjectLedger' $olConcurrent $olInvariants
    Invoke-Case 'object-ledger-s2-k2-concurrent' 'ObjectLedger' $olConcurrentK2 $olInvariants

    Invoke-Case 'object-ledger-append-s1-k1' 'ObjectLedger' $olAppendBase $olInvariants
    Invoke-Case 'object-ledger-append-s2-k2-core' 'ObjectLedger' $olAppendS2K2NoReader $olInvariants
    Invoke-Case 'object-ledger-append-s1-k1-concurrent' 'ObjectLedger' $olAppendConcurrent $olInvariants
    Invoke-Case 'object-ledger-append-s2-k2-concurrent' 'ObjectLedger' $olAppendConcurrentK2 $olInvariants

    $olMutants = @(
        @('seal-unconditional', $olBase, 'AcknowledgedSurvive'),
        @('seal-unconditional', $olBase, 'RefinesAtomicLedger'),
        @('check-without-marker', $olBase, 'OpenCheckSound'),
        @('check-without-marker', $olBase, 'RefinesAtomicLedger'),
        @('drop-without-marker', $olBase, 'NoFalseCorruption'),
        @('drop-without-marker', $olConcurrent, 'RecoveriesAgree'),
        @('recover-from-gap', $olS2K2NoReader, 'DecidedBelowEnd'),
        @('recover-from-gap', $olS2K2NoReader, 'NoFalseCorruption'),
        @('drop-deletes-seal', $olBase, 'NoAcknowledgementAtOrAboveEnd'),
        @('drop-deletes-seal', $olBase, 'RefinesAtomicLedger'),
        @('read-ignores-drop', $olBase, 'NoFalseCorruption'),
        @('check-without-marker', $olAppendBase, 'OpenCheckSound', 'append-check-without-marker'),
        @('check-without-marker', $olAppendBase, 'RefinesAtomicLedger', 'append-check-without-marker'),
        @('drop-without-marker', $olAppendBase, 'NoFalseCorruption', 'append-drop-without-marker'),
        @('read-ignores-drop', $olAppendBase, 'NoFalseCorruption', 'append-read-ignores-drop'),
        @('tombstone-appendable', $olAppendBase, 'NoAcknowledgementAtOrAboveEnd'),
        @('tombstone-appendable', $olAppendBase, 'RefinesAtomicLedger'),
        @('roll-before-written', $olAppendS2K2NoReader, 'AppendExtentShape'),
        @('roll-before-written', $olAppendS2K2NoReader, 'NoAcknowledgementAtOrAboveEnd'),
        @('roll-before-written', $olAppendS2K2NoReader, 'DecidedBelowEnd'),
        @('roll-before-written', $olAppendS2K2NoReader, 'RefinesAtomicLedger')
    )
    foreach ($case in $olMutants) {
        $label = if ($case.Count -ge 4) { $case[3] } else { $case[0] }
        Invoke-Case "object-ledger-mutant-$label-$($case[2])" 'ObjectLedger' (Mutant $case[1] $case[0]) `
            -ExpectedInvariant $case[2]
    }

    $olWitnesses = @(
        @('NeverLateSealLanded', $olBase),
        @('NeverReadSeesLateSeal', $olBase),
        @('NeverStragglerSwept', $olBase),
        @('NeverRecoveryDropped', $olConcurrent),
        @('NeverBothRecoveriesFinish', $olConcurrent),
        @('NeverRecoveryLoops', $olS2K1),
        @('NeverAckAfterSealed', $olS2K2Core),
        @('NeverClosedAboveAcked', $olBase),
        @('NeverAcknowledgedAfterClose', $olBase),
        @('NeverDataAboveEnd', $olS2K2Core),
        @('AppendNeverRecoveryFollowedRoll', $olAppendS2K2NoReader, 'NeverRecoveryFollowedRoll'),
        @('AppendNeverRolledAndTombstoned', $olAppendS2K2NoReader, 'NeverRolledAndTombstoned'),
        @('AppendNeverCoalescedAppend', $olAppendS2K2NoReader, 'NeverCoalescedAppend'),
        @('AppendNeverRecoveryDropped', $olAppendConcurrent, 'NeverRecoveryDropped'),
        @('AppendNeverBothRecoveriesFinish', $olAppendConcurrent, 'NeverBothRecoveriesFinish'),
        @('AppendNeverAckAfterSealed', $olAppendS2K2Core, 'NeverAckAfterSealed'),
        @('AppendNeverClosedAboveAcked', $olAppendBase, 'NeverClosedAboveAcked'),
        @('AppendNeverAcknowledgedAfterClose', $olAppendBase, 'NeverAcknowledgedAfterClose')
    )
    foreach ($case in $olWitnesses) {
        $inv = if ($case.Count -ge 3) { $case[2] } else { $case[0] }
        Invoke-Case "object-ledger-witness-$($case[0])" 'ObjectLedger' $case[1] `
            -ExpectedInvariant $inv
    }

    if ($Extended) {
        Invoke-Case 'object-ledger-s2-k2' 'ObjectLedger' $olS2K2 $olInvariants
        Invoke-Case 'object-ledger-s2-k2-concurrent-reader-fetcher' 'ObjectLedger' `
            (With $olConcurrentK2 @{ Readers = 'Set(0)'; Fetchers = 'Set(0)' }) $olInvariants
        Invoke-Case 'object-ledger-s3-k2' 'ObjectLedger' `
            (With $olS2K2NoReader @{ Slots = '3'; Window = '2' }) $olInvariants
        Invoke-Case 'object-ledger-append-s2-k2' 'ObjectLedger' $olAppendS2K2 $olInvariants
        Invoke-Case 'object-ledger-append-s3-k2' 'ObjectLedger' `
            (With $olAppendS2K2NoReader @{ Slots = '3'; Window = '2' }) $olInvariants
    }

    # LedgerDrop: one final-window address of a closed ledger through recovery, drain, removal from the
    # ledger map, and drops, with delayed writer writes, seals, and recovery writes, over nodes which create a
    # replica unless they hold its ledger's tombstone, and whose reconciliation drops each replica their view of the
    # map revokes, recording the tombstone in the same catalog write. Views start before the split which installed
    # the ledgers.
    $deletion = [ordered]@{ Replicas = 'Set(0, 1, 2)'; Quorum = 2; Mutation = '"none"'; Crashes = 'true' }
    $deletionInvariants = @('DecisionsAgree', 'DurableIsDecided', 'FencedHasNoEffect', 'EndAddressSealed',
        'NeverDropsListed', 'TombstoneExcludesReplica', 'DroppedIsFinal')
    Invoke-Case 'deletion' 'LedgerDrop' $deletion $deletionInvariants

    foreach ($case in @(
        @('drop-keeps-replica', 'DurableIsDecided'),
        @('drop-keeps-replica', 'EndAddressSealed'),
        @('no-tombstone', 'DurableIsDecided'),
        @('no-tombstone', 'EndAddressSealed'),
        @('non-atomic-drop', 'DurableIsDecided'),
        @('non-atomic-drop', 'EndAddressSealed'),
        @('split-tombstone', 'DurableIsDecided'),
        @('split-tombstone', 'EndAddressSealed'),
        @('drop-volatile', 'DroppedIsFinal'),
        @('revoke-unlisted', 'NeverDropsListed'),
        @('count-dropped', 'DurableIsDecided'),
        @('short-coverage', 'DecisionsAgree'),
        @('ambiguous-as-fenced', 'FencedHasNoEffect'))) {
        Invoke-Case "deletion-mutant-$($case[0])-$($case[1])" 'LedgerDrop' (Mutant $deletion $case[0]) `
            -ExpectedInvariant $case[1]
    }

    # Controls: without crashes, requests wait at the gate between a split drop's two catalog writes, so the split
    # mutants hold every property, and their counterexamples above each pass through a crash.
    foreach ($mutation in @('non-atomic-drop', 'split-tombstone')) {
        Invoke-Case "deletion-control-$mutation-without-crashes" 'LedgerDrop' `
            (With (Mutant $deletion $mutation) @{ Crashes = 'false' }) $deletionInvariants
    }


    # Witnesses: a lower ledger's first write succeeds on a node which dropped a higher one, which a creation floor
    # at the highest dropped ledger ID would refuse; a write delayed past the drop creates a stray replica on a node
    # which held none, and that node's reconciliation drops it again; the writer is answered Dropped.
    foreach ($witness in @('NeverLowerCreatedAfterDrop', 'NeverStrayAfterRemoval', 'NeverStrayDroppedAgain',
            'NeverWriterAnsweredDropped')) {
        Invoke-Case "deletion-witness-$witness" 'LedgerDrop' $deletion -ExpectedInvariant $witness
    }

    if ($Extended) {
        # Control: a drop follows the close, so the state a crash restores under the drop-volatile mutant is the node's
        # state before the drop, and the mutant holds every property but DroppedIsFinal.
        Invoke-Case 'deletion-control-drop-volatile' 'LedgerDrop' (Mutant $deletion 'drop-volatile') `
            ($deletionInvariants | Where-Object { $_ -ne 'DroppedIsFinal' })
        Invoke-Case 'deletion-5' 'LedgerDrop' (With $deletion @{ Replicas = 'Set(0, 1, 2, 3, 4)'; Quorum = 3 }) `
            $deletionInvariants
    }

    # LedgerLease: L2 ledger-replica quorum read lease with bounded monotonic clock-rate drift, RAM-only
    # replica grants and bootLeaseHorizon across crash/restart, sealing leaseRemaining wait before LedgerMap CAS
    # (ReplaceTakenOver, Split, Merge), and cooperative PrepareRelease permanent surrender (0 ms handoff).
    $lease = [ordered]@{
        Replicas = 'Set(0, 1, 2)'
        Quorum = 2
        VMin = 2
        VMax = 3
        LeaseDuration = 3
        GuardBand = 0
        MaxTime = 2
        MaxReqs = 1
        Mutation = '"none"'
    }
    $leaseInvariants = @('NoStaleLocalRead', 'SealedStaysSealed')
    Invoke-Case 'ledger-lease' 'LedgerLease' $lease $leaseInvariants
    Invoke-Case 'ledger-lease-guard-band' 'LedgerLease' (With $lease @{ GuardBand = 1 }) $leaseInvariants

    foreach ($mutation in @(
        'no-boot-horizon',
        'no-drift-divisor',
        'no-seal-wait',
        'no-surrender-before-release',
        'extend-after-surrender',
        'short-lease-quorum',
        'short-seal-coverage')) {
        Invoke-Case "ledger-lease-mutant-$mutation" 'LedgerLease' (Mutant $lease $mutation) `
            -ExpectedInvariant 'NoStaleLocalRead'
    }

    foreach ($witness in @(
        'NeverLocalRead',
        'NeverUnplannedWaitCommit',
        'NeverCooperativeZeroWaitCommit',
        'NeverRestartBeforeSeal')) {
        Invoke-Case "ledger-lease-witness-$witness" 'LedgerLease' $lease -ExpectedInvariant $witness
    }

    if ($Extended) {
        Invoke-Case 'ledger-lease-renewal' 'LedgerLease' (With $lease @{ MaxReqs = 2 }) $leaseInvariants
        Invoke-Case 'ledger-lease-drift-125' 'LedgerLease' `
            (With $lease @{ VMin = 4; VMax = 5; LeaseDuration = 5; GuardBand = 1; MaxTime = 2; MaxReqs = 1 }) $leaseInvariants
    }

    # LedgerMap: dyadic ledgers over KeyBits-bit keys, with takeovers, splits, sibling merges,
    # drains, trims at segment starts, and drops in each key's lineage order, readers which snapshot the ledger map
    # before the manifest, and each ledger's segments, defining and referencing rows, and token chain.
    $ledgerMap = [ordered]@{ KeyBits = 1; MaxLedgers = 4; MaxJsn = 1; MaxMarkers = 1; Mutation = '"none"' }
    $ledgerMapJsn = With $ledgerMap @{ MaxLedgers = 3; MaxJsn = 2 }
    $ledgerMapInvariants = @(
        'OneOwner', 'IdsDense', 'HistoryOrdered', 'AckedInHistory', 'DropsInLineageOrder', 'ReadsSeeAcked',
        'ReferencesResolve', 'TrimsAtSegmentStarts', 'TokensReplayPlan')
    Invoke-Case 'ledger-map' 'LedgerMap' $ledgerMap $ledgerMapInvariants
    Invoke-Case 'ledger-map-jsn-2' 'LedgerMap' $ledgerMapJsn $ledgerMapInvariants
    foreach ($case in @(
        @('no-close-overlap', $ledgerMap, 'OneOwner'),
        @('skip-id', $ledgerMap, 'IdsDense'),
        @('reuse-id', $ledgerMapJsn, 'HistoryOrdered'),
        @('own-ledger-only', $ledgerMap, 'HistoryOrdered'),
        @('no-seal', $ledgerMap, 'HistoryOrdered'),
        @('drop-undrained', $ledgerMap, 'AckedInHistory'),
        @('write-before-install', $ledgerMap, 'AckedInHistory'),
        @('drop-out-of-order', $ledgerMap, 'DropsInLineageOrder'),
        @('manifest-before-map', $ledgerMap, 'ReadsSeeAcked'),
        @('ignore-trimmed', $ledgerMap, 'ReadsSeeAcked'),
        @('withdrawn-defining-batch', $ledgerMap, 'ReferencesResolve'),
        @('trim-mid-segment', $ledgerMap, 'TrimsAtSegmentStarts'),
        @('trim-mid-segment', $ledgerMapJsn, 'ReferencesResolve'),
        @('token-from-wrong-parent', $ledgerMapJsn, 'TokensReplayPlan'),
        @('token-includes-fenced', $ledgerMapJsn, 'TokensReplayPlan'))) {
        Invoke-Case "ledger-map-mutant-$($case[0])-$($case[2])" 'LedgerMap' (Mutant $case[1] $case[0]) `
            -ExpectedInvariant $case[2]
    }

    # Witnesses: a listed ledger stores a referencing row, which needs a key's second write, and a marker.
    Invoke-Case 'ledger-map-witness-NeverReferencingRow' 'LedgerMap' $ledgerMapJsn -ExpectedInvariant 'NeverReferencingRow'
    Invoke-Case 'ledger-map-witness-NeverMarker' 'LedgerMap' $ledgerMap -ExpectedInvariant 'NeverMarker'

    # The hazard itself: the write lands in the ledger once a transition installs it, beside the former primary's
    # admission of the same JSN.
    Invoke-Case 'ledger-map-mutant-write-before-install-history' 'LedgerMap' (Mutant $ledgerMap 'write-before-install') `
        -ExpectedInvariant 'HistoryOrdered'

    if ($Extended) {
        Invoke-Case 'ledger-map-jsn-2-merges' 'LedgerMap' (With $ledgerMap @{ MaxJsn = 2 }) $ledgerMapInvariants
        Invoke-Case 'ledger-map-2-bits' 'LedgerMap' (With $ledgerMap @{ KeyBits = 2; MaxLedgers = 5 }) `
            $ledgerMapInvariants
    }

    # Archive: one key's history in a closed ledger and its open successor, each cut into segments, drained a whole closed
    # segment at a time and compacted by racing stateless workers, dropped from the map once complete, swept by condition,
    # and read from a map value and then a manifest snapshot: the prefix of the ledgers the map no longer lists, then each
    # listed ledger from slot 0. The open ledger's batches are decided a segment at a time, so its drains take only the
    # segments a decided start closes. The first history's closed ledger is one segment, and its successor two; the second
    # history moves the base with a Replace before the Expired, over two segments in each ledger; the third ends the
    # closed ledger with a marker, a segment which holds nothing else. The mutants run under the first history.
    $archive = [ordered]@{
        Ledger1 = '["A", "A"]'; Ledger2 = '["E", "A"]'; Starts1 = 'Set(0)'; Starts2 = 'Set(0, 1)'; MaxObjects = 4
        Mutation = '"none"'
    }
    $archiveReplace = With $archive @{ Ledger1 = '["A", "R"]'; Ledger2 = '["A", "E"]'; Starts1 = 'Set(0, 1)' }
    $archiveMarker = With $archive @{ Ledger1 = '["A", "M"]'; Starts1 = 'Set(0, 1)' }
    $archiveInvariants = @('ReferencedExist', 'FoldPreserved', 'WatermarksMonotone', 'WatermarksAtSegmentStarts',
        'NoDrainSuperseded', 'ReadsCorrect')
    Invoke-Case 'archive' 'Archive' $archive $archiveInvariants
    Invoke-Case 'archive-replace' 'Archive' $archiveReplace $archiveInvariants
    Invoke-Case 'archive-marker' 'Archive' $archiveMarker $archiveInvariants
    foreach ($case in @(
        @('sweep-drain', 'ReferencedExist'),
        @('sweep-compaction', 'ReferencedExist'),
        @('snapshot-before-list', 'ReferencedExist'),
        @('drop-ignoring-sections', 'FoldPreserved'),
        @('drop-ignoring-live', 'FoldPreserved'),
        @('stale-drain', 'WatermarksMonotone'),
        @('lsm-mid-segment-watermark', 'WatermarksAtSegmentStarts'),
        @('ignore-missing', 'ReadsCorrect'),
        @('hold-by-watermark', 'ReadsCorrect'),
        @('supersede-by-open', 'ReadsCorrect'),
        @('no-predates-check', 'ReadsCorrect'))) {
        Invoke-Case "archive-mutant-$($case[0])" 'Archive' (Mutant $archive $case[0]) -ExpectedInvariant $case[1]
    }

    # A drain inside a segment also leaves a racing drain of the whole segment superseded.
    Invoke-Case 'archive-mutant-lsm-mid-segment-watermark-superseded' 'Archive' `
        (Mutant $archive 'lsm-mid-segment-watermark') -ExpectedInvariant 'NoDrainSuperseded'

    if ($Extended) {
        Invoke-Case 'archive-objects-5' 'Archive' (With $archive @{ MaxObjects = 5 }) $archiveInvariants
        Invoke-Case 'archive-replace-objects-5' 'Archive' (With $archiveReplace @{ MaxObjects = 5 }) $archiveInvariants
    }

    # ArchiveLogs: one key's log over a closed ledger and its open successor, each cut into segments, drained a whole
    # closed segment at a time by two racing drainers which may crash at any request, a crashed drainer's request landing
    # later beside its restarted worker, as Create, Append Block, and Put Blob actions, each failed condition classified
    # from one properties read; markers, drops in lineage order, the sweeper of checkpoints and markers, readers whose tail
    # may lag, and Journals' loader. The first history appends twice in one segment, so a crash splits a run, then opens a
    # generation, and its successor appends and deletes; the second passes a segment of markers alone and one of another
    # key's rows, and its successor replaces and appends in one segment; the third opens three generations, so the
    # sweeper deletes below a current one beside an opener in progress; the fourth puts an opener, with an append after it,
    # in the open ledger's first segment, which drains while the ledger is open, and a reader whose tail lies inside it
    # sees the opener alone. Each mutant runs under the smallest history which reaches its counterexample.
    $logs = [ordered]@{
        Ledger1 = '["A", "A", "R"]'; Ledger2 = '["A", "E"]'; Starts1 = 'Set(0, 2)'; Starts2 = 'Set(0, 1)'; Drainers = 2
        Sweeping = 'true'; Loading = 'true'; Stragglers = 'false'; Mutation = '"none"'
    }
    $logsMarkers = With $logs @{ Ledger1 = '["A", "M", "O"]'; Starts1 = 'Set(0, 1, 2)'; Ledger2 = '["R", "A"]'; Starts2 = 'Set(0)' }
    $logsGenerations = With $logs @{ Ledger1 = '["R", "A", "R"]'; Starts1 = 'Set(0, 1, 2)'; Ledger2 = '["R"]'; Starts2 = 'Set(0)' }
    $logsOpenOpener = With $logs @{ Ledger1 = '["A"]'; Starts1 = 'Set(0)'; Ledger2 = '["R", "A", "A"]'; Starts2 = 'Set(0, 2)' }
    $logsInvariants = @('NoLostCommand', 'StaleReaders', 'ReadsAtTheirCut', 'NoUndecidedCommand', 'HoldsSound', 'ReferencedExist', 'VersionsUnique', 'NoFalseCorruption')
    Invoke-Case 'archive-logs' 'ArchiveLogs' $logs $logsInvariants
    Invoke-Case 'archive-logs-markers' 'ArchiveLogs' $logsMarkers $logsInvariants
    Invoke-Case 'archive-logs-generations' 'ArchiveLogs' $logsGenerations $logsInvariants
    Invoke-Case 'archive-logs-open-opener' 'ArchiveLogs' $logsOpenOpener $logsInvariants
    # A drainer's request may land after it crashed: the first history with the sweeper and the loader off, so the stray's
    # interleavings stay within a per-change run.
    $logsStragglers = With $logs @{ Loading = 'false'; Stragglers = 'true' }
    Invoke-Case 'archive-logs-stragglers' 'ArchiveLogs' $logsStragglers $logsInvariants
    function LogsHistory([string] $Ledger1, [string] $Starts1, [string] $Ledger2 = '[]', [string] $Starts2 = 'Set()') {
        return With $logs @{ Ledger1 = $Ledger1; Starts1 = $Starts1; Ledger2 = $Ledger2; Starts2 = $Starts2 }
    }

    foreach ($case in @(
        @('replace-over-newer', (LogsHistory '["R", "A"]' 'Set(0, 1)'), 'NoLostCommand'),
        @('checkpoint-overwrite', (LogsHistory '["R", "R"]' 'Set(0, 1)'), 'NoLostCommand'),
        @('delete-removes-log', (LogsHistory '["A", "E"]' 'Set(0, 1)'), 'NoLostCommand'),
        @('partial-segment-drain', (LogsHistory '["A", "A"]' 'Set(0)'), 'NoLostCommand'),
        @('segments-out-of-order', (LogsHistory '["A", "O"]' 'Set(0, 1)'), 'NoLostCommand'),
        @('wait-as-held', (LogsHistory '["A"]' 'Set(0)'), 'NoLostCommand'),
        @('complete-early', (LogsHistory '["A", "A"]' 'Set(0, 1)'), 'NoLostCommand'),
        @('404-as-held-without-properties-read', (LogsHistory '["A", "A"]' 'Set(0)'), 'NoLostCommand'),
        @('drain-past-tail', (LogsHistory '["A"]' 'Set(0)' '["A", "A", "M"]' 'Set(0, 2)'), 'NoUndecidedCommand'),
        @('append-without-generation-fence', (LogsHistory '["A", "R"]' 'Set(0, 1)'), 'HoldsSound'),
        @('append-without-offset-fence', (LogsHistory '["A", "A"]' 'Set(0, 1)'), 'HoldsSound'),
        @('first-run-as-opener', (LogsHistory '["A"]' 'Set(0)'), 'HoldsSound'),
        @('wal-before-checkpoint', (LogsHistory '["R"]' 'Set(0)'), 'ReferencedExist'),
        @('gc-at-or-above-current', (LogsHistory '["R"]' 'Set(0)'), 'ReferencedExist'),
        @('referenced-missing', (LogsHistory '["R", "R"]' 'Set(0, 1)'), 'ReferencedExist'),
        @('no-zero-pad', (LogsHistory '["R", "R", "R"]' 'Set(0, 1, 2)'), 'ReferencedExist'),
        @('rebase-with-live-rows', (LogsHistory '["A"]' 'Set(0)' '["O", "A"]' 'Set(0, 1)'), 'VersionsUnique'),
        @('rebase-from-map-after-archive', (LogsHistory '["A"]' 'Set(0)' '["O"]' 'Set(0)'), 'VersionsUnique'),
        @('rebase-on-stale-token', (LogsHistory '["A"]' 'Set(0)' '["O"]' 'Set(0)'), 'VersionsUnique'),
        @('lagging-read-as-linearizable', (LogsHistory '["A"]' 'Set(0)' '["R", "A", "A"]' 'Set(0, 2)'), 'ReadsAtTheirCut'),
        @('drain-ignores-lineage', (LogsHistory '["A"]' 'Set(0)' '["A"]' 'Set(0)'), 'NoFalseCorruption'))) {
        Invoke-Case "archive-logs-mutant-$($case[0])" 'ArchiveLogs' (Mutant $case[1] $case[0]) -ExpectedInvariant $case[2]
    }

    # Three drainers race the first history, its open ledger two segments long, with stragglers: exhaustively on every
    # check with the sweeper and the loader off, a seeded random run with both on, and, on the scheduled -Nightly leg,
    # exhaustively with the sweeper on.
    $logsDrainers = With $logs @{ Drainers = 3; Sweeping = 'false'; Loading = 'false'; Stragglers = 'true' }
    Invoke-Case 'archive-logs-drainers-3' 'ArchiveLogs' $logsDrainers $logsInvariants
    $logsDrainersAll = With $logs @{ Drainers = 3; Stragglers = 'true' }
    Invoke-Simulation 'archive-logs-drainers-3-sampled' 'ArchiveLogs' $logsDrainersAll $logsInvariants -Samples 2000 -Steps 60 -Seed 17
    if ($Nightly) {
        Invoke-Case 'archive-logs-drainers-3-sweeping' 'ArchiveLogs' (With $logsDrainers @{ Sweeping = 'true' }) $logsInvariants
    }
}
finally {
    if (-not $server.HasExited) {
        Stop-Process -Id $server.Id
    }

    $results | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $output 'results.json')
}

Write-Host "Checked $($results.Count) cases. Logs and counterexamples: $output"
exit 0
