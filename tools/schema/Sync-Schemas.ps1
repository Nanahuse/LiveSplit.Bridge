param(
    [switch]$Check
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$generatedDirectory = Join-Path $PSScriptRoot 'out'
$schemaDirectory = Join-Path $repositoryRoot 'schemas/v4'
$schemaNames = @(
    'rpc-request.schema.json',
    'rpc-response.schema.json',
    'bridge-event.schema.json'
)

foreach ($name in $schemaNames) {
    $generatedPath = Join-Path $generatedDirectory $name
    $checkedInPath = Join-Path $schemaDirectory $name
    if (-not (Test-Path -LiteralPath $generatedPath -PathType Leaf)) {
        throw "Generated schema is missing: $generatedPath"
    }

    if ($Check) {
        if (-not (Test-Path -LiteralPath $checkedInPath -PathType Leaf)) {
            throw "Checked-in schema is missing: $checkedInPath"
        }

        $generatedBytes = [System.IO.File]::ReadAllBytes($generatedPath)
        $checkedInBytes = [System.IO.File]::ReadAllBytes($checkedInPath)
        $matches = $generatedBytes.Length -eq $checkedInBytes.Length
        for ($index = 0; $matches -and $index -lt $generatedBytes.Length; $index++) {
            $matches = $generatedBytes[$index] -eq $checkedInBytes[$index]
        }

        if (-not $matches) {
            throw "Generated schema differs byte-for-byte from $checkedInPath"
        }

        Write-Output "MATCH $name"
    }
    else {
        Copy-Item -LiteralPath $generatedPath -Destination $checkedInPath -Force
        Write-Output "UPDATED $checkedInPath"
    }
}
