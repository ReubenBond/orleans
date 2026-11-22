#Requires -Version 7.3
<#
.SYNOPSIS
Runs the sample Orleans app over Clog: three silos over Azurite, with the Aspire dashboard (playground/DurableStorage/README.md).

.DESCRIPTION
Builds playground/DurableStorage/Orleans.Durable.Storage.Samples.AppHost in Release without restoring (just restore does) and runs it, which needs Docker for
Azurite. Each run is a new Clog cluster: the AppHost creates the silos' node roots anew under artifacts/sample. Ctrl+C
stops the AppHost, its silos, and Azurite.

.EXAMPLE
./eng/sample.ps1
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
. (Join-Path $PSScriptRoot 'dotnet-env.ps1')
$project = Join-Path (Split-Path $PSScriptRoot -Parent) '..' '..' 'playground' 'DurableStorage' 'Orleans.Durable.Storage.Samples.AppHost'
& dotnet run --project $project -c Release --no-restore
exit $LASTEXITCODE
