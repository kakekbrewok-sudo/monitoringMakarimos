# Installer Mini Monitor

Installer WinForms satu EXE, memakai compiler .NET Framework bawaan Windows. Tidak membutuhkan Inno Setup, NSIS, Node, WebView, atau service tambahan. Payload ZIP dan ikon ditanam sebagai resource. Hash SHA-256 diverifikasi sebelum instalasi.

Jalankan `Build-Installer.ps1` dari PowerShell. Skrip membangun aplikasi, memverifikasi tanda tangan dependensi resmi, membatasi isi payload ke daftar rilis eksplisit, lalu menghasilkan EXE di `outputs/Installer`. File `.build.json` memuat lokasi staging dan hash untuk audit pengembangan; bukan kebutuhan pengguna akhir.

`SetupEngine.cs` menangani penutupan proses, ekstraksi yang tervalidasi, cadangan/rollback, startup, shortcut, registrasi Installed Apps, dan uninstall. `SetupProgram.cs` menangani pilihan UI, elevasi pengguna yang sama, pemeriksaan OS/runtime, dan driver opsional. Pengaturan di LocalAppData tidak disentuh oleh installer. Hasil rilis dicatat di `outputs/Dokumentasi/HASIL-UJI-INSTALLER-3.5.2.txt`.

Build `/define:SETUP_TEST` atau `Build-Installer.ps1 -TestBuild` memakai direktori, prefix task, shortcut, dan registry terpisah. `SetupChecks.cs` menguji lifecycle di lokasi uji tersebut dan tidak boleh dikompilasi tanpa `SETUP_TEST`. Jangan jalankan uji terhadap pemasangan produksi.

Installer hanya mendukung akun administrator interaktif yang sama sebelum/sesudah UAC. Penggunaan kredensial admin lain dibatalkan dengan pesan jelas. CLI `--quiet` memakai preferensi startup yang sudah tersimpan, tidak membuka aplikasi, dan tidak memasang driver secara otomatis. `--uninstall` membuka UI penghapusan; `--uninstall --quiet` menghapus tanpa UI. Preview UI tanpa elevasi: `--preview <path PNG>`.

EXE belum memiliki tanda tangan penerbit komersial. Build tidak melemahkan keamanan Windows. Tidak ada installer compiler eksternal yang dipasang.
