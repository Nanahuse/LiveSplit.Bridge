param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$PackageName,

    [string]$OutputDirectory = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src/LiveSplit.Bridge/LiveSplit.Bridge.csproj'
$propertiesJson = & dotnet msbuild $projectPath `
    -property:Configuration=Release `
    -getProperty:TargetDir,BridgeDistributionFiles `
    -nologo
if ($LASTEXITCODE -ne 0) {
    throw 'Could not read the Release output and BridgeDistributionFiles properties.'
}

$properties = ($propertiesJson -join [Environment]::NewLine) | ConvertFrom-Json
$buildOutputPath = $properties.Properties.TargetDir
$distributionFiles = @(
    $properties.Properties.BridgeDistributionFiles -split ';' |
        ForEach-Object { $_.Trim() } |
        Where-Object { $_.Length -gt 0 }
)
if ($distributionFiles.Count -eq 0) {
    throw 'BridgeDistributionFiles is empty.'
}
if (($distributionFiles | Select-Object -Unique).Count -ne $distributionFiles.Count) {
    throw 'BridgeDistributionFiles contains duplicate names.'
}

$tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
$stagingPath = [System.IO.Path]::GetFullPath((Join-Path $tempRoot $PackageName))
$tempRootPrefix = $tempRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
if ($PackageName -in @('.', '..') -or -not $stagingPath.StartsWith($tempRootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Staging path must be a child of the temporary directory: $stagingPath"
}

$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
}
else {
    Join-Path $repositoryRoot $OutputDirectory
}
$archivePath = Join-Path $outputPath "$PackageName.zip"
$checksumPath = Join-Path $outputPath 'SHA256SUMS.txt'

if (Test-Path -LiteralPath $stagingPath) {
    Remove-Item -LiteralPath $stagingPath -Recurse -Force
}
New-Item -ItemType Directory -Path $stagingPath, $outputPath -Force | Out-Null

foreach ($file in $distributionFiles) {
    if ([System.IO.Path]::GetFileName($file) -ne $file) {
        throw "Distribution entries must be file names: $file"
    }

    $sourcePath = Join-Path $buildOutputPath $file
    if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
        throw "Expected Release file was not produced: $sourcePath"
    }

    Copy-Item -LiteralPath $sourcePath -Destination $stagingPath
}

$expectedFiles = @($distributionFiles | Sort-Object)
$stagedFiles = @(
    Get-ChildItem -LiteralPath $stagingPath -File |
        ForEach-Object Name |
        Sort-Object
)
if (Compare-Object $expectedFiles $stagedFiles) {
    throw 'Release staging contents do not match BridgeDistributionFiles.'
}
if (Get-ChildItem -LiteralPath $stagingPath -Filter '*.pdb' -File) {
    throw 'Release staging must not contain PDB files.'
}

Compress-Archive -Path (Join-Path $stagingPath '*') -DestinationPath $archivePath -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $archiveFiles = @($archive.Entries | ForEach-Object FullName | Sort-Object)
    if (Compare-Object $expectedFiles $archiveFiles) {
        throw 'ZIP contents do not match BridgeDistributionFiles.'
    }
    if ($archiveFiles | Where-Object { $_.EndsWith('.pdb', [StringComparison]::OrdinalIgnoreCase) }) {
        throw 'Release ZIP must not contain PDB files.'
    }
}
finally {
    $archive.Dispose()
}

$hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
"$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($archivePath))" |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Output "Verified distribution files: $($expectedFiles -join ', ')"
Write-Output "Created $archivePath"
Write-Output "Created $checksumPath"
