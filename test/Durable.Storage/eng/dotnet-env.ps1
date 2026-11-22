# Dot-source before running Durable Storage's tests. On Linux, temporary files lie under test/Durable.Storage/artifacts/tmp:
# WriteAheadLog refuses a file system which serves its files through the page cache, as tmpfs, which many distributions
# mount at /tmp, does (wal.md §9). The repository's global.json selects the .NET SDK.
& {
    $storage = Split-Path $PSScriptRoot -Parent
    if ($IsLinux) {
        $env:TMPDIR = Join-Path $storage 'artifacts' 'tmp'
        $null = New-Item -ItemType Directory -Force -Path $env:TMPDIR
    }
}
