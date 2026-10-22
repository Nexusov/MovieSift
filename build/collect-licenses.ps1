[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $AssetsFile,
    [Parameter(Mandatory)]
    [string] $Destination,
    [string[]] $AdditionalPackages = @(),
    [string] $CacheDirectory = (Join-Path $PSScriptRoot '../artifacts/downloads/license-materials')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$assets = Get-Content -LiteralPath $AssetsFile -Raw | ConvertFrom-Json -AsHashtable
$packageFolders = @($assets.packageFolders.Keys)
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
$inventory = [Collections.Generic.List[object]]::new()
$packageEntries = $assets.libraries.Clone()
$materials = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'license-materials.lock.json') -Raw | ConvertFrom-Json -AsHashtable
New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null

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
        $packageMetadata = Get-Content -LiteralPath (Join-Path $packagePath '.nupkg.metadata') -Raw | ConvertFrom-Json
        $packageHash = $packageMetadata.contentHash
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
    $repository = $metadata.SelectSingleNode('*[local-name()="repository"]')
    Copy-Item -LiteralPath $specification[0].FullName -Destination $packageDestination
    $licenseFiles = @(Get-ChildItem -LiteralPath $packagePath -File -Recurse | Where-Object Name -Match '^(license|licence|copying|notice|third[-_. ]?party)')

    if ($license -and $license.GetAttribute('type') -eq 'file') {
        $declaredLicense = [IO.Path]::GetFullPath((Join-Path $packagePath $license.InnerText))
        $packagePrefix = [IO.Path]::GetFullPath($packagePath) + [IO.Path]::DirectorySeparatorChar
        $validDeclaredLicense = $declaredLicense.StartsWith($packagePrefix, [StringComparison]::Ordinal) -and (Test-Path -LiteralPath $declaredLicense -PathType Leaf)

        if (-not $validDeclaredLicense) {
            throw "The declared license file is missing or outside $($entry.Key)."
        }

        $licenseFiles = @(@($licenseFiles) + (Get-Item -LiteralPath $declaredLicense) | Sort-Object FullName -Unique)
    }

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
    $repositoryLocation = ''
    $repositoryCommit = ''

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

    if ($repository) {
        $repositoryLocation = $repository.GetAttribute('url')
        $repositoryCommit = $repository.GetAttribute('commit')
    }

    $supplementalMaterials = @()

    foreach ($material in $materials.materials) {
        $matchesPackage = $material.packageIds -contains $packageName -and $material.version -eq $packageVersion

        if (-not $matchesPackage) {
            continue
        }

        if ($repositoryCommit -ne $material.repositoryCommit) {
            throw "Supplemental license commit does not match the restored nuspec: $($entry.Key)."
        }

        $cachedMaterial = Join-Path $CacheDirectory $material.id

        if (-not (Test-Path -LiteralPath $cachedMaterial -PathType Leaf)) {
            Invoke-WebRequest -Uri $material.url -OutFile $cachedMaterial
        }

        $actualHash = (Get-FileHash -LiteralPath $cachedMaterial -Algorithm SHA256).Hash.ToLowerInvariant()

        if ($actualHash -ne $material.sha256) {
            throw "Supplemental license hash mismatch: $($material.id)."
        }

        $targetPath = Join-Path $packageDestination $material.fileName
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($targetPath)) -Force | Out-Null
        Copy-Item -LiteralPath $cachedMaterial -Destination $targetPath
        $supplementalMaterials += [ordered]@{
            path = $material.fileName
            url = $material.url
            sha256 = $actualHash
            kind = $material.kind
            evidence = $material.evidence
        }
    }

    $inventory.Add([ordered]@{
        id = $packageName
        version = $packageVersion
        sha512 = $packageHash
        licenseType = $licenseType
        license = $licenseValue
        licenseUrl = $licenseLocation
        projectUrl = $projectLocation
        repositoryUrl = $repositoryLocation
        repositoryCommit = $repositoryCommit
        specificationFile = $specification[0].Name
        includedLicenseFiles = @($licenseFiles | ForEach-Object { [IO.Path]::GetRelativePath($packagePath, $_.FullName).Replace('\', '/') })
        supplementalMaterials = $supplementalMaterials
    })
}

$inventory | Sort-Object id, version | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $Destination 'inventory.json') -Encoding utf8NoBOM
