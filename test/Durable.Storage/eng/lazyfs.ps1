#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Runs the Linux LazyFS + SIGKILL multi-process crash and linearizability soak tests.
.DESCRIPTION
    Builds Orleans.Durable.Storage.Tests in Release and executes the explicit Category=LazyFs
    suite with CLOG_LAZYFS=1 so missing FUSE or LazyFS prerequisites fail rather than silently skip.
    Requires Linux with FUSE (/dev/fuse, fusermount3 or fusermount) and the lazyfs binary on PATH,
    in ~/.local/bin/lazyfs, or at CLOG_LAZYFS_BIN.
#>
[CmdletBinding()]
param(
    [ValidateRange(1, 50)]
    [int]$Rounds = 2,

    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $IsLinux) {
    throw 'LazyFS FUSE crash soak tests require Linux (with /dev/fuse and lazyfs installed).'
}

$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot 'dotnet-env.ps1')

$project = Join-Path $root 'Orleans.Durable.Storage.Tests/Orleans.Durable.Storage.Tests.csproj'
$env:CLOG_LAZYFS = '1'
$env:CLOG_LAZYFS_ROUNDS = "$Rounds"

# Keep Microsoft.Testing.Platform IPC sockets under /tmp so long worktree paths do not hit sun_path limits.
if ([string]::IsNullOrWhiteSpace($env:TESTINGPLATFORM_PIPE_DIRECTORY)) {
    $env:TESTINGPLATFORM_PIPE_DIRECTORY = '/tmp'
}

& dotnet build $project -c $Configuration --nologo
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$exe = Join-Path $root "Orleans.Durable.Storage.Tests/bin/$Configuration/net11.0/Orleans.Durable.Storage.Tests"
& $exe --filter-trait Category=LazyFs --explicit on
exit $LASTEXITCODE
