using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media.Animation;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AgosClip;

public partial class PanelWindow : Window
{
    readonly ObservableCollection<DayCard> _cards = new();
    readonly ObservableCollection<ItemVm> _items = new();
    IntPtr _hwnd, _target;
    bool _muteDayChange, _muteStoreChange, _pasting, _reloadQueued;
    SortedDictionary<DateOnly, int> _counts = new();
    DateOnly _calMonth;
    readonly DispatcherTimer _searchDebounce;

    static Store Store => App.Current.Store;

    public PanelWindow()
    {
        InitializeComponent();
        DayList.ItemsSource = _cards;
        ItemList.ItemsSource = _items;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        // tool window: no Alt+Tab entry
        Native.SetWindowLongPtr(_hwnd, -20, (IntPtr)((long)Native.GetWindowLongPtr(_hwnd, -20) | 0x80));
        // No system menu → DWM draws no caption buttons (the stray red ✕) over the glass frame.
        Native.SetWindowLongPtr(_hwnd, -16, (IntPtr)((long)Native.GetWindowLongPtr(_hwnd, -16) & ~0x00080000L & ~0x00020000L & ~0x00010000L));
        Deactivated += (_, _) => { if (!_pasting && !Snapshot && !_sticky) HidePanel(); };
        // Remember the last window the user worked in, so a pinned panel knows where to paste.
        _fgProc = (_, _, hwnd, _, _, _, _) =>
        {
            if (hwnd == IntPtr.Zero || hwnd == _hwnd) return;
            Native.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != (uint)Environment.ProcessId) _lastExternal = hwnd;
        };
        _fgHook = Native.SetWinEventHook(Native.EVENT_SYSTEM_FOREGROUND, Native.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _fgProc, 0, 0, Native.WINEVENT_OUTOFCONTEXT);
        Closed += (_, _) => { if (_fgHook != IntPtr.Zero) Native.UnhookWinEvent(_fgHook); };
        PreviewKeyDown += OnKey;
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); if (!Snapshot) LoadItems(0); };
        Store.Changed += () =>
        {
            if (_muteStoreChange || !IsVisible || _reloadQueued) return;
            _reloadQueued = true;
            Dispatcher.BeginInvoke(() => { _reloadQueued = false; Reload(keepIndex: true); }, DispatcherPriority.Background);
        };
    }

    // ───────────── show / hide ─────────────

    readonly Native.WinEventProc _fgProc; // keep delegate alive
    readonly IntPtr _fgHook;
    IntPtr _lastExternal;
    bool _sticky;

    /// <summary>Pinned ("固定在桌面"): stays open when clicking elsewhere and after pasting.</summary>
    public bool IsSticky => _sticky;

    void SetSticky(bool on)
    {
        _sticky = on;
        StickyBtn.Content = on ? "\uE840" : "\uE718"; // PinFill / Pin
        if (on)
        {
            StickyBtn.SetResourceReference(ForegroundProperty, "Accent");
            StickyBtn.SetResourceReference(BackgroundProperty, "SelBg");
            StickyBtn.ToolTip = "已固定在桌面 · 点击取消";
        }
        else
        {
            StickyBtn.ClearValue(ForegroundProperty);
            StickyBtn.ClearValue(BackgroundProperty);
            StickyBtn.ToolTip = "固定在桌面：点别处或粘贴后不自动关闭";
        }
    }

    void Sticky_Click(object sender, RoutedEventArgs e) => SetSticky(!_sticky);

    /// <summary>Win+V while pinned but in the background: bring it forward instead of closing.</summary>
    public void FocusSticky()
    {
        Activate();
        Native.ForceForeground(_hwnd);
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
    }

    // ───────────── moving ─────────────

    void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // only the header strip (title, grip, empty space) drags; buttons/fields/items handle their own clicks
        if (e.Handled || e.GetPosition(Header).Y > Header.ActualHeight + 4) return;
        var s = App.Current.Settings;
        if (e.ClickCount == 2)
        {
            s.PanelX = s.PanelY = null;
            s.Save();
            PlaceNear(_target);
            e.Handled = true;
            return;
        }
        try { DragMove(); } catch (InvalidOperationException) { return; }
        if (Native.GetWindowRect(_hwnd, out var r))
        {
            s.PanelX = r.Left;
            s.PanelY = r.Top;
            s.Save();
        }
        e.Handled = true;
    }

    public void ShowFor(IntPtr target)
    {
        Watchdog.Step("open panel");
        if (target == _hwnd || !Native.IsWindow(target)) target = IntPtr.Zero;
        if (IsVisible && _sticky) { if (target != IntPtr.Zero) _target = target; FocusSticky(); return; }
        _target = target;
        Glass.Apply(this);
        PausedBadge.Visibility = App.Paused ? Visibility.Visible : Visibility.Collapsed;

        _searchDebounce.Stop();
        SearchBox.TextChanged -= Search_TextChanged;
        SearchBox.Text = "";
        SearchBox.TextChanged += Search_TextChanged;
        RebuildCards(null);
        LoadItems(0);

        CalOverlay.Visibility = Visibility.Collapsed;
        PlaceNear(target);   // move while hidden so it never flashes at the old spot
        Show();
        PlaceNear(target);
        PlayOpenAnimation();
        Activate();
        Native.ForceForeground(_hwnd);
        SearchBox.Focus();
        Keyboard.Focus(SearchBox);
        ScrollCardsToStart();
    }

    void PlayOpenAnimation()
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(170);
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
        RootScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.97, 1, dur) { EasingFunction = ease });
        RootScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.97, 1, dur) { EasingFunction = ease });
    }

    bool CalOpen
    {
        get => CalOverlay.Visibility == Visibility.Visible;
        set => CalOverlay.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    internal bool Snapshot;

    /// <summary>Self-test only: lays the panel out with today's data selected, without activation logic.</summary>
    internal void PrepareSnapshot(int dayIndex, string? search = null, bool openCalendar = false)
    {
        Snapshot = true;
        RebuildCards(null);
        if (dayIndex >= 0) DayList.SelectedIndex = dayIndex;
        if (search != null) SearchBox.Text = search;
        LoadItems(0);
        if (openCalendar)
        {
            _calMonth = new DateOnly(DateTime.Now.Year, DateTime.Now.Month, 1);
            BuildCalendar();
            CalOpen = true;
        }
        else CalOpen = false;
    }


    /// <summary>Closes the panel (✕, Esc, Win+V). Closing explicitly also un-pins it.</summary>
    public void HidePanel()
    {
        if (_sticky) SetSticky(false);
        CalOpen = false;
        if (IsVisible) Hide();
    }

    internal void PlaceNear(IntPtr target)
    {
        if (TryPlaceRemembered()) return;
        Native.POINT pt = default;
        int caretH = 0;
        bool haveCaret = false;
        if (target != IntPtr.Zero)
        {
            uint tid = Native.GetWindowThreadProcessId(target, out _);
            var gti = new Native.GUITHREADINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.GUITHREADINFO>() };
            if (Native.GetGUIThreadInfo(tid, ref gti) && gti.hwndCaret != IntPtr.Zero)
            {
                pt = new Native.POINT { X = gti.rcCaret.Left, Y = gti.rcCaret.Bottom };
                Native.ClientToScreen(gti.hwndCaret, ref pt);
                caretH = Math.Max(0, gti.rcCaret.Bottom - gti.rcCaret.Top);
                haveCaret = pt.X != 0 || pt.Y != 0;
            }
        }
        if (!haveCaret) { Native.GetCursorPos(out pt); caretH = 0; }

        var mon = Native.MonitorFromPoint(pt, 2 /* NEAREST */);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref mi);
        double scale = 1;
        if (Native.GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0) scale = dpi / 96.0;
        int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale), gap = (int)(6 * scale);
        var wa = mi.rcWork;

        int x = pt.X, y = pt.Y + gap;
        if (y + h > wa.Bottom) y = pt.Y - caretH - h - gap;
        x = Math.Clamp(x, wa.Left + gap, Math.Max(wa.Left + gap, wa.Right - w - gap));
        y = Math.Clamp(y, wa.Top + gap, Math.Max(wa.Top + gap, wa.Bottom - h - gap));
        Native.SetWindowPos(_hwnd, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010); // NOSIZE | NOACTIVATE, topmost
    }

    /// <summary>Opens where the user last dragged it, if that spot is still on a connected monitor.</summary>
    bool TryPlaceRemembered()
    {
        var s = App.Current.Settings;
        if (s.PanelX is not int x || s.PanelY is not int y) return false;
        // a point a little inside the panel's top-left must be on some monitor (monitor may have been unplugged)
        var mon = Native.MonitorFromPoint(new Native.POINT { X = x + 40, Y = y + 20 }, 0 /* NULL */);
        if (mon == IntPtr.Zero) return false;
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(mon, ref mi);
        double scale = 1;
        if (Native.GetDpiForMonitor(mon, 0, out uint dpi, out _) == 0) scale = dpi / 96.0;
        int w = (int)Math.Round(Width * scale), h = (int)Math.Round(Height * scale);
        var wa = mi.rcWork;
        x = Math.Clamp(x, wa.Left, Math.Max(wa.Left, wa.Right - w));
        y = Math.Clamp(y, wa.Top, Math.Max(wa.Top, wa.Bottom - h));
        Native.SetWindowPos(_hwnd, new IntPtr(-1), x, y, 0, 0, 0x0001 | 0x0010);
        return true;
    }

    // ───────────── data ─────────────

    DayCard? CurrentCard => DayList.SelectedItem as DayCard;
    string Query => SearchBox.Text.Trim();

    void Reload(bool keepIndex)
    {
        int idx = ItemList.SelectedIndex;
        RebuildCards(CurrentCard);
        LoadItems(keepIndex ? Math.Max(0, idx) : 0);
    }

    /// <summary>Rebuilds the date cards; keeps the given card selected if it still exists, else selects today.</summary>
    void RebuildCards(DayCard? keep)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        _counts = Store.DayCounts();
        int pinned = Store.PinnedCount();

        _muteDayChange = true;
        _cards.Clear();
        if (pinned > 0) _cards.Add(DayCard.Pinned(pinned));
        _cards.Add(DayCard.ForDay(today, _counts.GetValueOrDefault(today), today));
        foreach (var (d, n) in _counts.Reverse())
            if (d != today) _cards.Add(DayCard.ForDay(d, n, today));

        DayCard? sel = null;
        if (keep != null)
            sel = keep.IsPinned ? _cards.FirstOrDefault(c => c.IsPinned) : _cards.FirstOrDefault(c => !c.IsPinned && c.Date == keep.Date);
        sel ??= _cards.First(c => !c.IsPinned && c.Date == today);
        DayList.SelectedItem = sel;
        _muteDayChange = false;
        DayList.ScrollIntoView(sel);
    }

    void LoadItems(int selectIndex)
    {
        Watchdog.Step("load list");
        var q = Query;
        List<ClipItem> rows;
        bool showDate = false;
        if (q.Length > 0)
        {
            rows = Store.Search(q);
            showDate = true;
            SectionTitle.Text = rows.Count == 0 ? $"搜索“{q}”" : $"搜索“{q}” · {rows.Count} 条结果";
            EmptyText.Text = "没有找到匹配的内容";
            DayList.Opacity = 0.5;
        }
        else
        {
            DayList.Opacity = 1;
            var card = CurrentCard;
            if (card == null) { rows = new(); }
            else if (card.IsPinned)
            {
                rows = Store.PinnedItems();
                showDate = true;
                SectionTitle.Text = $"固定的条目 · {rows.Count} 条 · 不会被自动清理";
                EmptyText.Text = "还没有固定的条目";
            }
            else
            {
                rows = Store.ItemsOnDay(card.Date);
                var d = card.Date;
                SectionTitle.Text = $"{d.Year}年{d.Month}月{d.Day}日 {DayCard.WeekName(d)} · {rows.Count} 条";
                EmptyText.Text = card.Date == DateOnly.FromDateTime(DateTime.Now)
                    ? "今天还没有复制过内容\n复制文字或图片后会出现在这里"
                    : "这一天没有记录";
            }
        }

        _items.Clear();
        foreach (var r in rows) _items.Add(new ItemVm(r, showDate));
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_items.Count > 0)
        {
            ItemList.SelectedIndex = Math.Min(selectIndex, _items.Count - 1);
            ItemList.ScrollIntoView(ItemList.SelectedItem);
        }
    }

    // ───────────── actions ─────────────

    ItemVm? Selected => ItemList.SelectedItem as ItemVm;

    void Paste(ItemVm vm)
    {
        _pasting = true;
        Watchdog.Step(vm.IsImage ? $"paste: put image {vm.Item.Width}x{vm.Item.Height} on clipboard" : "paste: put text on clipboard");
        try
        {
            if (!Paster.SetClipboard(vm.Item))
            {
                EmptyText.Text = "无法写入剪贴板（图片文件可能已被删除）";
                EmptyState.Visibility = Visibility.Visible;
                return;
            }
            if (_sticky)
            {
                // stay open; paste into the window the user was last working in
                var target = _lastExternal != IntPtr.Zero && Native.IsWindow(_lastExternal) ? _lastExternal : _target;
                if (App.Current.Settings.PasteAfterSelect && target != IntPtr.Zero && Native.IsWindow(target))
                    Paster.PasteInto(target, Dispatcher);
                return;
            }
            HidePanel();
            if (App.Current.Settings.PasteAfterSelect && _target != IntPtr.Zero)
                Paster.PasteInto(_target, Dispatcher);
        }
        finally { _pasting = false; }
    }

    void TogglePin(ItemVm vm)
    {
        bool now = !vm.Pinned;
        _muteStoreChange = true;
        try { Store.SetPinned(vm.Item.Id, now); } finally { _muteStoreChange = false; }
        vm.Pinned = now;
        var card = CurrentCard;
        RebuildCards(card);
        if (!now && card is { IsPinned: true } && Query.Length == 0)
        {
            if (CurrentCard is { IsPinned: true }) RemoveItem(vm);
            else LoadItems(0); // pinned card vanished (no pinned left) → today's list
        }
    }

    void DeleteItem(ItemVm vm)
    {
        _muteStoreChange = true;
        try { Store.Delete(vm.Item.Id); } finally { _muteStoreChange = false; }
        var card = CurrentCard;
        RebuildCards(card);
        bool sameView = Query.Length > 0 || SameCard(card, CurrentCard);
        if (sameView) RemoveItem(vm);
        else LoadItems(0); // the day's last item was deleted, so its card is gone
    }

    static bool SameCard(DayCard? a, DayCard? b) =>
        a != null && b != null && a.IsPinned == b.IsPinned && (a.IsPinned || a.Date == b.Date);

    void RemoveItem(ItemVm vm)
    {
        int i = _items.IndexOf(vm);
        if (i < 0) return;
        _items.RemoveAt(i);
        EmptyState.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_items.Count > 0) ItemList.SelectedIndex = Math.Min(i, _items.Count - 1);
    }

    void MoveDay(int delta)
    {
        int i = DayList.SelectedIndex + delta;
        if (i < 0 || i >= _cards.Count) return;
        DayList.SelectedIndex = i;
        DayList.ScrollIntoView(DayList.SelectedItem);
    }

    void OnKey(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool searching = SearchBox.Text.Length > 0;
        switch (e.Key)
        {
            case Key.Escape:
                if (CalOpen) CalOpen = false;
                else if (searching) SearchBox.Text = "";
                else HidePanel();
                break;
            case Key.Down:
                if (_items.Count > 0) { ItemList.SelectedIndex = Math.Min(_items.Count - 1, ItemList.SelectedIndex + 1); ItemList.ScrollIntoView(ItemList.SelectedItem); }
                break;
            case Key.Up:
                if (_items.Count > 0) { ItemList.SelectedIndex = Math.Max(0, ItemList.SelectedIndex - 1); ItemList.ScrollIntoView(ItemList.SelectedItem); }
                break;
            case Key.PageDown:
                if (_items.Count > 0) { ItemList.SelectedIndex = Math.Min(_items.Count - 1, ItemList.SelectedIndex + 5); ItemList.ScrollIntoView(ItemList.SelectedItem); }
                break;
            case Key.PageUp:
                if (_items.Count > 0) { ItemList.SelectedIndex = Math.Max(0, ItemList.SelectedIndex - 5); ItemList.ScrollIntoView(ItemList.SelectedItem); }
                break;
            case Key.Enter:
                if (Selected is { } s) Paste(s);
                break;
            case Key.Left when !searching:
                MoveDay(-1);
                break;
            case Key.Right when !searching:
                MoveDay(+1);
                break;
            case Key.Delete when !searching || ctrl:
                if (Selected is { } d) DeleteItem(d);
                break;
            case Key.P when ctrl:
                if (Selected is { } p) TogglePin(p);
                break;
            case Key.Tab:
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // ───────────── event handlers ─────────────

    void Item_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: ItemVm vm }) Paste(vm);
    }

    void Pin_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ItemVm vm }) TogglePin(vm);
        e.Handled = true;
    }

    void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ItemVm vm }) DeleteItem(vm);
        e.Handled = true;
    }

    void Settings_Click(object sender, RoutedEventArgs e) => App.Current.ShowSettings();
    void Close_Click(object sender, RoutedEventArgs e) => HidePanel();

    void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    // Day cards are not focusable (focus stays in the search box), and ListBoxItem only selects on
    // click when it can take focus, so select them by hand.
    void DayCard_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is ListBoxItem { DataContext: DayCard card })
        {
            DayList.SelectedItem = card;
            e.Handled = true;
        }
    }

    void DayList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_muteDayChange || DayList.SelectedItem == null) return;
        if (SearchBox.Text.Length > 0)
        {
            SearchBox.TextChanged -= Search_TextChanged;
            SearchBox.Text = "";
            SearchBox.TextChanged += Search_TextChanged;
        }
        LoadItems(0);
        ItemList.ScrollIntoView(ItemList.SelectedItem);
        SearchBox.Focus();
    }

    ScrollViewer? CardScroller => FindChild<ScrollViewer>(DayList);

    void ScrollCardsToStart() => Dispatcher.BeginInvoke(() =>
    {
        if (DayList.SelectedItem != null) DayList.ScrollIntoView(DayList.SelectedItem);
        if (DayList.SelectedIndex <= 1) CardScroller?.ScrollToHorizontalOffset(0);
    }, DispatcherPriority.Loaded);

    void DayList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (CardScroller is { } sv)
        {
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta * 0.6);
            e.Handled = true;
        }
    }

    static T? FindChild<T>(DependencyObject root) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) return t;
            if (FindChild<T>(c) is { } found) return found;
        }
        return null;
    }

    // ───────────── calendar ─────────────

    void Cal_Click(object sender, RoutedEventArgs e)
    {
        var basis = CurrentCard is { IsPinned: false } c ? c.Date : DateOnly.FromDateTime(DateTime.Now);
        _calMonth = new DateOnly(basis.Year, basis.Month, 1);
        if (CalOpen) { CalOpen = false; return; }
        BuildCalendar();
        CalOpen = true;
    }

    void CalBackdrop_Click(object sender, MouseButtonEventArgs e) { CalOpen = false; e.Handled = true; }

    void CalPrev_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(-1); BuildCalendar(); }
    void CalNext_Click(object sender, RoutedEventArgs e) { _calMonth = _calMonth.AddMonths(1); BuildCalendar(); }

    void BuildCalendar()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var thisMonth = new DateOnly(today.Year, today.Month, 1);
        var earliest = _counts.Count > 0 ? _counts.Keys.First() : today;
        CalTitle.Text = $"{_calMonth.Year}年{_calMonth.Month}月";
        CalNext.IsEnabled = _calMonth < thisMonth;
        CalPrev.IsEnabled = _calMonth > new DateOnly(earliest.Year, earliest.Month, 1);

        var selected = CurrentCard is { IsPinned: false } c ? c.Date : (DateOnly?)null;
        CalGrid.Children.Clear();
        int offset = ((int)_calMonth.DayOfWeek + 6) % 7; // Monday first
        for (int i = 0; i < offset; i++) CalGrid.Children.Add(new Border());
        int days = DateTime.DaysInMonth(_calMonth.Year, _calMonth.Month);
        for (int d = 1; d <= days; d++)
        {
            var date = new DateOnly(_calMonth.Year, _calMonth.Month, d);
            int n = _counts.GetValueOrDefault(date);
            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            stack.Children.Add(new TextBlock { Text = d.ToString(), HorizontalAlignment = HorizontalAlignment.Center, FontSize = 12 });
            var dot = new Ellipse { Width = 4, Height = 4, Margin = new Thickness(0, 2, 0, 0), HorizontalAlignment = HorizontalAlignment.Center };
            if (n > 0) dot.SetResourceReference(Shape.FillProperty, "Accent");
            stack.Children.Add(dot);
            var btn = new Button
            {
                Style = (Style)Resources["CalDay"],
                Content = stack,
                IsEnabled = n > 0 || date == today,
                ToolTip = n > 0 ? $"{date.Month}月{date.Day}日 · {n} 条" : null,
            };
            ToolTipService.SetShowOnDisabled(btn, false);
            if (date == today) btn.SetResourceReference(BorderBrushProperty, "Accent");
            if (date == selected) btn.SetResourceReference(BackgroundProperty, "SelBg");
            btn.Click += (_, _) =>
            {
                CalOpen = false;
                var card = _cards.FirstOrDefault(x => !x.IsPinned && x.Date == date);
                if (card != null) { DayList.SelectedItem = card; DayList.ScrollIntoView(card); }
                SearchBox.Focus();
            };
            CalGrid.Children.Add(btn);
        }
    }
}
