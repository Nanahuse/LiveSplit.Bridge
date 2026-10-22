param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[A-Za-z0-9._-]+$')]
    [string]$PackageName,

    [string]$OutputDirectory = 'artifacts'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repositoryRoot 'src/LiveSplit.Bridge/LiveSplit.Bridge.csproj'
$buildOutputPath = & dotnet msbuild $projectPath `
    -property:Configuration=Release `
    -getProperty:TargetPath `
    -nologo
if ($LASTEXITCODE -ne 0) {
    throw 'Could not read the Release assembly path.'
}
$buildOutputPath = ($buildOutputPath -join [Environment]::NewLine).Trim()
if (-not (Test-Path -LiteralPath $buildOutputPath -PathType Leaf)) {
    throw "Expected Release assembly was not produced: $buildOutputPath"
}

$outputPath = if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $OutputDirectory
}
else {
    Join-Path $repositoryRoot $OutputDirectory
}
$archivePath = Join-Path $outputPath "$PackageName.zip"
$checksumPath = Join-Path $outputPath 'SHA256SUMS.txt'

New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
Compress-Archive -LiteralPath $buildOutputPath -DestinationPath $archivePath -Force
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    if ($archive.Entries.Count -ne 1 -or $archive.Entries[0].FullName -cne 'LiveSplit.Bridge.dll' -or $archive.Entries[0].Length -eq 0) {
        throw 'Release ZIP must contain exactly one nonempty LiveSplit.Bridge.dll.'
    }
}
finally {
    $archive.Dispose()
}

$hash = Get-FileHash -LiteralPath $archivePath -Algorithm SHA256
"$($hash.Hash.ToLowerInvariant())  $([System.IO.Path]::GetFileName($archivePath))" |
    Set-Content -LiteralPath $checksumPath -Encoding ascii

Write-Output 'Verified distribution: LiveSplit.Bridge.dll only'
Write-Output "Created $archivePath"
Write-Output "Created $checksumPath"
