param([string]$OutputDirectory = '', [switch]$TestBuild)
$ErrorActionPreference = 'Stop'
$sourceRoot = Split-Path -Parent $PSScriptRoot
$outputsRoot = Split-Path -Parent $sourceRoot
$packageRoot = Join-Path $outputsRoot 'MiniMonitor'
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $outputsRoot 'Installer' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$buildRoot = Join-Path ([IO.Path]::GetFullPath((Join-Path $outputsRoot '..\work\installer'))) ('build-' + [Guid]::NewGuid().ToString('N'))
$payloadRoot = Join-Path $buildRoot 'payload'
New-Item -ItemType Directory -Path $payloadRoot -Force | Out-Null

# Use a closed list of release files. Developer files and local preferences
# never enter the installer, even if someone leaves them in the runtime folder.
$files = @(
    'MiniMonitor.exe','MiniMonitor.exe.config','Startup.ps1',
    'Jalankan-Admin.cmd','Pasang-Otomatis.cmd','Perbarui-Aplikasi.cmd','Nonaktifkan-Startup.cmd',
    'PANDUAN.txt','THIRD-PARTY.txt','fps\PresentMon.exe','setup\PawnIO_setup.exe',
    'sensors\LibreHardwareMonitorLib.dll','sensors\BlackSharp.Core.dll','sensors\DiskInfoToolkit.dll',
    'sensors\HidSharp.dll','sensors\RAMSPDToolkit-NDD.dll','sensors\System.Buffers.dll',
    'sensors\System.Memory.dll','sensors\System.Numerics.Vectors.dll',
    'sensors\System.Runtime.CompilerServices.Unsafe.dll','sensors\LICENSE-LibreHardwareMonitor.txt',
    'licenses\BlackSharp-LICENSE.txt','licenses\DiskInfoToolkit-LICENSE.txt','licenses\DotNet-LICENSE.txt',
    'licenses\hidsharp-LICENSE.txt','licenses\PawnIO-COPYING.txt','licenses\PawnIO-Modules-COPYING.txt',
    'licenses\PawnIO-README.md','licenses\PresentMon-LICENSE.txt','licenses\RAMSPDToolkit-LICENSE.txt'
)
foreach ($relative in $files) {
    $destination = Join-Path $payloadRoot $relative
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $destination) | Out-Null
    Copy-Item -LiteralPath (Join-Path $packageRoot $relative) -Destination $destination
}
foreach ($relative in @('fps\PresentMon.exe','setup\PawnIO_setup.exe')) {
    $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $payloadRoot $relative)
    if ($signature.Status -ne 'Valid') { throw "Invalid third-party signature: $relative" }
}
& (Join-Path $sourceRoot 'Build.ps1') -OutputPath (Join-Path $payloadRoot 'MiniMonitor.exe')
if ((Get-Item (Join-Path $payloadRoot 'MiniMonitor.exe')).VersionInfo.FileVersion -ne '3.5.5.0') { throw 'App version does not match installer' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$payloadZip = Join-Path $buildRoot 'payload.zip'
[IO.Compression.ZipFile]::CreateFromDirectory($payloadRoot, $payloadZip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash -LiteralPath $payloadZip).Hash
$hashSource = Join-Path $buildRoot 'EmbeddedPayload.cs'
('static class EmbeddedPayload { public const string Sha256 = "' + $hash + '"; }') | Set-Content -LiteralPath $hashSource -Encoding UTF8
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$outputName = if ($TestBuild) { 'MiniMonitor-SetupTest-3.5.5-x64.exe' } else { 'MiniMonitor-Setup-3.5.5-x64.exe' }
$outputPath = Join-Path $OutputDirectory $outputName
$compilerArgs = @('/nologo','/target:winexe','/platform:x64','/optimize+',"/out:$outputPath",'/main:SetupProgram',
    ('/win32manifest:' + (Join-Path $PSScriptRoot 'Setup.manifest')),
    ('/win32icon:' + (Join-Path $sourceRoot 'MiniMonitor.ico')),
    ('/resource:' + (Join-Path $sourceRoot 'MiniMonitor.ico') + ',MiniMonitor.Icon'),
    ('/resource:' + $payloadZip + ',MiniMonitor.Payload'),
    '/reference:System.Windows.Forms.dll','/reference:System.Drawing.dll','/reference:Microsoft.CSharp.dll',
    '/reference:System.Management.dll','/reference:System.IO.Compression.dll','/reference:System.IO.Compression.FileSystem.dll')
if ($TestBuild) { $compilerArgs += '/define:SETUP_TEST' }
$compilerArgs += @((Join-Path $PSScriptRoot 'SetupProgram.cs'),(Join-Path $PSScriptRoot 'SetupEngine.cs'),(Join-Path $PSScriptRoot 'SetupAssemblyInfo.cs'),$hashSource)
& $compiler $compilerArgs
if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
$result = [ordered]@{ Installer = $outputPath; InstallerSHA256 = (Get-FileHash -LiteralPath $outputPath).Hash; Payload = $payloadZip; PayloadRoot = $payloadRoot; PayloadSHA256 = $hash; Version = '3.5.5'; TestBuild = [bool]$TestBuild }
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory ($outputName + '.build.json')) -Encoding UTF8
Write-Output ($result | ConvertTo-Json)
