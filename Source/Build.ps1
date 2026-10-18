param([string]$OutputPath = '')
$ErrorActionPreference = 'Stop'
$packageRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\MiniMonitor'))
if (-not $OutputPath) { $OutputPath = Join-Path $packageRoot 'MiniMonitor.exe' }
$OutputPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputPath)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$manifest = Join-Path $PSScriptRoot 'MiniMonitor.manifest'
$icon = Join-Path $PSScriptRoot 'MiniMonitor.ico'
$sensorLibrary = Join-Path $packageRoot 'sensors\LibreHardwareMonitorLib.dll'
$sources = @('AssemblyInfo.cs','MonitorV2.cs','MonitorThemes.cs','MonitorWideThemes.cs','MonitorCooling.cs','GpuFanBoost.cs','MonitorFps.cs','FpsReader.cs','FpsTrace.cs','ActivityTracker.cs','DotClock.cs','StartupIntegration.cs','SoftChimes.cs','KeepOpenMenuItem.cs') | ForEach-Object { Join-Path $PSScriptRoot $_ }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ "/out:$OutputPath" "/win32manifest:$manifest" "/win32icon:$icon" "/resource:$icon,MiniMonitor.Icon" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll "/reference:$sensorLibrary" $sources
if ($LASTEXITCODE -ne 0) { throw 'Build gagal.' }
Write-Output "Build berhasil: $OutputPath"
