[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $PackageRoot,
    [Parameter(Mandatory)][ValidateSet('win-x64', 'linux-x64')][string] $RuntimeIdentifier,
    [Parameter(Mandatory)][string] $AssetsFile,
    [Parameter(Mandatory)][string] $RuntimeVersion,
    [string] $Dotnet = 'dotnet'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Reuse the pinned SDK's native apphost; it loads the nested assembly in the same
# process, without a second launcher service, runtime extraction or child GUI.
$sdkVersion = (& $Dotnet --version).Trim()

if ($LASTEXITCODE -ne 0) {
    throw 'Could not resolve the pinned SDK for apphost creation.'
}

$sdkLines = @(& $Dotnet --list-sdks)

if ($LASTEXITCODE -ne 0) {
    throw 'Could not list SDK locations.'
}

$sdkPattern = '^' + [Regex]::Escape($sdkVersion) + ' \[(.+)\]$'
$sdkLocation = @($sdkLines | Where-Object { $_ -match $sdkPattern })

if ($sdkLocation.Count -ne 1 -or $sdkLocation[0] -notmatch $sdkPattern) {
    throw 'The pinned SDK location is ambiguous.'
}

$modelPath = Join-Path $Matches[1] "$sdkVersion/Microsoft.NET.HostModel.dll"
$dotnetRoot = [IO.Path]::GetFullPath((Join-Path $Matches[1] '..'))
Add-Type -Path $modelPath
$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json
$templateName = if ($RuntimeIdentifier -eq 'win-x64') { 'apphost.exe' } else { 'apphost' }
$packageTemplates = @($assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
    $candidate = Join-Path $_ "microsoft.netcore.app.host.$RuntimeIdentifier/$RuntimeVersion/runtimes/$RuntimeIdentifier/native/$templateName"

    if (Test-Path -LiteralPath $candidate -PathType Leaf) {
        $candidate
    }
})
$sdkTemplate = Join-Path $dotnetRoot "packs/Microsoft.NETCore.App.Host.$RuntimeIdentifier/$RuntimeVersion/runtimes/$RuntimeIdentifier/native/$templateName"
$templates = @(@($sdkTemplate) + $packageTemplates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Sort-Object -Unique)

if (-not $templates.Count) {
    throw 'The pinned native apphost template is absent from both SDK packs and restored packages.'
}

$templateHashes = @($templates | ForEach-Object { (Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash } | Sort-Object -Unique)

if ($templateHashes.Count -ne 1) {
    throw 'Conflicting pinned native apphost templates were found; no template was selected.'
}

$executableName = if ($RuntimeIdentifier -eq 'win-x64') { 'MovieSift.exe' } else { 'MovieSift' }
[Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost($templates[0], (Join-Path $PackageRoot $executableName),
    'app/MovieSift.dll', ($RuntimeIdentifier -eq 'win-x64'), (Join-Path $PackageRoot 'app/MovieSift.dll'), $false, $false, $null)
