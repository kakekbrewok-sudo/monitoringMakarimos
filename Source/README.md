# Kode sumber Mini Monitor v3.5.1

Versi 3.5.3 menambahkan tombol pendinginan lalu shutdown: target 100% selama 15 detik melalui NVML, pemulihan mode/target sebelumnya, kemudian shutdown hanya setelah hasil berhasil. Tombol es biasa tetap mendinginkan tanpa shutdown. Pembaca terpisah hidup hanya selama tindakan ini.

Versi 3.4.7: desain lebar Kanji, putih, dan hitam diperbarui dengan tipografi lebih besar, indikator beban, serta pemisahan informasi yang lebih rapi. Tema Kanji memakai tipografi poster dan cap merah; putih mempertahankan jam merah utuh; hitam mempertahankan dot.

Versi 3.4.6: tema putih memakai angka jam utuh merah, durasi aplikasi lebih jelas, dan aksen merah pada pengingat.

Versi 3.4.5: jendela lebar dan pendek memakai tiga kolom dengan angka besar. Warna title bar mengikuti tema pada Windows 11. Layout vertikal dan overlay tetap tersedia otomatis sesuai ukuran.

Mode jendela: matikan Mode overlay, lalu tarik tepi/sudut untuk mengubah ukuran. Mendukung maximize, minimize ke taskbar, dan Snap Windows (Win+panah). Jam dan teks mengikuti ukuran jendela; ukuran terakhir dan status maximize tersimpan terpisah dari ukuran overlay.

Versi 3.4.3 memperjelas suara pengingat: dua nada untuk pergantian jam (0,56 detik), empat ketukan untuk satu jam aktif (1,08 detik).

Fungsi FPS sebelumnya diuji pada v3.4.2 dengan fixture Direct3D9 60 FPS serta EA SPORTS FC 26. Paket runtime dan ZIP menggunakan build yang sama; lihat `../Dokumentasi/HASIL-UJI-FPS-3.4.2.txt`.

Gunakan Windows 64-bit dengan .NET Framework 4.7.2 atau lebih baru. Folder `Source` dan `MiniMonitor` harus tetap berdampingan; DLL referensi diambil dari `MiniMonitor/sensors`.

Dari folder workspace, jalankan:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\outputs\Source\Build.ps1
```

Perintah tersebut menghasilkan `outputs/MiniMonitor/MiniMonitor.exe`. Tutup aplikasi jika executable di folder itu sedang digunakan. Untuk memeriksa build tanpa mengganti paket, gunakan lokasi sementara:

```powershell
New-Item -ItemType Directory -Force .\work | Out-Null
powershell -NoProfile -ExecutionPolicy Bypass -File .\outputs\Source\Build.ps1 -OutputPath .\work\MiniMonitor.exe
```

OutputPath relatif dihitung dari direktori terminal saat ini. EXE hasil build memerlukan konfigurasi dan folder sensor paket jika hendak dijalankan; build tidak menyalin dependensi atau memasang aplikasi. ZIP tidak diperbarui secara otomatis.

- `MonitorV2.cs`: jendela, tray, menu, pengambilan sensor; nama berkas lama dipertahankan.
- `MonitorThemes.cs`, `MonitorWideThemes.cs`, `DotClock.cs`: empat tema dan tampilan jam.
- `MonitorFps.cs`, `FpsReader.cs`: menu/tampilan FPS, pemilihan game, parsing frame, dan proses PresentMon sesuai kebutuhan. Komponen resminya ada di `MiniMonitor/fps`.
- `FpsTrace.cs`: sesi ETW DirectX yang disiapkan sebelum game, filter PID/event, penghitungan present sukses, provider mati saat idle, dan fallback PresentMon. Deklarasi ETW mengikuti Windows SDK x64; properti event dibaca dengan TDH.
- `ActivityTracker.cs`: durasi aplikasi aktif, pengingat, dan identitas executable melalui `QueryFullProcessImageName` dengan `PROCESS_QUERY_LIMITED_INFORMATION`; metadata tidak bergantung pada akses modul/memori game.
- `GpuFanBoost.cs`, `MonitorCooling.cs`: kontrol kipas sementara, pemulihan, countdown dan tombol es.
- `SoftChimes.cs`: jadwal dan pembuatan bunyi singkat.
- `KeepOpenMenuItem.cs`: perilaku pilihan menu yang tetap terbuka.
- `StartupIntegration.cs`: integrasi pengaturan startup Windows.
- `MiniMonitor.manifest`, `MiniMonitor.ico`: sumber daya yang disematkan ke EXE.
- `Build-Icon.ps1`: menghasilkan ulang ikon dan `Pratinjau/MiniMonitor-logo.png`; hanya dipakai saat pengembangan.
- `Tests/`: sumber pengujian dan petunjuk menjalankannya.

Dependensi dan lisensinya tercatat di `../MiniMonitor/THIRD-PARTY.txt`. Tidak perlu mengunduh ulang library untuk melakukan build.

Referensi kompatibilitas: [PresentMon #573](https://github.com/GameTechDev/PresentMon/issues/573) menjelaskan mengapa sesi harus dibuat sebelum EA AntiCheat berjalan. Sesi DirectX memakai API Windows yang sudah tersedia, tanpa dependensi baru.
