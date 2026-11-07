using System;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

public enum VolumeKey { Up, Down, Mute }

/// <summary>
/// Menangkap tombol volume di keyboard sebelum sampai ke Windows, supaya pop-up volume bawaan
/// Windows tidak muncul dan yang tampil panel volume Dynamic Island.
/// Hook dilepas otomatis saat aplikasi ditutup / crash, dan saat mode game.
/// </summary>
public sealed class VolumeKeyHook : IDisposable
{
    const int WH_KEYBOARD_LL = 13;
    const int WM_KEYDOWN = 0x0100, WM_SYSKEYDOWN = 0x0104;
    const uint VK_VOLUME_MUTE = 0xAD, VK_VOLUME_DOWN = 0xAE, VK_VOLUME_UP = 0xAF;

    [StructLayout(LayoutKind.Sequential)]
    struct KBDLLHOOKSTRUCT
    {
        public uint vkCode, scanCode, flags, time;
        public IntPtr dwExtraInfo;
    }

    readonly Dispatcher _ui;
    readonly HookThread _hook; // di thread sendiri: tetap responsif walau tampilan sedang sibuk

    public event Action<VolumeKey>? Pressed;

    public VolumeKeyHook(Dispatcher ui)
    {
        _ui = ui;
        _hook = new HookThread(WH_KEYBOARD_LL, HookProc);
        Start();
    }

    public bool IsActive => _hook.IsRunning;

    public void Start() => _hook.Start();

    public void Stop() => _hook.Stop();

    IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (k.vkCode is VK_VOLUME_UP or VK_VOLUME_DOWN or VK_VOLUME_MUTE)
            {
                int msg = wParam.ToInt32();
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    var key = k.vkCode == VK_VOLUME_UP ? VolumeKey.Up
                            : k.vkCode == VK_VOLUME_DOWN ? VolumeKey.Down : VolumeKey.Mute;
                    // kerjakan di thread UI; hook harus selesai secepat mungkin
                    _ui.BeginInvoke(new Action(() => Pressed?.Invoke(key)));
                }
                return (IntPtr)1; // telan tombolnya: Windows tidak menampilkan pop-up volume
            }
        }
        return Native.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
    }

    public void Dispose() => Stop();
}
