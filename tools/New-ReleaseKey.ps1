#Requires -Version 7
<#
.SYNOPSIS
  Creates the key pair that signs CLIo releases. Run once.

.DESCRIPTION
  The PRIVATE key is saved only on this PC, encrypted with Windows DPAPI for your user account
  ($env:APPDATA\CLIo-release\release-key.dpapi). It is never written anywhere else and never printed.
  The PUBLIC key is printed (and saved next to it); it goes into Services/Update/UpdateTrust.cs so every
  copy of CLIo can verify releases.

  Back up the private key file somewhere safe and private. If it is lost, installed copies of CLIo will
  not accept updates signed with a new key and have to be reinstalled by hand. If it leaks, anyone can
  make CLIo install their code: treat it like a password.
#>
[CmdletBinding()]
param([switch]$Force)

$ErrorActionPreference = 'Stop'
$dir = Join-Path $env:APPDATA 'CLIo-release'
$privateFile = Join-Path $dir 'release-key.dpapi'
$publicFile = Join-Path $dir 'release-key.pub'

if ((Test-Path $privateFile) -and -not $Force) {
    throw "A release key already exists at $privateFile. Refusing to overwrite it (use -Force only if you really mean to replace it)."
}

New-Item -ItemType Directory -Force -Path $dir | Out-Null

$ecdsa = [System.Security.Cryptography.ECDsa]::Create([System.Security.Cryptography.ECCurve]::CreateFromFriendlyName('nistP256'))
$privateB64 = [Convert]::ToBase64String($ecdsa.ExportPkcs8PrivateKey())
$publicB64 = [Convert]::ToBase64String($ecdsa.ExportSubjectPublicKeyInfo())

# ConvertFrom-SecureString without a key = Windows DPAPI, current user only
ConvertTo-SecureString $privateB64 -AsPlainText -Force | ConvertFrom-SecureString | Set-Content -Path $privateFile -Encoding ASCII
Set-Content -Path $publicFile -Value $publicB64 -Encoding ASCII

Write-Host "Private key saved (DPAPI-protected, this user only): $privateFile"
Write-Host "Public key (put this in Services/Update/UpdateTrust.cs):"
Write-Host $publicB64
