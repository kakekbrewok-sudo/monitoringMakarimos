# Pengujian Mini Monitor

Sumber tes disimpan terpisah dari paket aplikasi. Masing-masing memiliki entry point sendiri dan harus dikompilasi bersama kode aplikasi menggunakan `/main:<nama tes>`.

| Tes | Cakupan |
| --- | --- |
| `ActivityChecks` | Durasi aktif, perpindahan aplikasi, sleep/lock, pengingat, dan pemakaian ulang PID. |
| `ChimeChecks` | Pergantian jam, batas jam aktif, mute, serta bentuk dan durasi WAV; tidak memutar suara. |
| `UsageChecks` | Pemilihan sensor usage, format angka, layout, dan pembacaan usage perangkat. |
| `MonitorChecks` | Suhu, RAM, hotkey, shutdown, dan sampel kinerja; khusus perangkat pengembangan dengan dua SSD dan HDD eksternal, memerlukan driver serta administrator. |
| `TrayChecks` | Pilihan menu, persistensi pengaturan tes, dan ikon tray. |
| `ThemeChecks` | Empat tema, persistensi, posisi/handle jendela, dan render kasus tepi. |
| `ThemeTrayPollingChecks` | Menu tray saat timer benar-benar berjalan; memakai desktop Windows terpisah yang tidak ditampilkan. |
| `FpsChecks` | Parser FPS, filtering PID/swap chain, data basi, batas memori, deteksi game, dan persistensi pilihan. |
| `NativeFpsChecks` | Persiapan sesi ETW sebelum fixture Direct3D9, FPS 60, provider idle/resume, kedaluwarsa frame, biaya CPU dan buffer; kompilasikan juga `FpsLiveChecks.cs`, gunakan folder keluaran baru dan jalankan sebagai administrator ketika EA AntiCheat tidak aktif. |
| `NativeGameFpsChecks` | Siapkan sesi native, tunggu executable game dibuka, periksa frame asli dan resume selama game berjalan; argumen nama proses tanpa `.exe` lalu path laporan baru. Tes tidak membuka atau menutup game. |
| `FpsStartupChecks` | Penanganan startup FPS yang ditolak, toggle dan shutdown; argumen path laporan, opsional `--blocked` untuk memeriksa status saat EA AntiCheat aktif. |
| `FpsLiveChecks` | Fixture Direct3D9 offscreen 60 FPS, lifecycle helper dan pengukuran CPU/RAM; perlu administrator dan salinan folder `MiniMonitor/fps` di sebelah EXE tes. Gunakan folder keluaran baru setiap run. |
| `RunningGameFpsChecks` | Membaca FPS selama 15 detik dari PID game yang sudah berjalan, tanpa mengaktifkan atau menutup game; perlu administrator dan folder `fps` di sebelah EXE tes. Argumen: PID lalu path berkas laporan. |
| `FpsUiChecks` | Menu FPS saat timer berjalan, persistensi dan render empat tema; kompilasikan juga `ThemeTrayPollingChecks.cs` untuk memakai DesktopTestRunner. |

Contoh dari folder `outputs/Source`, untuk mengompilasi tes durasi:

```powershell
$testName = 'ActivityChecks'
$testOutput = [IO.Path]::GetFullPath((Join-Path (Get-Location) '..\..\work\tests'))
New-Item -ItemType Directory -Force $testOutput | Out-Null
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$sources = Get-ChildItem -LiteralPath . -Filter '*.cs' | Select-Object -ExpandProperty FullName
& $compiler /nologo /target:exe /platform:x64 /optimize+ "/main:$testName" "/out:$testOutput\$testName.exe" /win32manifest:MiniMonitor.manifest /win32icon:MiniMonitor.ico /resource:MiniMonitor.ico,MiniMonitor.Icon /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:Microsoft.CSharp.dll /reference:..\MiniMonitor\sensors\LibreHardwareMonitorLib.dll $sources "Tests\$testName.cs"
if ($LASTEXITCODE -ne 0) { throw 'Kompilasi tes gagal.' }
Get-ChildItem -LiteralPath ..\MiniMonitor\sensors -Filter '*.dll' | Copy-Item -Destination $testOutput
& "$testOutput\$testName.exe" "$testOutput\activity-checks.txt"
if ($LASTEXITCODE -ne 0) { throw 'Tes gagal; baca laporan.' }
```

`ActivityChecks` dan `FpsStartupChecks` menerima path berkas laporan. `RunningGameFpsChecks` menerima PID game dan path berkas laporan. `NativeGameFpsChecks` menerima nama proses dan path laporan. Tes lain menerima path folder keluaran, misalnya `& "$testOutput\ThemeChecks.exe" $testOutput`, setelah mengganti nama tes pada langkah kompilasi. `ThemeTrayPollingChecks` menerima argumen opsional kedua berupa nama berkas laporan.

Tes yang membuka UI atau membaca sensor memerlukan sesi desktop Windows dan akses perangkat. Tes desktop terpisah tidak dapat berjalan bila sandbox melarang pembuatan desktop. `TrayChecks` dan `MonitorChecks` berinteraksi dengan desktop aktif; jalankan saat tidak sedang menggunakan aplikasi lain. Hasil pada `../../Dokumentasi/HASIL-UJI.txt` merupakan catatan pengujian sebelum perapihan folder, bukan hasil otomatis setiap build.

WindowChecks memeriksa ukuran normal, lebar 1080x300/680x300, portrait, overlay, maximize dan persistensi; juga merender empat tema. Tes tidak membuka game atau membunyikan pengingat.

FanBoostChecks memakai perangkat palsu: 15 detik, AUTO/manual, pembatalan, konflik controller, rollback parsial, dan journal. Tidak mengubah kipas asli. CoolingUiChecks merender tombol di seluruh tema/ukuran dan menguji aksesibilitas/countdown tanpa klik hardware.
