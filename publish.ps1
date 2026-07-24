[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Kept as a PowerShell entry point for existing operators. Packaging is
# intentionally centralized in package.ps1 so every official build is FDD + ZIP.
& (Join-Path $root 'package.ps1')
exit $LASTEXITCODE
