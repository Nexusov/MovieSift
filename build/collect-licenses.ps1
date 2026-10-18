[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $AssetsFile,
    [Parameter(Mandatory)]
    [string] $Destination,
    [string[]] $AdditionalPackages = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json -AsHashtable
$packageFolders = @($assets.packageFolders.Keys)
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$inventory = [Collections.Generic.List[object]]::new()
$packageEntries = $assets.libraries.Clone()

foreach ($additionalPackage in $AdditionalPackages) {

    if (-not $packageEntries.ContainsKey($additionalPackage)) {
        $packageEntries[$additionalPackage] = @{ type = 'package'; path = $additionalPackage.ToLowerInvariant(); sha512 = '' }
    }
}

foreach ($entry in $packageEntries.GetEnumerator()) {

    if ($entry.Value.type -ne 'package') {
        continue
    }

    $packageName, $packageVersion = $entry.Key.Split('/')
    $packagePath = $null

    foreach ($packageFolder in $packageFolders) {
        $candidate = Join-Path $packageFolder $entry.Value.path

        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $packagePath = $candidate
            break
        }
    }

    if (-not $packagePath) {
        throw "The restored package is missing: $($entry.Key)"
    }

    $packageHash = $entry.Value.sha512

    if (-not $packageHash) {
        $hashPath = Join-Path $packagePath ($packageName.ToLowerInvariant() + '.' + $packageVersion + '.nupkg.sha512')
        $packageHash = (Get-Content -LiteralPath $hashPath -Raw).Trim()
    }

    $packageDestination = Join-Path $Destination $entry.Key
    New-Item -ItemType Directory -Path $packageDestination -Force | Out-Null
    $specification = @(Get-ChildItem -LiteralPath $packagePath -Filter '*.nuspec' -File)

    if ($specification.Count -ne 1) {
        throw "Expected one package specification in $packagePath."
    }

    $readerSettings = [Xml.XmlReaderSettings]::new()
    $readerSettings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
    $readerSettings.XmlResolver = $null
    $reader = [Xml.XmlReader]::Create($specification[0].FullName, $readerSettings)

    try {
        $document = [Xml.XmlDocument]::new()
        $document.XmlResolver = $null
        $document.Load($reader)
    }
    finally {
        $reader.Dispose()
    }

    $metadata = $document.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    $license = $metadata.SelectSingleNode('*[local-name()="license"]')
    $licenseUrl = $metadata.SelectSingleNode('*[local-name()="licenseUrl"]')
    $projectUrl = $metadata.SelectSingleNode('*[local-name()="projectUrl"]')
    Copy-Item -LiteralPath $specification[0].FullName -Destination $packageDestination
    $licenseFiles = @(Get-ChildItem -LiteralPath $packagePath -File -Recurse | Where-Object Name -Match '^(license|licence|notice|third[-_. ]?party)')

    foreach ($licenseFile in $licenseFiles) {
        $relativePath = [IO.Path]::GetRelativePath($packagePath, $licenseFile.FullName)
        $targetPath = Join-Path $packageDestination $relativePath
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($targetPath)) -Force | Out-Null
        Copy-Item -LiteralPath $licenseFile.FullName -Destination $targetPath
    }

    $licenseValue = ''
    $licenseType = ''
    $licenseLocation = ''
    $projectLocation = ''

    if ($license) {
        $licenseValue = $license.InnerText
        $licenseType = $license.GetAttribute('type')
    }

    if ($licenseUrl) {
        $licenseLocation = $licenseUrl.InnerText
    }

    if ($projectUrl) {
        $projectLocation = $projectUrl.InnerText
    }

    $inventory.Add([ordered]@{
        id = $packageName
        version = $packageVersion
        sha512 = $packageHash
        licenseType = $licenseType
        license = $licenseValue
        licenseUrl = $licenseLocation
        projectUrl = $projectLocation
        includedLicenseFiles = @($licenseFiles | ForEach-Object { [IO.Path]::GetRelativePath($packagePath, $_.FullName).Replace('\', '/') })
    })
}

$inventory | Sort-Object id, version | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Destination 'inventory.json') -Encoding utf8NoBOM
