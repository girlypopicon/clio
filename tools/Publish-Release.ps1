#Requires -Version 7
<#
.SYNOPSIS
  Builds, packages and signs a CLIo release. Add -Publish to upload it to GitHub.

.DESCRIPTION
  1. Reads the version from <Version> in CLIo.csproj (bump it first, and add a patch-notes entry).
  2. dotnet publish (self-contained, win-x64) into artifacts\publish.
  3. Adds release.json (the marker that tells an install it can update itself) and zips the folder.
  4. Writes manifest.json (version, file name, size, SHA-256, notes) and signs it with the private key from
     New-ReleaseKey.ps1 into manifest.sig. Checks the signature against the public key in UpdateTrust.cs.
  5. With -Publish: gh release create vX.Y.Z <zip> manifest.json manifest.sig.

  Without -Publish nothing leaves this PC.
#>
[CmdletBinding()]
param(
    [string]$Notes = '',
    [switch]$Publish
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# ---- version ------------------------------------------------------------------------------
$csproj = [xml](Get-Content (Join-Path $root 'CLIo.csproj'))
$version = ($csproj.Project.PropertyGroup | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1)
if (-not $version) { throw 'No <Version> in CLIo.csproj.' }
Write-Host "Building CLIo $version"

# ---- key checks ---------------------------------------------------------------------------
$trustFile = Join-Path $root 'Services\Update\UpdateTrust.cs'
$embedded = [regex]::Match((Get-Content $trustFile -Raw), 'PublicKeyBase64 = "([^"]+)"').Groups[1].Value
$keyFile = Join-Path $env:APPDATA 'CLIo-release\release-key.dpapi'
if (-not (Test-Path $keyFile)) { throw "No signing key at $keyFile. Run tools\New-ReleaseKey.ps1 first." }
$pubFile = Join-Path $env:APPDATA 'CLIo-release\release-key.pub'
if ((Get-Content $pubFile -Raw).Trim() -ne $embedded) {
    throw 'The public key in UpdateTrust.cs does not match your signing key, so installed apps would reject this release.'
}

# ---- publish ------------------------------------------------------------------------------
$artifacts = Join-Path $root 'artifacts'
$publishDir = Join-Path $artifacts 'publish'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

dotnet publish (Join-Path $root 'CLIo.csproj') -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
if (-not (Test-Path (Join-Path $publishDir 'CLIo.exe'))) { throw 'CLIo.exe is missing from the publish output.' }

# the marker that says "this is an installed release that may update itself"
@{ app = 'CLIo'; version = "$version" } | ConvertTo-Json | Set-Content -Path (Join-Path $publishDir 'release.json') -Encoding UTF8

# ---- package ------------------------------------------------------------------------------
$zipName = "CLIo-$version-win-x64.zip"
$zip = Join-Path $artifacts $zipName
if (Test-Path $zip) { Remove-Item $zip -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($publishDir, $zip, [System.IO.Compression.CompressionLevel]::Optimal, $false)

$size = (Get-Item $zip).Length
$sha256 = (Get-FileHash -Algorithm SHA256 -Path $zip).Hash.ToLowerInvariant()

# ---- manifest + signature -----------------------------------------------------------------
$manifest = [ordered]@{
    app       = 'CLIo'
    version   = "$version"
    asset     = $zipName
    size      = $size
    sha256    = $sha256
    notes     = $Notes
    createdAt = (Get-Date).ToUniversalTime().ToString('o')
}
$manifestBytes = [System.Text.UTF8Encoding]::new($false).GetBytes(($manifest | ConvertTo-Json))
$manifestPath = Join-Path $artifacts 'manifest.json'
[System.IO.File]::WriteAllBytes($manifestPath, $manifestBytes)

$secure = Get-Content $keyFile | ConvertTo-SecureString
$privateB64 = [System.Net.NetworkCredential]::new('', $secure).Password
$ecdsa = [System.Security.Cryptography.ECDsa]::Create()
$read = 0
$ecdsa.ImportPkcs8PrivateKey([Convert]::FromBase64String($privateB64), [ref]$read)
$signature = $ecdsa.SignData($manifestBytes, [System.Security.Cryptography.HashAlgorithmName]::SHA256)
$sigPath = Join-Path $artifacts 'manifest.sig'
Set-Content -Path $sigPath -Value ([Convert]::ToBase64String($signature)) -Encoding ASCII -NoNewline

# sanity: verify with the PUBLIC key that is compiled into the app
$verifier = [System.Security.Cryptography.ECDsa]::Create()
$verifier.ImportSubjectPublicKeyInfo([Convert]::FromBase64String($embedded), [ref]$read)
if (-not $verifier.VerifyData($manifestBytes, $signature, [System.Security.Cryptography.HashAlgorithmName]::SHA256)) {
    throw 'Signature check against the built-in public key failed. Not publishing.'
}

Write-Host ''
Write-Host "Package : $zip ($([math]::Round($size / 1MB, 1)) MB)"
Write-Host "SHA-256 : $sha256"
Write-Host "Manifest: $manifestPath (signed, verified against the key inside the app)"

if (-not $Publish) {
    Write-Host ''
    Write-Host 'Nothing was uploaded. To publish:'
    Write-Host "  gh release create v$version `"$zip`" `"$manifestPath`" `"$sigPath`" --repo girlypopicon/clio --title `"CLIo v$version`" --notes `"$Notes`""
    return
}

if (git status --porcelain) { Write-Warning 'The working tree has uncommitted changes; the release tag will point at the last commit on the default branch.' }
gh release create "v$version" $zip $manifestPath $sigPath --repo girlypopicon/clio --title "CLIo v$version" --notes $Notes
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed.' }
Write-Host "Published v$version."
