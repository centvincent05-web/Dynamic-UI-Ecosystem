using System;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;

namespace DynamicIsland.Core;

/// <summary>
/// Mencegah "bayangan" sesaat saat jendela transparan (dock, island) dipindah + diubah ukurannya.
/// Penyebab: Windows menampilkan gambar LAMA jendela di posisi BARU selama satu frame, sebelum WPF sempat
/// menggambar ulang — itu yang terlihat sebagai dock "melayang" sepersekian detik saat menu dibuka.
/// Solusi: jendela disembunyikan oleh DWM (cloak) tepat sebelum dipindah, lalu dimunculkan lagi setelah
/// WPF selesai menggambar isi barunya (±2 frame). Isinya tetap digambar selama tersembunyi, jadi tidak ada
/// jeda; yang hilang hanya frame yang salah.
/// </summary>
public static class WindowCloak
{
    const int DWMWA_CLOAK = 13;

    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static void Run(IntPtr hwnd, Dispatcher dispatcher, Action move)
    {
        if (hwnd == IntPtr.Zero) { move(); return; }
        bool cloaked = Set(hwnd, true);
        try { move(); }
        finally
        {
            if (cloaked)
            {
                int frames = 0;
                bool done = false;
                void Uncloak()
                {
                    if (done) return;
                    done = true;
                    CompositionTarget.Rendering -= OnRender;
                    Set(hwnd, false);
                }
                void OnRender(object? s, EventArgs e)
                {
                    // tunggu 2 frame: frame pertama menggambar isi baru, frame kedua memastikan sudah tampil
                    if (++frames >= 2) dispatcher.BeginInvoke(new Action(Uncloak), DispatcherPriority.Background);
                }
                CompositionTarget.Rendering += OnRender;
                // pengaman: apa pun yang terjadi, jendela muncul lagi paling lambat 120 ms
                var safety = new DispatcherTimer(DispatcherPriority.Normal, dispatcher) { Interval = TimeSpan.FromMilliseconds(120) };
                safety.Tick += (_, _) => { safety.Stop(); Uncloak(); };
                safety.Start();
            }
        }
    }

    static bool Set(IntPtr hwnd, bool on)
    {
        try
        {
            int v = on ? 1 : 0;
            return DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref v, sizeof(int)) == 0;
        }
        catch { return false; }
    }
}
