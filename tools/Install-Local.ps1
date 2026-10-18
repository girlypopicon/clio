#Requires -Version 5.1
<#
.SYNOPSIS
  First-time install of a CLIo release zip into your user profile, so it can update itself afterwards.

.DESCRIPTION
  Unpacks CLIo-<version>-win-x64.zip into %LOCALAPPDATA%\Programs\CLIo (a folder your account can write to,
  which self-updates need) and adds a Start Menu shortcut. Your data stays in %LOCALAPPDATA%\CLIo and is
  not touched. After this, updates come from inside CLIo.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Zip,
    [string]$Destination = (Join-Path $env:LOCALAPPDATA 'Programs\CLIo')
)

$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Zip)) { throw "Zip not found: $Zip" }

if (Test-Path (Join-Path $Destination 'CLIo.exe')) {
    throw "CLIo is already installed in $Destination. Update it from inside CLIo (ask her to install the update)."
}

if (Get-Process -Name CLIo -ErrorAction SilentlyContinue) { throw 'CLIo is running. Quit it from the tray first.' }

New-Item -ItemType Directory -Force -Path $Destination | Out-Null
Expand-Archive -Path $Zip -DestinationPath $Destination -Force

if (-not (Test-Path (Join-Path $Destination 'release.json'))) { throw 'That zip is not a CLIo release (no release.json).' }

$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut((Join-Path ([Environment]::GetFolderPath('Programs')) 'CLIo.lnk'))
$link.TargetPath = Join-Path $Destination 'CLIo.exe'
$link.WorkingDirectory = $Destination
$link.Save()

Write-Host "Installed to $Destination. Start CLIo from the Start Menu."
