<#
.SYNOPSIS
Checks the repository's Markdown: style and syntax (markdownlint), relative links and heading anchors
(remark-validate-links), and that every Mermaid diagram parses.
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path $PSScriptRoot -Parent
$source = Join-Path $PSScriptRoot 'docs'
$tools = Join-Path $root 'artifacts' 'tools' 'docs'
$null = New-Item -ItemType Directory -Force $tools
$null = Get-Command node, npm, git -ErrorAction Stop

# The checkers are pinned by test/Durable.Storage/eng/docs/package-lock.json and installed outside the source tree. The stamp records the
# lock a completed install matches; it is removed before installing, so an interrupted install reinstalls next time.
$lock = Join-Path $source 'package-lock.json'
$stamp = Join-Path $tools '.lock-sha256'
$modules = Join-Path $tools 'node_modules'
$lockHash = (Get-FileHash -LiteralPath $lock -Algorithm SHA256).Hash
$installed = (Test-Path -LiteralPath $stamp) -and (Test-Path -LiteralPath $modules -PathType Container) -and
    (Get-Content -LiteralPath $stamp -Raw).Trim() -eq $lockHash
if (-not $installed) {
    Remove-Item -LiteralPath $stamp -Force -ErrorAction SilentlyContinue
    Copy-Item -LiteralPath (Join-Path $source 'package.json'), $lock -Destination $tools -Force
    Push-Location $tools
    try {
        # --engine-strict reports a Node.js release the pinned packages do not support.
        & npm ci --no-audit --no-fund --engine-strict --loglevel=error
        if ($LASTEXITCODE -ne 0) {
            throw 'npm ci failed for the pinned documentation checkers.'
        }
    }
    finally {
        Pop-Location
    }

    Set-Content -LiteralPath $stamp -Value $lockHash
}

# The scripts run from the tools directory, where their packages resolve.
Copy-Item -Path (Join-Path $source '*.mjs') -Destination $tools -Force

Push-Location $root
try {
    # Tracked and new, unignored Markdown files under test/Durable.Storage present in the working tree.
    $files = @(& git ls-files --cached --others --exclude-standard -- '*.md' | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) {
        throw 'git ls-files failed.'
    }

    $files = @($files | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })

    $failed = @()

    Write-Host "markdownlint: $($files.Count) files"
    & node (Join-Path $modules 'markdownlint-cli2' 'markdownlint-cli2-bin.mjs') @files
    if ($LASTEXITCODE -ne 0) { $failed += 'markdownlint' }

    Write-Host 'Links and anchors'
    & node (Join-Path $modules 'remark-cli' 'cli.js') @files `
        --rc-path (Join-Path $tools 'remark-links.mjs') --quiet --frail --no-stdout --no-config
    if ($LASTEXITCODE -ne 0) { $failed += 'links' }

    & node (Join-Path $tools 'check-mermaid.mjs') @files
    if ($LASTEXITCODE -ne 0) { $failed += 'Mermaid' }

    if ($failed.Count -gt 0) {
        throw "Documentation checks failed: $($failed -join ', ')."
    }

    Write-Host 'Documentation checks passed.'
}
finally {
    Pop-Location
}
