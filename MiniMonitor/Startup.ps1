param(
    [ValidateSet('Install','Update','Enable','Disable')][string]$Mode = 'Install',
    [string]$PreviousExecutable = '',
    [string]$ResultPath = ''
)
$ErrorActionPreference = 'Stop'

function Get-KnownMiniMonitorProcesses([string[]]$AllowedExecutables) {
    foreach ($candidate in @(Get-Process -Name MiniMonitor -ErrorAction SilentlyContinue)) {
        try {
            if ($candidate.HasExited) { $candidate.Dispose(); continue }
            if ($AllowedExecutables -contains $candidate.MainModule.FileName) { $candidate }
            else { $candidate.Dispose() }
        } catch {
            if ($candidate.HasExited) { $candidate.Dispose(); continue }
            $candidate.Dispose()
            throw
        }
    }
}

function Get-MiniMonitorCommandLine($Process) {
    $details = Get-CimInstance Win32_Process -Filter ("ProcessId=" + $Process.Id) -ErrorAction Stop
    if ($Process.HasExited) { return '' }
    if (-not $details -or -not $details.CommandLine) {
        throw 'Proses Mini Monitor belum dapat diperiksa. Tutup aplikasi sebelum memperbarui.'
    }
    return $details.CommandLine
}

function Stop-MiniMonitorForUpdate([string[]]$AllowedExecutables) {
    foreach ($process in @(Get-KnownMiniMonitorProcesses $AllowedExecutables)) {
        try {
            if ($process.HasExited) { continue }
            $command = Get-MiniMonitorCommandLine $process
            if ($process.HasExited -or $command -match '\s--fan-boost(?:\s|$)') { continue }
            $null = $process.CloseMainWindow()
            if (-not $process.WaitForExit(8000)) {
                throw 'Mini Monitor belum selesai ditutup. Tutup aplikasi lalu ulangi pemasangan.'
            }
        } finally { $process.Dispose() }
    }
    # A helper can start after the first process snapshot. Recheck only after
    # the UI has stopped, so restoration finishes before replacing its EXE.
    foreach ($process in @(Get-KnownMiniMonitorProcesses $AllowedExecutables)) {
        try {
            if ($process.HasExited) { continue }
            $command = Get-MiniMonitorCommandLine $process
            if ($process.HasExited) { continue }
            if ($command -notmatch '\s--fan-boost(?:\s|$)') {
                throw 'Mini Monitor dibuka kembali. Tutup aplikasi lalu ulangi pemasangan.'
            }
            # Never force-kill the helper: it owns restoration of the fan policy.
            if (-not $process.WaitForExit(20000)) {
                throw 'Tunggu pemulihan kipas GPU selesai sebelum memperbarui.'
            }
        } finally { $process.Dispose() }
    }
    $remaining = @(Get-KnownMiniMonitorProcesses $AllowedExecutables)
    foreach ($process in $remaining) { $process.Dispose() }
    if ($remaining.Count -gt 0) { throw 'Mini Monitor masih berjalan. Ulangi setelah aplikasi ditutup.' }
}

function Update-MiniMonitorTaskAction($ExistingTask, [string]$InstalledExe, [string]$InstallRoot) {
    $actions = @($ExistingTask.Actions)
    if ($actions.Count -eq 1 -and $actions[0].Execute -eq $InstalledExe -and
        $actions[0].Arguments -eq '--startup' -and $actions[0].WorkingDirectory -eq $InstallRoot) { return }
    $wasEnabled = [bool]$ExistingTask.Settings.Enabled
    $action = New-ScheduledTaskAction -Execute $InstalledExe -Argument '--startup' -WorkingDirectory $InstallRoot
    # Change only the action. Keep the user's principal, triggers and settings.
    Set-ScheduledTask -TaskName $ExistingTask.TaskName -TaskPath $ExistingTask.TaskPath -Action $action | Out-Null
    $updated = Get-ScheduledTask -TaskName $ExistingTask.TaskName -TaskPath $ExistingTask.TaskPath
    if ([bool]$updated.Settings.Enabled -ne $wasEnabled) {
        if ($wasEnabled) { Enable-ScheduledTask -TaskName $ExistingTask.TaskName -TaskPath $ExistingTask.TaskPath | Out-Null }
        else { Disable-ScheduledTask -TaskName $ExistingTask.TaskName -TaskPath $ExistingTask.TaskPath | Out-Null }
    }
}

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $launchArgs = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"{0}"' -f $PSCommandPath), '-Mode', $Mode)
    if ($PreviousExecutable) { $launchArgs += @('-PreviousExecutable', ('"{0}"' -f $PreviousExecutable)) }
    if ($ResultPath) { $launchArgs += @('-ResultPath', ('"{0}"' -f $ResultPath)) }
    Start-Process powershell.exe -ArgumentList $launchArgs -Verb RunAs -WindowStyle Hidden
    exit
}

$taskName = 'MiniMonitor-' + $identity.User.Value
$installRoot = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'MiniMonitor'))
$expectedRoot = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\MiniMonitor'
if ($installRoot -ne $expectedRoot) { throw 'Unexpected installation target.' }
$installedExe = Join-Path $installRoot 'MiniMonitor.exe'
$userData = Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'MiniMonitor'
if (-not $ResultPath) { $ResultPath = Join-Path $userData 'startup-result.txt' }

try {
    New-Item -ItemType Directory -Force -Path $userData | Out-Null
    if ($Mode -eq 'Disable') {
        Disable-ScheduledTask -TaskName $taskName | Out-Null
        "Startup disabled: $taskName" | Set-Content -LiteralPath $ResultPath
        exit
    }
    if ($Mode -eq 'Enable') {
        Enable-ScheduledTask -TaskName $taskName | Out-Null
        "Startup enabled: $taskName" | Set-Content -LiteralPath $ResultPath
        exit
    }

    $sourceExe = Join-Path $PSScriptRoot 'MiniMonitor.exe'
    if (-not (Test-Path -LiteralPath $sourceExe)) { throw 'Ekstrak seluruh ZIP sebelum memasang.' }
    $allowedExecutables = @([IO.Path]::GetFullPath($sourceExe), $installedExe)
    if ($PreviousExecutable) { $allowedExecutables += [IO.Path]::GetFullPath($PreviousExecutable) }
    Stop-MiniMonitorForUpdate $allowedExecutables
    New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
    if ([IO.Path]::GetFullPath($PSScriptRoot) -ne $installRoot) {
        Get-ChildItem -LiteralPath $PSScriptRoot | Where-Object { $_.Name -ne 'MiniMonitor.settings' } | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName -Destination $installRoot -Recurse -Force
        }
    }
    $localSettings = Join-Path $userData 'MiniMonitor.settings'
    $oldSettings = Join-Path $PSScriptRoot 'MiniMonitor.settings'
    if ($PreviousExecutable) { $oldSettings = Join-Path ([IO.Path]::GetDirectoryName($PreviousExecutable)) 'MiniMonitor.settings' }
    if (-not (Test-Path -LiteralPath $localSettings) -and (Test-Path -LiteralPath $oldSettings)) {
        Copy-Item -LiteralPath $oldSettings -Destination $localSettings
    }

    $existingTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($existingTask) {
        # Reinstall and update both preserve the user's startup toggle and task.
        Update-MiniMonitorTaskAction $existingTask $installedExe $installRoot
        Start-Process -FilePath $installedExe -ArgumentList '--startup' -WindowStyle Normal
        @("Updated: $installedExe", "Task: $taskName", "Enabled: $($existingTask.Settings.Enabled)", 'Startup configuration and user settings preserved.') | Set-Content -LiteralPath $ResultPath
        exit
    }
    $action = New-ScheduledTaskAction -Execute $installedExe -Argument '--startup' -WorkingDirectory $installRoot
    $trigger = New-ScheduledTaskTrigger -AtLogOn -User $identity.Name
    $taskPrincipal = New-ScheduledTaskPrincipal -UserId $identity.Name -LogonType Interactive -RunLevel Highest
    $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew
    Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $taskPrincipal -Settings $settings -Description 'Mini Monitor: clock, foreground application duration, RAM and temperatures. Starts at user login.' -Force | Out-Null
    Start-ScheduledTask -TaskName $taskName
    $task = Get-ScheduledTask -TaskName $taskName
    @("Installed: $installedExe", "Task: $taskName", "User: $($task.Principal.UserId)", "RunLevel: $($task.Principal.RunLevel)", "LogonType: $($task.Principal.LogonType)", "Enabled: $($task.Settings.Enabled)", "Arguments: $($task.Actions.Arguments)", "Trigger: $($task.Triggers.CimClass.CimClassName)") | Set-Content -LiteralPath $ResultPath
} catch {
    "ERROR: $($_.Exception.Message)" | Set-Content -LiteralPath $ResultPath
    Add-Type -AssemblyName System.Windows.Forms
    [Windows.Forms.MessageBox]::Show($_.Exception.Message, 'Mini Monitor - pemasangan belum selesai') | Out-Null
    exit 1
}
