[CmdletBinding()]
param(
    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$flavor = if ($SelfContained) { 'self-contained' } else { 'fdd' }
$out = Join-Path $root (Join-Path 'publish' $flavor)

if (Test-Path -LiteralPath $out) {
    Remove-Item -LiteralPath $out -Recurse -Force
}

$selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
dotnet publish (Join-Path $root 'src\Hdiff.UI\Hdiff.UI.csproj') `
    -c Release `
    -r win-x64 `
    -p:SelfContained=$selfContainedValue `
    -o $out

Copy-Item -LiteralPath (Join-Path $root 'README.md') -Destination (Join-Path $out 'README.md') -Force

Get-ChildItem -LiteralPath $out | Select-Object Name, Length
