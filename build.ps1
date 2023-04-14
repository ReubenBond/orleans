# --------------------
# Orleans build script
# --------------------
param(
  [Parameter()]
  [string] $BuildConfiguration = $env:BuildConfiguration
)
. ./common.ps1

$scriptDir = Split-Path $script:MyInvocation.MyCommand.Path
$solution = Join-Path $scriptDir "Orleans.sln"

# Define build flags & config
if ($null -eq $env:BUILD_FLAGS)
{
    $env:BUILD_FLAGS = "/m /v:m"
}

if ($null -eq $BuildConfiguration)
{
    $BuildConfiguration = "Debug"
}

# Clear the 'Platform' env variable for this session, as it's a per-project setting within the build, and
# misleading value (such as 'MCD' in HP PCs) may lead to build breakage (issue: #69).
$Platform = $null

# Disable multilevel lookup https://github.com/dotnet/core-setup/blob/main/Documentation/design-docs/multilevel-sharedfx-lookup.md
 $DOTNET_MULTILEVEL_LOOKUP = 0

$BuildProperties = "/p:Configuration=$BuildConfiguration";

 # Set DateTime suffix for debug builds
 if ($BuildConfiguration -eq "Debug")
 {
    $dateSuffix = Get-Date -Format "yyyyMMddHHmm"
    $BuildProperties = $BuildProperties + " /p:VersionDateSuffix=$dateSuffix"
 }

Write-Output "===== Building $solution ====="

Install-Dotnet

if ($args[0] -ne "Pack")
{
    Write-Output "Build $BuildConfiguration =============================="
    Invoke-Dotnet -Command "restore" -Arguments "$env:BUILD_FLAGS /bl:${BuildConfiguration}-Restore.binlog ${BuildProperties} `"$solution`""
    Invoke-Dotnet -Command "build" -Arguments "$env:BUILD_FLAGS /bl:${BuildConfiguration}-Build.binlog ${BuildProperties} `"$solution`""
}

Write-Output "Package $BuildConfiguration ============================"
Invoke-Dotnet -Command "pack" -Arguments "--no-build --no-restore $BUILD_FLAGS /bl:${BuildConfiguration}-Pack.binlog ${BuildProperties} `"$solution`""

Write-Output "===== Build succeeded for $solution ====="