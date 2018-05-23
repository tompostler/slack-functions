<#
.DESCRIPTION
Given a blob storage container uri and sas token, will patch a parameters file to have a proper package link.

.PARAMETER ParamFile
The parameter file to patch.

.PARAMETER BlobUri
The blob uri.

.PARAMETER BlobSas
The blob sas key.
#>

param (
    [string]$ParamFile,
    [string]$BlobUri,
    [string]$BlobSas
)

# Get things and update path
$content = Get-Content $ParamFile | ConvertFrom-Json
$uri = "$BlobUri/ApiFunctionsApp.zip$BlobSas"
$content.parameters.packageLink.value = $uri

# Unescape things and write out
$content = $content | ConvertTo-Json -Depth 20 | ForEach-Object { [System.Text.RegularExpressions.Regex]::Unescape($_) }
$content | Out-File $ParamFile

Write-Host "Updated '$ParamFile' with '$uri'"
