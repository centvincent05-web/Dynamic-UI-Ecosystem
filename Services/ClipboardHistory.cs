using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using DynamicIsland.Core;

namespace DynamicIsland.Services;

/// <summary>
/// Riwayat teks yang disalin (hanya di memori, tidak pernah disimpan ke disk).
/// Data dari password manager (yang menandai dirinya "jangan direkam") dilewati.
/// </summary>
public sealed class ClipboardHistory
{
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    const int WM_CLIPBOARDUPDATE = 0x031D;
    const int Max = 12;

    readonly List<string> _items = new();
    readonly Dispatcher _ui;
    IntPtr _hwnd;
    string? _ignoreNext;

    public IReadOnlyList<string> Items => _items;
    public event Action? Changed;

    public ClipboardHistory(Dispatcher ui) => _ui = ui;

    public void Attach(Window w)
    {
        _hwnd = new WindowInteropHelper(w).Handle;
        if (_hwnd == IntPtr.Zero) return;
        AddClipboardFormatListener(_hwnd);
        HwndSource.FromHwnd(_hwnd)?.AddHook((IntPtr h, int msg, IntPtr wp, IntPtr lp, ref bool handled) =>
        {
            if (msg == WM_CLIPBOARDUPDATE && Settings.Current.PageClipboard)
                _ui.BeginInvoke(new Action(Read), DispatcherPriority.Background); // baca setelah aplikasi lain selesai menyalin
            return IntPtr.Zero;
        });
    }

    void Read()
    {
        string? text = null;
        for (int attempt = 0; attempt < 3 && text == null; attempt++)
        {
            try
            {
                var data = Clipboard.GetDataObject();
                if (data == null) return;
                // password manager & aplikasi sensitif menandai isinya supaya tidak direkam
                if (data.GetDataPresent("ExcludeClipboardContentFromMonitorProcessing")) return;
                if (data.GetDataPresent("CanIncludeInClipboardHistory") &&
                    data.GetData("CanIncludeInClipboardHistory") is System.IO.MemoryStream ms && ms.Length >= 4 && ms.ToArray()[0] == 0) return;
                if (!data.GetDataPresent(DataFormats.UnicodeText)) return;
                text = data.GetData(DataFormats.UnicodeText) as string;
            }
            catch { System.Threading.Thread.Sleep(30); } // clipboard sedang dipakai aplikasi lain
        }
        if (string.IsNullOrWhiteSpace(text)) return;
        if (text.Length > 4000) text = text[..4000];
        if (text == _ignoreNext) { _ignoreNext = null; }
        _items.Remove(text);
        _items.Insert(0, text);
        if (_items.Count > Max) _items.RemoveRange(Max, _items.Count - Max);
        Changed?.Invoke();
    }

    /// <summary>Salin ulang salah satu item (naik ke paling atas).</summary>
    public void Copy(string text)
    {
        try
        {
            _ignoreNext = text;
            Clipboard.SetText(text);
        }
        catch { }
    }

    public void Remove(string text) { _items.Remove(text); Changed?.Invoke(); }
    public void Clear() { _items.Clear(); Changed?.Invoke(); }

    public void Detach()
    {
        if (_hwnd != IntPtr.Zero) RemoveClipboardFormatListener(_hwnd);
    }
}
