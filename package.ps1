[CmdletBinding()]
param()

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

$flavor = 'fdd'
# $packageName = "Hdiff-v$version-win-x64-$flavor"
$packageName = "Hdiff-v$version"
$publishDirectory = Join-Path $publishRoot $flavor
$zipPath = Join-Path $publishRoot "$packageName.zip"
$temporaryPublishDirectory = $false

# Do not force an operator to close an already-running package just to produce a
# newer ZIP. Hdiff.exe is the UI and python.exe is the identical PDF DRM worker;
# either can be locked while the package is in use.
foreach ($existingFileName in @('Hdiff.exe', 'python.exe')) {
    $existingExecutable = Join-Path $publishDirectory $existingFileName
    if (-not (Test-Path -LiteralPath $existingExecutable)) { continue }
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
        break
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
    foreach ($fileName in @('Hdiff.exe', 'python.exe', 'README.md')) {
        $oldFile = Join-Path $publishDirectory $fileName
        if (Test-Path -LiteralPath $oldFile) {
            Remove-Item -LiteralPath $oldFile -Force
        }
    }
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Write-Host "[Hdiff] Publishing v$version ($flavor)..." -ForegroundColor Cyan
    $publishArguments = @(
        'publish', $project,
        '-c', 'Release',
        '-r', 'win-x64',
        '-p:SelfContained=false',
        '-o', $publishDirectory
    )
    & dotnet @publishArguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }

    # DocMine production proved that the target DRM permits protected PDF
    # reads only to a python.exe basename. Keep Hdiff.exe as the user-facing UI
    # and place an identical binary beside it solely for isolated PDF workers.
    Copy-Item -LiteralPath (Join-Path $publishDirectory 'Hdiff.exe') `
        -Destination (Join-Path $publishDirectory 'python.exe') -Force
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
