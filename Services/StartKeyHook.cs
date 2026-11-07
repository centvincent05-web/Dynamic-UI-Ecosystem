using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>
/// Tombol Windows ditekan SENDIRIAN → buka menu dock Dynamic Island, bukan Start Windows.
/// Kombinasi (Win+E, Win+D, Win+Shift+S, Win+V, …) tetap jalan normal karena tombol Win-nya diteruskan.
///
/// Caranya sama seperti Open-Shell / AutoHotkey: saat Win dilepas tanpa tombol lain, pelepasan aslinya ditahan,
/// lalu dikirim ulang dengan satu tombol "kosong" (VK 0xE8, tidak dipakai apa pun) di antaranya.
/// Windows mengira itu kombinasi Win+tombol, jadi Start tidak terbuka. Tidak ada kode yang disuntikkan ke explorer.
/// Ctrl+Esc tetap membuka Start Windows.
/// </summary>
public sealed class StartKeyHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const uint VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_MASK = 0xE8;
    const uint LLKHF_INJECTED = 0x10;
    const uint KEYEVENTF_KEYUP = 0x0002;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);

    readonly Dispatcher _ui;
    readonly HookThread _hook;
    volatile bool _winDown, _otherPressed;

    /// <summary>Dipanggil di thread UI saat tombol Windows ditekan sendirian.</summary>
    public event Action? WinPressedAlone;

    /// <summary>False = hook tetap terpasang tapi tidak melakukan apa-apa (mis. saat mode game).</summary>
    public volatile bool Active = true;

    public StartKeyHook(Dispatcher ui)
    {
        _ui = ui;
        _hook = new HookThread(WH_KEYBOARD_LL, HookProc);
    }

    public bool IsRunning => _hook.IsRunning;
    public void Start() => _hook.Start();
    public void Stop() => _hook.Stop();
    public void Dispose() => Stop();

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Active)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            bool injected = (k.flags & LLKHF_INJECTED) != 0;
            int msg = wParam.ToInt32();
            bool down = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
            bool up = msg is WM_KEYUP or WM_SYSKEYUP;
            bool isWin = k.vkCode is VK_LWIN or VK_RWIN;

            if (!injected) // tombol yang dikirim program (termasuk kiriman kita sendiri) tidak dihitung
            {
                if (isWin && down)
                {
                    if (!_winDown) { _winDown = true; _otherPressed = false; }
                }
                else if (!isWin && down && _winDown)
                {
                    _otherPressed = true; // kombinasi Win+tombol: biarkan Windows yang menangani
                }
                else if (isWin && up && _winDown)
                {
                    _winDown = false;
                    if (!_otherPressed)
                    {
                        // tahan pelepasan asli, kirim ulang dengan tombol kosong di antaranya → Start tidak terbuka
                        keybd_event((byte)VK_MASK, 0, 0, UIntPtr.Zero);
                        keybd_event((byte)VK_MASK, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        keybd_event((byte)k.vkCode, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
                        _ui.BeginInvoke(new Action(() => WinPressedAlone?.Invoke()));
                        return (IntPtr)1;
                    }
                }
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }
}
