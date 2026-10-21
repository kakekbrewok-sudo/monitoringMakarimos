# Mini Monitor v3.5.3

Installer offline satu file: `Installer/MiniMonitor-Setup-3.5.3-x64.exe`. Jalankan untuk memasang atau memperbarui Mini Monitor. Ada pilihan startup, shortcut desktop, dan driver CPU bila belum tersedia. Aplikasi terdaftar di Installed Apps Windows untuk uninstall; pengaturan pribadi dan driver bersama tetap disimpan.

Versi 3.5.3 menambahkan tombol daya pada panel jam, terpisah dari tombol es di suhu GPU. Sekali klik langsung mendinginkan GPU selama 15 detik, memulihkan kontrol kipas, lalu mematikan Windows. Klik lagi selama hitung mundur untuk membatalkan; kegagalan atau intervensi pengendali lain juga membatalkan shutdown. Tombol es biasa tetap tersedia tanpa shutdown.

Versi 3.5.1 terpasang dan terverifikasi. Tombol es di samping suhu GPU meminta target kipas 100% selama 15 detik lalu mengembalikan mode/target sebelumnya. Desain lebar Kanji, putih, dan hitam ikut terpasang. Hasil uji ada di Dokumentasi/HASIL-UJI-FAN-3.5.1.txt.

Versi 3.4.7: desain lebar Kanji, putih, dan hitam diperbarui dengan tipografi lebih besar, indikator beban, serta pemisahan informasi yang lebih rapi. Tema Kanji memakai tipografi poster dan cap merah; putih mempertahankan jam merah utuh; hitam mempertahankan dot.

Versi 3.4.6: tema putih memakai angka jam utuh merah, durasi aplikasi lebih jelas, dan aksen merah pada pengingat.

Versi 3.4.5: jendela lebar dan pendek memakai tiga kolom dengan angka besar. Warna title bar mengikuti tema pada Windows 11. Layout vertikal dan overlay tetap tersedia otomatis sesuai ukuran.

Mode jendela: matikan Mode overlay, lalu tarik tepi/sudut untuk mengubah ukuran. Mendukung maximize, minimize ke taskbar, dan Snap Windows (Win+panah). Jam dan teks mengikuti ukuran jendela; ukuran terakhir dan status maximize tersimpan terpisah dari ukuran overlay.

Versi 3.4.3 memperjelas suara pengingat: dua nada untuk pergantian jam (0,56 detik), empat ketukan untuk satu jam aktif (1,08 detik).

Folder ini berisi versi terbaru Mini Monitor dengan FPS game serta tema Dot Hitam, Dot Putih, Kanji Art, dan G-Shock. FPS menyatu di baris nama game; pengaturannya ada di klik kanan > FPS game.

Versi 3.4.2 memperbaiki identifikasi EA FC dan menyiapkan sesi FPS sebelum game dibuka. Sesi DirectX tetap siap saat menganggur dengan provider frame dinonaktifkan. Hasil uji game langsung, biaya sumber daya, dan pemasangan tercatat di `Dokumentasi/HASIL-UJI-FPS-3.4.2.txt`.

| Lokasi | Isi |
| --- | --- |
| `Installer/` | Installer EXE offline, checksum, dan petunjuk pemasangan. |
| `MiniMonitor/` | Dependensi dan input build installer, serta EXE portable rilis 3.5.1 sebelumnya. Gunakan installer untuk rilis 3.5.3. |
| `MiniMonitor.zip` | Arsip portable rilis 3.5.1 sebelumnya. Ekstrak seluruh isinya sebelum menjalankan. |
| `Source/` | Kode sumber C#, ikon, manifest, skrip build, dan sumber pengujian. |
| `Dokumentasi/` | Catatan hasil pengujian, perapihan, dan satu arsip riwayat pengembangan. |
| `Pratinjau/` | Tampilan empat tema, tampilan sensor, logo, dan contoh bunyi singkat. |

Untuk memasang atau memperbarui, jalankan `Installer/MiniMonitor-Setup-3.5.3-x64.exe`. Skrip `Pasang-Otomatis.cmd` dan `Perbarui-Aplikasi.cmd` tetap tersedia untuk paket ZIP lama. Panduan lengkap ada di `MiniMonitor/PANDUAN.txt`.

Aplikasi yang sudah terpasang berjalan dari `C:\Program Files\MiniMonitor`. Preferensi pengguna disimpan di `%LOCALAPPDATA%\MiniMonitor\MiniMonitor.settings`.

Build lama, salinan paket, unduhan sementara, dan folder tes sementara sudah dibersihkan. Riwayat kode, skrip, laporan, dan gambar disimpan di `Dokumentasi/Arsip-Pengembangan.zip` (sekitar 1 MB) untuk pemulihan; arsip ini tidak diperlukan untuk menjalankan atau membangun aplikasi terbaru. Folder `work/` dapat dibuat lagi untuk pekerjaan pengembangan berikutnya.

