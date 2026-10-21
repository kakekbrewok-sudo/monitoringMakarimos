# Source Mini Monitor 3.5.3

Source menggunakan C# Windows Forms untuk .NET Framework 4.7.2 dan target Windows x64. Folder `Source` dan `MiniMonitor` harus berdampingan karena build memakai DLL di `MiniMonitor/sensors`.

## Build

Dari root repository:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Source\Build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\Source\Installer\Build-Installer.ps1
```

`Build.ps1` menghasilkan `MiniMonitor/MiniMonitor.exe` secara default. Gunakan `-OutputPath` untuk build sementara. `Build-Installer.ps1` membuat installer offline di folder `Installer`.

## Struktur source

- `MonitorV2.cs`: jendela, tray, menu, sensor, serta layout dasar.
- `MonitorThemes.cs`, `MonitorWideThemes.cs`, `DotClock.cs`: empat tema dan jam.
- `ActivityTracker.cs`: aplikasi aktif, durasi, dan pengingat.
- `MonitorFps.cs`, `FpsReader.cs`, `FpsTrace.cs`: deteksi game dan FPS melalui ETW/PresentMon.
- `GpuFanBoost.cs`, `MonitorCooling.cs`: pendinginan GPU, pemulihan kipas, dan shutdown opsional.
- `SoftChimes.cs`: bunyi pergantian jam dan pengingat satu jam aktif.
- `StartupIntegration.cs`: integrasi Task Scheduler.
- `Installer/`: builder dan engine installer/uninstaller.
- `Tests/`: pengujian unit, integrasi, UI offscreen, FPS, dan kipas palsu.
- `Build-Icon.ps1`: menghasilkan ulang `MiniMonitor.ico` dan pratinjau logo.

Dependensi dan lisensinya tercatat di `../MiniMonitor/THIRD-PARTY.txt`. Build tidak mengunduh paket atau mengubah driver.
