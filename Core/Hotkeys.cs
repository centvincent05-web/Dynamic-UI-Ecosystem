using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Input;

namespace DynamicIsland.Core;

/// <summary>Teks shortcut ("Ctrl+Alt+Space") ⇄ kode tombol Windows untuk RegisterHotKey.</summary>
public static class Hotkeys
{
    public const uint ModAlt = 0x1, ModCtrl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;

    /// <summary>Shortcut yang gagal didaftarkan (sudah dipakai aplikasi lain).</summary>
    public static readonly HashSet<string> Failed = new(StringComparer.OrdinalIgnoreCase);

    public static bool TryParse(string text, out uint mods, out uint vk)
    {
        mods = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var p in parts.Take(parts.Length - 1))
        {
            switch (p.ToLowerInvariant())
            {
                case "ctrl": case "control": mods |= ModCtrl; break;
                case "alt": mods |= ModAlt; break;
                case "shift": mods |= ModShift; break;
                case "win": case "windows": mods |= ModWin; break;
                default: return false;
            }
        }
        string keyName = parts[^1];
        if (keyName.Length == 1 && char.IsDigit(keyName[0])) keyName = "D" + keyName;
        if (!Enum.TryParse(keyName, true, out Key key) || key == Key.None) return false;
        vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        return mods != 0 && vk != 0; // wajib pakai Ctrl/Alt/Shift/Win supaya tidak bentrok dengan mengetik biasa
    }

    public static string Format(ModifierKeys mods, Key key)
    {
        var parts = new List<string>();
        if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        string name = key.ToString();
        if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1])) name = name[1..];
        parts.Add(name);
        return string.Join("+", parts);
    }

    public static bool IsModifier(Key k) => k is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt
        or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin or Key.System;
}
