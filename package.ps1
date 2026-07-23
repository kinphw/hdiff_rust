[CmdletBinding()]
param(
    [switch]$FrameworkDependent
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$propsFile = Join-Path $root 'Directory.Build.props'
$project = Join-Path $root 'src\Hdiff.UI\Hdiff.UI.csproj'
$publishRoot = Join-Path $root 'publish'

if (-not (Test-Path -LiteralPath $propsFile)) {
    throw "Version source not found: $propsFile"
}

$props = [xml](Get-Content -LiteralPath $propsFile -Raw)
$versionNode = $props.SelectSingleNode('//Version')
$version = if ($null -eq $versionNode) { $null } else { $versionNode.InnerText.Trim() }
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "<Version> is required in $propsFile"
}
if ($version -notmatch '^[0-9A-Za-z][0-9A-Za-z.-]*$') {
    throw "Version contains an unsafe filename character: $version"
}

$flavor = if ($FrameworkDependent) { 'fdd' } else { 'self-contained' }
$packageName = "Hdiff-v$version-win-x64-$flavor"
$publishDirectory = Join-Path $publishRoot $flavor
$zipPath = Join-Path $publishRoot "$packageName.zip"
$temporaryPublishDirectory = $false

# Do not force an operator to close an already-running package just to produce a
# newer ZIP. When the standard staging EXE is locked, publish into a controlled
# temporary folder, create the ZIP, and remove that temporary folder afterwards.
$existingExecutable = Join-Path $publishDirectory 'Hdiff.exe'
if (Test-Path -LiteralPath $existingExecutable) {
    $lockProbe = $null
    try {
        $lockProbe = [System.IO.File]::Open(
            $existingExecutable,
            [System.IO.FileMode]::Open,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
    }
    catch [System.IO.IOException] {
        $publishDirectory = Join-Path $publishRoot ".staging-$packageName"
        $temporaryPublishDirectory = $true
        Write-Warning "The existing $flavor package is running. Building this ZIP from an isolated staging folder."
    }
    finally {
        if ($null -ne $lockProbe) { $lockProbe.Dispose() }
    }
}

# Do not remove the publish directory itself: an operator may have a CMD window
# open in it while rebuilding. The single-file publish layout has only these
# controlled outputs, which are safe to replace individually.
try {
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    foreach ($fileName in @('Hdiff.exe', 'README.md')) {
        $oldFile = Join-Path $publishDirectory $fileName
        if (Test-Path -LiteralPath $oldFile) {
            Remove-Item -LiteralPath $oldFile -Force
        }
    }
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    $selfContained = if ($FrameworkDependent) { 'false' } else { 'true' }
    Write-Host "[Hdiff] Publishing v$version ($flavor)..." -ForegroundColor Cyan
    $publishArguments = @(
        'publish', $project,
        '-c', 'Release',
        '-r', 'win-x64',
        "-p:SelfContained=$selfContained",
        '-o', $publishDirectory
    )
    if (-not $FrameworkDependent) {
        # Single-file bundle compression is supported only by self-contained publishing.
        $publishArguments += '-p:EnableCompressionInSingleFile=true'
    }
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $publishDirectory 'README.md') -Force
    Compress-Archive -LiteralPath (Get-ChildItem -LiteralPath $publishDirectory -File | Select-Object -ExpandProperty FullName) `
        -DestinationPath $zipPath `
        -CompressionLevel Optimal

    $zipSizeMb = [math]::Round((Get-Item -LiteralPath $zipPath).Length / 1MB, 1)
    Write-Host "" 
    Write-Host "[Hdiff] Package ready: $zipPath ($zipSizeMb MB)" -ForegroundColor Green
}
finally {
    if ($temporaryPublishDirectory -and (Test-Path -LiteralPath $publishDirectory)) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force
    }
}
