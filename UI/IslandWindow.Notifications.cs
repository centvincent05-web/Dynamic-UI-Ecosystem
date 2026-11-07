using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynamicIsland.Core;
using DynamicIsland.Services;

namespace DynamicIsland.UI;

/// <summary>Island: notifikasi Windows (halaman + notifikasi sebentar di notch).</summary>
public partial class IslandWindow
{
    // =====================================================================
    // Notifikasi Windows
    // =====================================================================

    NotificationService? _notifs;

    public void AttachNotifications(NotificationService n)
    {
        _notifs = n;
        n.Changed += () => { RememberNotifApps(n.Items); RebuildNotifList(); };
        n.Arrived += item =>
        {
            var st = Settings.Current;
            RememberNotifApps(new[] { item });
            // aplikasi yang dimatikan di Pengaturan: tetap masuk daftar, tapi tidak muncul di notch
            if (st.NotifPeek && st.NotificationsEnabled && !st.IsNotifMuted(item.AppId)) ShowNotificationAlert(item);
        };
        RememberNotifApps(n.Items);
        NotifClearButton.Click += (_, _) => n.HideAll();
        NotifCenterButton.Click += (_, _) => RadioService.OpenSettings("ms-actioncenter:");
        NotifPeek.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            _alertActive = false; _alertA = 0; // notifikasi kecil langsung berganti jadi island besar
            if (_alertDevice) { RadioService.OpenSettings("ms-settings:bluetooth"); return; }
            if (_expanded) SetMode(IslandMode.Notifications, animate: true); else Expand(IslandMode.Notifications);
        };
        // scroll di daftar: gulir daftarnya; kalau sudah di ujung, pindah halaman
        NotifScroll.PreviewMouseWheel += (sender, e) =>
        {
            bool down = e.Delta < 0;
            bool canScroll = down ? NotifScroll.VerticalOffset < NotifScroll.ScrollableHeight - 0.5 : NotifScroll.VerticalOffset > 0.5;
            if (canScroll)
            {
                NotifScroll.ScrollToVerticalOffset(NotifScroll.VerticalOffset - e.Delta * 0.4);
                e.Handled = true;
                _pageWheel = 0;
                return;
            }
            OnPageWheel(sender, e);
            e.Handled = true;
        };
        RebuildNotifList();
        UpdateDots();
    }

    void ShowNotificationAlert(NotificationItem n)
    {
        if (_suspended || _expanded) { if (_expanded && _mode == IslandMode.Notifications) RebuildNotifList(); return; }
        ResetPeekForNotification();
        _alertNotif = true;
        _alertPlug = false;
        _alertDownload = false;
        _alertHud = false;
        _alertW = NotifPeekWidth;
        _alertH = 60;
        NotifPeekApp.Text = n.Attribution.Length > 0 ? $"{n.AppName} · {n.Attribution}" : n.AppName;
        NotifPeekTitle.Text = n.Title;
        NotifPeekBody.Text = n.Body;
        NotifPeekBody.Visibility = n.Body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifPeekIcon.Source = AppIcon(n.AppId);
        BeginAlert();
    }

    /// <summary>Pesan singkat dari Dynamic Island sendiri (mis. "dipulihkan setelah error"), memakai tampilan notifikasi.</summary>
    public void ShowSystemNotice(string title, string body)
    {
        if (_suspended || _expanded) return;
        ResetPeekForNotification();
        _alertNotif = true;
        _alertPlug = false;
        _alertDownload = false;
        _alertHud = false;
        _alertW = NotifPeekWidth;
        _alertH = 60;
        NotifPeekApp.Text = "Dynamic Island";
        NotifPeekTitle.Text = title;
        NotifPeekBody.Text = body;
        NotifPeekBody.Visibility = body.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifPeekIcon.Source = null;
        BeginAlert();
    }

    /// <summary>Catat aplikasi yang pernah mengirim notifikasi (untuk daftar filter di Pengaturan).</summary>
    static void RememberNotifApps(IEnumerable<NotificationItem> items)
    {
        var known = Settings.Current.NotifKnownApps;
        bool added = false;
        foreach (var n in items)
        {
            if (string.IsNullOrEmpty(n.AppId) || known.ContainsKey(n.AppId)) continue;
            known[n.AppId] = n.AppName;
            added = true;
        }
        if (added) Settings.Save();
    }

    static ImageSource? AppIcon(string appId)
    {
        if (string.IsNullOrEmpty(appId)) return null;
        try { return IconLoader.Get("shell:AppsFolder\\" + appId, 64).Image; } catch { return null; }
    }

    static string Ago(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return L.T("baru saja");
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes}{L.T(" mnt")}";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours}{L.T(" jam")}";
        return t.ToString("d MMM", L.Culture);
    }

    static readonly Brush NotifRowBg = MakeFrozen(Color.FromArgb(0xC0, 0x1F, 0x1F, 0x21));

    void RebuildNotifList()
    {
        if (_notifs == null) return;
        var items = _notifs.Items;
        NotifRows.Children.Clear();
        NotifCount.Text = items.Count > 0 ? items.Count.ToString() : "";
        NotifEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NotifClearButton.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        foreach (var n in items)
        {
            var icon = new Border
            {
                Width = 30, Height = 30, CornerRadius = new CornerRadius(8), ClipToBounds = true, VerticalAlignment = VerticalAlignment.Top,
                Background = TileOff, Margin = new Thickness(0, 1, 10, 0),
                Child = new Image { Source = AppIcon(n.AppId), Stretch = Stretch.UniformToFill },
            };
            var head = new Grid();
            head.Children.Add(new TextBlock
            {
                Text = n.Attribution.Length > 0 ? $"{n.AppName} · {n.Attribution}" : n.AppName,
                Foreground = Subtle, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 0, 70, 0),
            });
            var time = new TextBlock { Text = Ago(n.Arrived), Foreground = Subtle, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 22, 0) };
            head.Children.Add(time);
            var close = new TextBlock
            {
                Text = "✕", Foreground = Subtle, FontSize = 11, HorizontalAlignment = HorizontalAlignment.Right, Cursor = Cursors.Hand,
                Padding = new Thickness(4, 0, 2, 0), Background = Brushes.Transparent, Opacity = 0,
            };
            var item = n;
            close.MouseLeftButtonUp += (_, e) => { e.Handled = true; _notifs.Hide(item); };
            head.Children.Add(close);
            var text = new StackPanel();
            text.Children.Add(head);
            text.Children.Add(new TextBlock { Text = n.Title, Foreground = Brushes.White, FontSize = 12.5, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 1, 0, 0) });
            if (n.Body.Length > 0)
                text.Children.Add(new TextBlock { Text = n.Body, Foreground = Brushes.Gainsboro, FontSize = 11.5, TextWrapping = TextWrapping.Wrap, TextTrimming = TextTrimming.CharacterEllipsis, MaxHeight = 32 });
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition());
            grid.Children.Add(icon);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            var row = new Border
            {
                Background = NotifRowBg, CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 0, 6), Child = grid, Cursor = Cursors.Hand,
            };
            row.MouseEnter += (_, _) => close.Opacity = 1;
            row.MouseLeave += (_, _) => close.Opacity = 0;
            row.MouseLeftButtonUp += (_, e) => { e.Handled = true; RadioService.OpenSettings("ms-actioncenter:"); };
            // klik kanan: atur apakah aplikasi ini boleh muncul di notch
            if (!string.IsNullOrEmpty(item.AppId))
            {
                bool muted = Settings.Current.IsNotifMuted(item.AppId);
                var toggle = new MenuItem
                {
                    Header = muted ? string.Format(L.T("Munculkan lagi notifikasi {0} di notch"), item.AppName)
                                   : string.Format(L.T("Jangan munculkan notifikasi {0} di notch"), item.AppName),
                };
                toggle.Click += (_, _) =>
                {
                    Settings.Current.SetNotifMuted(item.AppId, !muted);
                    Settings.NotifyChanged();
                    Settings.Save();
                    RebuildNotifList();
                };
                row.ContextMenu = new ContextMenu { Items = { toggle } };
            }
            NotifRows.Children.Add(row);
        }
    }
}
