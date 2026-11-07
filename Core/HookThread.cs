using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace DynamicIsland.Core;

/// <summary>
/// Menjalankan low-level hook (mouse / keyboard) di thread sendiri dengan message loop sendiri.
/// Kalau thread tampilan sedang sibuk, hook tetap menjawab Windows secepatnya, jadi Windows
/// tidak mencabut hook diam-diam (penyebab panel tepi / tombol volume tiba-tiba tidak merespons).
/// </summary>
internal sealed class HookThread : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public int x, y; }

    [DllImport("user32.dll")] static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    const uint WM_QUIT = 0x0012;

    readonly int _hookType;
    readonly Native.LowLevelMouseProc _proc; // dipegang supaya tidak di-GC
    Thread? _thread;
    uint _threadId;
    IntPtr _hook;
    readonly ManualResetEventSlim _started = new();

    /// <param name="hookType">13 = WH_KEYBOARD_LL, 14 = WH_MOUSE_LL.</param>
    /// <param name="proc">Dipanggil di thread hook. Harus sangat cepat; jangan sentuh elemen tampilan.</param>
    public HookThread(int hookType, Native.LowLevelMouseProc proc)
    {
        _hookType = hookType;
        _proc = proc;
    }

    public IntPtr Handle => _hook;
    public bool IsRunning => _thread != null;

    public void Start()
    {
        if (_thread != null) return;
        _started.Reset();
        _thread = new Thread(Run, 256 * 1024) { IsBackground = true, Name = "Hook" + _hookType, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
        _started.Wait(2000);
    }

    void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = Native.SetWindowsHookEx(_hookType, _proc, Native.GetModuleHandle(null), 0);
        _started.Set();
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0) { } // hook dipanggil Windows lewat loop ini
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    public void Stop()
    {
        var t = _thread;
        if (t == null) return;
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        t.Join(1000);
        _thread = null;
    }

    public void Dispose() => Stop();
}
