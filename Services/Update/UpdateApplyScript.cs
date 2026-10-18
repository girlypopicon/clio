namespace CLIo.Services.Update;

/// <summary>
/// The PowerShell step that swaps the files once CLIo has exited. It lives inside the app (so it is covered
/// by the same trust as the app itself) and is written to the updates folder only when an update runs.
/// Windows PowerShell 5.1 ships with Windows, so nothing extra is needed.
/// </summary>
internal static class UpdateApplyScript
{
    public const string Text = """
        param(
            [Parameter(Mandatory = $true)][int]$ProcessId,
            [Parameter(Mandatory = $true)][string]$InstallDir,
            [Parameter(Mandatory = $true)][string]$StagingDir,
            [Parameter(Mandatory = $true)][string]$BackupDir,
            [Parameter(Mandatory = $true)][string]$ResultFile,
            [string]$ExeName = 'CLIo.exe',
            [string]$NewVersion = '',
            [int]$WatchSeconds = 12
        )

        $ErrorActionPreference = 'Stop'

        function Write-Result([bool]$ok, [string]$message) {
            $o = [ordered]@{ ok = $ok; version = $NewVersion; message = $message; time = (Get-Date).ToString('o') }
            ($o | ConvertTo-Json) | Set-Content -Path $ResultFile -Encoding UTF8
        }

        # robocopy exit codes 0-7 mean success
        function Mirror([string]$from, [string]$to) {
            & robocopy $from $to /MIR /R:3 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -ge 8) { throw "File copy failed (robocopy $LASTEXITCODE)." }
        }

        $appExe = Join-Path $InstallDir $ExeName

        try {
            # 1. wait for CLIo to close; if it does not, change nothing
            $proc = Get-Process -Id $ProcessId -ErrorAction SilentlyContinue
            if ($proc) {
                if (-not $proc.WaitForExit(60000)) { throw 'CLIo did not close within 60 seconds, so nothing was changed.' }
            }
            Start-Sleep -Seconds 1

            # 2. keep the current version
            if (Test-Path $BackupDir) { Remove-Item $BackupDir -Recurse -Force }
            Mirror $InstallDir $BackupDir

            try {
                # 3. swap in the new version and start it
                Mirror $StagingDir $InstallDir
                $new = Start-Process -FilePath $appExe -WorkingDirectory $InstallDir -PassThru
                Start-Sleep -Seconds $WatchSeconds
                if ($new.HasExited) { throw "The new version closed right after starting (exit code $($new.ExitCode))." }
                Write-Result $true 'Updated.'
            }
            catch {
                # 4. the new version did not survive: put the old one back
                $reason = $_.Exception.Message
                Mirror $BackupDir $InstallDir
                Start-Process -FilePath $appExe -WorkingDirectory $InstallDir | Out-Null
                Write-Result $false ('The update was rolled back: ' + $reason)
            }
        }
        catch {
            Write-Result $false $_.Exception.Message
            # nothing was swapped; bring CLIo back only if it is not still running
            if (-not (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue)) {
                try { Start-Process -FilePath $appExe -WorkingDirectory $InstallDir | Out-Null } catch { }
            }
        }
        """;
}
