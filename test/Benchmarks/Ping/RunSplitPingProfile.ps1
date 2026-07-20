param(
    [Parameter(Mandatory = $true)]
    [string] $BenchmarkDll,
    [string] $OutputDirectory = "Artifacts\Benchmarks\Rpc\phases",
    [int] $Concurrency = 225,
    [double] $WarmupSeconds = 5,
    [double] $MeasurementSeconds = 30,
    [int] $Iterations = 1,
    [int] $TraceProbes = 1,
    [int64] $DriverAffinity = 0,
    [int64] $TargetAffinity = 0,
    [ValidateSet("Normal", "AboveNormal", "High")]
    [string] $Priority = "High"
)

$ErrorActionPreference = "Stop"
$sessionId = [Guid]::NewGuid().ToString("N")
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[IO.Directory]::CreateDirectory($outputDirectory) | Out-Null
$readyFile = Join-Path $outputDirectory "$sessionId.ready"
$targetOut = Join-Path $outputDirectory "$sessionId-target.out.log"
$targetErr = Join-Path $outputDirectory "$sessionId-target.err.log"
$driverOut = Join-Path $outputDirectory "$sessionId-driver.out.log"
$driverErr = Join-Path $outputDirectory "$sessionId-driver.err.log"

$target = $null
$driver = $null
try
{
    $target = Start-Process dotnet `
        -ArgumentList @($BenchmarkDll, "FixedPingTarget", $sessionId, $readyFile) `
        -RedirectStandardOutput $targetOut `
        -RedirectStandardError $targetErr `
        -PassThru

    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    while (-not (Test-Path $readyFile))
    {
        if ($target.HasExited)
        {
            throw "Target exited before becoming ready. See $targetErr"
        }

        if ([DateTime]::UtcNow -ge $deadline)
        {
            throw "Target readiness timed out. See $targetOut"
        }

        Start-Sleep -Milliseconds 50
        $target.Refresh()
    }

    if ($TargetAffinity -ne 0)
    {
        $target.ProcessorAffinity = [IntPtr]$TargetAffinity
    }
    $target.PriorityClass = $Priority

    $driver = Start-Process dotnet `
        -ArgumentList @(
            $BenchmarkDll,
            "FixedPingDriver",
            $sessionId,
            $Concurrency,
            $WarmupSeconds,
            $MeasurementSeconds,
            $Iterations,
            $TraceProbes) `
        -RedirectStandardOutput $driverOut `
        -RedirectStandardError $driverErr `
        -PassThru

    if ($DriverAffinity -ne 0)
    {
        $driver.ProcessorAffinity = [IntPtr]$DriverAffinity
    }
    $driver.PriorityClass = $Priority
    $driver.WaitForExit()
    if ($driver.ExitCode -ne 0)
    {
        throw "Driver exited with code $($driver.ExitCode). See $driverErr"
    }

    $target.WaitForExit(30000) | Out-Null
    if (-not $target.HasExited)
    {
        throw "Target did not stop after the driver completed."
    }

    Write-Host "Driver PID $($driver.Id), target PID $($target.Id)"
    Write-Host "Driver output: $driverOut"
    Write-Host "Target output: $targetOut"
}
finally
{
    if ($driver -and -not $driver.HasExited)
    {
        Stop-Process -Id $driver.Id
    }
    if ($target -and -not $target.HasExited)
    {
        Stop-Process -Id $target.Id
    }
    if (Test-Path $readyFile)
    {
        Remove-Item $readyFile
    }
}
