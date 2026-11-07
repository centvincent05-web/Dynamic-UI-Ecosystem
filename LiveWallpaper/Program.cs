using System;
using System.Windows;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.LiveWallpaper;

/// <summary>
/// DynamicIsland.LiveWallpaper.exe — dinyalakan oleh app utama: "--wallpaper &lt;pid app&gt;".
/// Hanya menjalankan live wallpaper (kodenya ada di DynamicIsland.dll), lalu keluar sendiri kalau app utama berhenti.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        int i = Array.FindIndex(args, a => a.Equals("--wallpaper", StringComparison.OrdinalIgnoreCase));
        int parent = i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out int p) ? p : 0;

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.DispatcherUnhandledException += (_, e) => { CrashGuard.Log(e.Exception, "live wallpaper"); e.Handled = true; };
        app.Startup += (_, _) =>
        {
            Settings.Load();
            PerfProbe.Start("wallpaper"); // hanya aktif kalau perf-on.txt berisi "on"
            new LiveWallpaperHost(parent, app.Dispatcher).Run();
        };
        return app.Run();
    }
}
