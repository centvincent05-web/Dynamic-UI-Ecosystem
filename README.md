# Dynamic Island untuk Windows 11 — versi 0.2

Island berbentuk notch yang menempel ke tepi atas layar (sudut sambungannya melengkung cekung, tanpa ujung lancip), plus panel volume di tepi kiri dan kecerahan di tepi kanan.

## Baru di versi 0.2

- Notch kecil: **jam di tengah + baterai** saat diam. Saat musik diputar, notch **melebar sendiri** dan menampilkan cover lagu, jam, dan bar musik.
- Notch **meluncur hilang ke atas** saat ada jendela yang di-maximize atau fullscreen, dan muncul lagi di desktop. Saat tersembunyi, arahkan pointer ke tengah tepi atas layar untuk memunculkannya sementara.
- **Bingkai layar**: strip hitam tipis di atas + sudut layar membulat, tembus klik.
- **Opacity dan warna** island, panel samping, dan bingkai bisa diatur.
- Panel samping: abu-abu gelap, slider tipis dengan titik bulat putih, tanpa angka persen (bisa dinyalakan).

## Isi versi 1

- **Island notch**: jam, media yang sedang diputar (cover, judul, artis, sumber aplikasi), seekbar yang bisa diklik, tombol prev/play/next, kondisi laptop (baterai, CPU, RAM). Membuka saat di-hover/diklik dengan animasi pegas, dan terbuka sebentar saat lagu berganti.
- **Bar musik**: mode *ikuti suara asli* (menangkap audio yang keluar dari speaker), *animasi dekorasi*, atau *mati*.
- **Panel volume (tepi kiri)**: muncul saat pointer ke tepi kiri, diatur dengan scroll. Kalau pointer diam di panel sebentar, muncul daftar **sumber suara per aplikasi** (volume dan mute masing-masing, juga diatur dengan scroll). Aplikasi yang sedang memutar media menampilkan judul lagu/videonya.
- **Panel kecerahan (tepi kanan)**: sama, diatur dengan scroll. Layar laptop lewat WMI, monitor eksternal lewat DDC/CI.
- **Jeda muncul panel**: langsung di desktop, 0,3 detik di jendela biasa, 1,5 detik saat fullscreen. Semua bisa diubah.
- **Menu Pengaturan**: klik kanan island → *Pengaturan*, atau ikon gerigi saat island terbuka. Semua perubahan langsung diterapkan tanpa restart.

## Cara menjalankan

1. Install **.NET 8 SDK** (x64): https://dotnet.microsoft.com/download/dotnet/8.0
2. Ekstrak folder ini, buka **Terminal** di folder `DynamicIsland` (yang berisi `DynamicIsland.csproj`).
3. Untuk mencoba langsung:
   ```
   dotnet run -c Release
   ```
4. Untuk membuat file `.exe` yang bisa dipakai sehari-hari:
   ```
   dotnet publish -c Release -r win-x64 --self-contained false -o publish
   ```
   Lalu jalankan `publish\DynamicIsland.exe`. Centang *Jalankan saat Windows menyala* di Pengaturan supaya otomatis aktif.

Untuk menutup aplikasi: klik kanan island → **Keluar**.

## Cara pakai singkat

| Aksi | Hasil |
|---|---|
| Hover / klik island | Membuka tampilan besar |
| Klik seekbar | Lompat ke bagian lagu |
| Pointer ke tepi kiri layar | Panel volume muncul |
| Scroll di panel | Volume naik/turun |
| Diam sebentar di panel volume | Daftar sumber suara per aplikasi muncul |
| Scroll di baris aplikasi | Volume aplikasi itu saja |
| Klik ikon speaker / ikon aplikasi | Mute / unmute |
| Pointer ke tepi kanan layar | Panel kecerahan muncul |

## Pengaturan

Tersimpan di `%AppData%\DynamicIsland\settings.json`. Paling gampang diubah lewat menu Pengaturan. File JSON-nya juga bisa diedit saat aplikasi tertutup. Hapus file itu untuk kembali ke default.

## Optimisasi yang sudah diterapkan

- Tidak ada render saat diam: loop frame hanya aktif selama ada animasi atau bar musik bergerak.
- Media, volume, dan kecerahan berbasis **event**, bukan polling.
- Penangkapan audio untuk bar musik hanya berjalan saat musik diputar. Bar musik dibatasi 30 fps (bisa diubah).
- Jam diperbarui tepat di pergantian menit, bukan dicek terus.
- Deteksi tepi layar memakai hook mouse yang sangat ringan, tanpa jendela yang menghalangi scrollbar.
- Pengecekan CPU, RAM, dan baterai hanya saat island terbuka.

Cek pemakaian nyatanya di Task Manager. Kalau ada yang terasa berat, catat angkanya (CPU% dan RAM saat diam dan saat animasi).

## Batasan versi 1 (sengaja ditunda)

- **Notifikasi** (Chrome + Phone Link) dan **chat Gemini** → versi 2.
- **Volume per tab Chrome**: Windows melihat semua tab sebagai satu "Google Chrome". Detail per tab butuh ekstensi Chrome kecil → versi 2.
- **Pop-up volume bawaan Windows** masih ikut muncul saat menekan tombol volume di keyboard.
- **Dock pengganti taskbar** → versi 3 (ikon kotak dengan sudut melengkung halus, bukan bulat).
- Saat ini hanya untuk **monitor utama**.

## Kalau ada error

Versi ini ditulis tanpa sempat dijalankan di Windows, jadi kemungkinan ada yang perlu diperbaiki. Kirim:
1. Pesan error lengkap dari terminal (saat `dotnet run`), atau
2. Deskripsi apa yang tidak sesuai (misalnya "panel kanan tidak muncul", "bar musik diam").

## Struktur kode

```
Core/        Settings, animasi pegas, bentuk notch, Windows API
Services/    Media, bar musik (FFT), volume, kecerahan, kondisi laptop, deteksi tepi layar
UI/          Island, panel tepi, menu Pengaturan
```
