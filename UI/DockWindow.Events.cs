using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.UI;

/// <summary>
/// Dock diberi tahu Windows saat jendela aplikasi muncul / hilang / ditutup / ganti fokus (SetWinEventHook),
/// jadi tidak perlu memindai semua jendela tiap detik. Pemindaian berkala tetap ada tapi jarang (cadangan).
/// </summary>
public sealed partial class DockWindow
{
    const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    const uint EVENT_OBJECT_DESTROY = 0x8001, EVENT_OBJECT_SHOW = 0x8002, EVENT_OBJECT_HIDE = 0x8003;
    const uint EVENT_OBJECT_CLOAKED = 0x8017, EVENT_OBJECT_UNCLOAKED = 0x8018;
    const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
    const long WS_CAPTION_ = 0x00C00000L, WS_EX_APPWINDOW = 0x00040000L, WS_EX_TOOLWINDOW_ = 0x00000080L;

    delegate void WinEventDelegate(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll")]
    static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventDelegate proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);

    WinEventDelegate? _winEventProc; // dipegang supaya tidak di-GC
    readonly List<IntPtr> _winHooks = new();
    readonly HashSet<IntPtr> _knownWindows = new();
    DispatcherTimer? _eventRefresh;
    long _lastEventRefresh;

    void StartWindowEvents()
    {
        if (_winHooks.Count > 0) return;
        _winEventProc ??= OnWinEvent;
        void Hook(uint a, uint b)
        {
            var h = SetWinEventHook(a, b, IntPtr.Zero, _winEventProc, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS);
            if (h != IntPtr.Zero) _winHooks.Add(h);
        }
        Hook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND);
        Hook(EVENT_OBJECT_DESTROY, EVENT_OBJECT_HIDE);
        Hook(EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED);
    }

    void StopWindowEvents()
    {
        foreach (var h in _winHooks) UnhookWinEvent(h);
        _winHooks.Clear();
        _eventRefresh?.Stop();
    }

    /// <summary>Dipanggil saat daftar jendela dipindai: ingat jendela mana yang ada di dock.</summary>
    void RememberWindows(List<DockApp> apps)
    {
        _knownWindows.Clear();
        foreach (var a in apps) foreach (var w in a.Windows) _knownWindows.Add(w);
    }

    void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
    {
        if (idObject != 0 || idChild != 0 || hwnd == IntPtr.Zero || _suspended) return; // hanya jendela itu sendiri
        switch (ev)
        {
            case EVENT_OBJECT_DESTROY:
            case EVENT_OBJECT_HIDE:
                if (!_knownWindows.Contains(hwnd)) return; // yang hilang bukan jendela di dock
                break;
            case EVENT_OBJECT_SHOW:
            case EVENT_OBJECT_UNCLOAKED:
            case EVENT_OBJECT_CLOAKED:
                if (Native.GetAncestor(hwnd, 2) != hwnd) return; // bukan jendela utama (GA_ROOT)
                if (ev == EVENT_OBJECT_SHOW && Native.ClassNameOf(hwnd) is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                {
                    TaskbarService.Reapply(); // taskbar Windows muncul sendiri → sembunyikan lagi
                    return;
                }
                if (ev == EVENT_OBJECT_CLOAKED && !_knownWindows.Contains(hwnd)) return;
                if (ev != EVENT_OBJECT_CLOAKED && !LooksLikeAppWindow(hwnd)) return; // tooltip, menu, popup kecil
                break;
        }
        ScheduleRefresh();
    }

    static bool LooksLikeAppWindow(IntPtr hwnd)
    {
        long style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        if ((ex & WS_EX_TOOLWINDOW_) != 0 && (ex & WS_EX_APPWINDOW) == 0) return false;
        return (style & WS_CAPTION_) == WS_CAPTION_ || (ex & WS_EX_APPWINDOW) != 0;
    }

    /// <summary>Pindai ulang sebentar lagi; banyak kejadian beruntun digabung jadi satu, maksimal ±1x per 0,7 detik.</summary>
    void ScheduleRefresh()
    {
        if (_eventRefresh == null)
        {
            _eventRefresh = new DispatcherTimer(DispatcherPriority.Background);
            _eventRefresh.Tick += (_, _) =>
            {
                _eventRefresh.Stop();
                _lastEventRefresh = Environment.TickCount64;
                if (!IsTucked) Refresh();
            };
        }
        if (_eventRefresh.IsEnabled) return;
        long since = Environment.TickCount64 - _lastEventRefresh;
        int delay = since < 700 ? (int)(700 - since) + 120 : 120;
        _eventRefresh.Interval = TimeSpan.FromMilliseconds(delay);
        _eventRefresh.Start();
    }
}
