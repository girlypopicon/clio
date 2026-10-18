# Releasing CLIo (and how self-update trusts a release)

CLIo can install updates from this repo's GitHub releases, but only releases **you** signed. The signing key is a private ECDSA key that lives only on your PC; CLIo contains the matching public key (`Services/Update/UpdateTrust.cs`) and refuses anything that does not verify. A hijacked GitHub account cannot get code onto your machine without that key.

## One-time setup

```powershell
pwsh tools\New-ReleaseKey.ps1
```

- The private key is saved encrypted for your Windows account at `%APPDATA%\CLIo-release\release-key.dpapi`. **Back it up somewhere private.** If it is lost, installed copies cannot accept updates signed with a new key and must be reinstalled by hand. If it leaks, treat it like a leaked password: anyone with it can make CLIo install their code.
- The public key it prints is already in `UpdateTrust.cs`. Only replace it if you create a new key.

## Making a release

1. Bump `<Version>` in `CLIo.csproj` and add a patch-notes entry in `Services/SystemPromptBuilder.cs`.
2. Commit and push to `main`. (The build refuses a version without a matching top patch-notes entry; see `docs/VERSIONING.md`.)
3. Build, package and sign (nothing is uploaded yet):
   ```powershell
   pwsh tools\Publish-Release.ps1 -Notes "What changed, in a sentence or two."
   ```
   This produces `artifacts\CLIo-<version>-win-x64.zip`, `manifest.json` (version, file name, size, SHA-256) and `manifest.sig` (signature over the exact manifest bytes), and checks the signature against the key compiled into the app.
4. Upload:
   ```powershell
   pwsh tools\Publish-Release.ps1 -Notes "..." -Publish
   ```
   (or run the `gh release create` command the script prints).

For release builds, set a private `CLIO_SELFDOCS_KEY` environment variable first (see `CLAUDE.md`, self_docs).

## First install on a machine

Download the zip from the release page and run:

```powershell
pwsh tools\Install-Local.ps1 -Zip .\CLIo-<version>-win-x64.zip
```

It installs to `%LOCALAPPDATA%\Programs\CLIo` (a folder your account can write to, which self-update needs) and adds a Start Menu shortcut. After that, updates come from inside CLIo: ask her to install the update, or press INSTALL in the banner.

## What an update does (and what protects it)

1. Reads the latest release; downloads `manifest.json` and `manifest.sig`; verifies the signature with the built-in public key.
2. Checks the manifest describes a newer version and names the package.
3. Shows you the preview and waits for your approval.
4. Downloads the package; refuses it unless size and SHA-256 match the signed manifest exactly.
5. Unpacks to a staging folder (path-traversal and size checks), checks `CLIo.exe` and the `release.json` marker (version must match).
6. Backs up the database (`backups\clio.db.pre-update-*`).
7. Closes CLIo; a PowerShell step keeps the current version in `%LOCALAPPDATA%\CLIo\updates\previous-install`, swaps the files, starts the new version, and if it closes within a few seconds restores the old one.
8. The next start tells you in Main Chat whether it worked.

A copy run from source (`dotnet run`) or installed somewhere your account cannot write refuses to self-update and says why.
