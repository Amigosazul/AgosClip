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
    bool _searchOpen;
    int _searchAnimationVersion;

    static Store Store => App.Current.Store;

    public PanelWindow()
    {
        InitializeComponent();
        // native resizing updates Window.Width/Height; keep the content size separately (issue #11: remembered)
        var st = App.Current.Settings;
        _wDip = st.PanelWidth > 0 ? Math.Max(MinWDip, st.PanelWidth) : DefaultW;
        _hDip = st.PanelHeight > 0 ? Math.Max(MinHDip, st.PanelHeight) : DefaultH;
        Root.Height = _hDip;
        DayList.ItemsSource = _cards;
        ItemList.ItemsSource = _items;
        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        // tool window: no Alt+Tab entry
        Native.SetWindowLongPtr(_hwnd, -20, (IntPtr)((long)Native.GetWindowLongPtr(_hwnd, -20) | 0x80));
        // No system menu → DWM draws no caption buttons (the stray red ✕) over the glass frame.
        Native.SetWindowLongPtr(_hwnd, -16, (IntPtr)((long)Native.GetWindowLongPtr(_hwnd, -16) & ~0x00080000L & ~0x00020000L & ~0x00010000L));
        // Our reveal already animates the real glass window; avoid a second DWM show/hide transition.
        int disableTransitions = 1;
        Native.DwmSetWindowAttribute(_hwnd, 3, ref disableTransitions, 4); // DWMWA_TRANSITIONS_FORCEDISABLED
        Deactivated += (_, _) =>
        {
            if (_pasting || Snapshot || _sticky || EditOpen) return;
            // focus moving to our own preview window is not "clicking elsewhere"
            Dispatcher.BeginInvoke(() => { if (!ItemPreviewWindow.IsOpen || !ItemPreviewWindow.Current.IsActive) { if (!IsActive) HidePanel(); } },
                System.Windows.Threading.DispatcherPriority.Input);
        };
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
        SmoothWheel.Attach(ItemList);
        ItemList.PreviewMouseLeftButtonDown += ItemList_PreviewMouseDown;
        ItemList.PreviewMouseMove += ItemList_PreviewMouseMove;
        ItemList.PreviewMouseLeftButtonUp += ItemList_PreviewMouseUp;
        SmoothWheel.HookHorizontal(this,
            p => DayList.IsVisible && new Rect(DayList.TranslatePoint(new Point(), this), DayList.RenderSize).Contains(p),
            delta => { if (CardScroller is { } sv) sv.ScrollToHorizontalOffset(sv.HorizontalOffset + delta * 0.6); });
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _searchDebounce.Tick += (_, _) => { _searchDebounce.Stop(); if (!Snapshot) LoadItems(0); };
        Store.Changed += () =>
        {
            _dataDirty = true;
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
        FocusPanelInput();
    }

    // ───────────── moving ─────────────

    void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        // only the header strip (title, grip, empty space) drags; buttons/fields/items handle their own clicks
        if (e.Handled || _closing || e.GetPosition(Header).Y > Header.ActualHeight + 4) return;
        FinishAnimation(1); // drag the full window
        try { DragMove(); } catch (InvalidOperationException) { return; }
        if (Native.GetWindowRect(_hwnd, out var r))
            (_fx, _fy, _fw, _fh) = (r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
        e.Handled = true;
    }

    int _showRequest;
    internal bool IsOpening { get; private set; }

    public async void ShowFor(IntPtr target)
    {
        Watchdog.Step("open panel");
        if (target == _hwnd || !Native.IsWindow(target)) target = IntPtr.Zero;
        if (IsVisible && _sticky) { if (target != IntPtr.Zero) _target = target; FocusSticky(); return; }
        if (IsVisible)
        {
            if (target != IntPtr.Zero) _target = target;
            if (_closing) PlayOpenAnimation(); // reverse smoothly without moving or rebuilding the live panel
            FocusSticky();
            return;
        }
        if (IsOpening) return;
        IsOpening = true;
        int request = ++_showRequest;
        var t0 = System.Diagnostics.Stopwatch.StartNew();
        // The focused-field lookup (UI Automation in another process) runs in the background while the
        // list is prepared here, instead of the panel waiting for it before doing anything.
        var anchorTask = PanelPlacement.CaptureAsync(target);
        Glass.Apply(this);
        PausedBadge.Visibility = App.Paused ? Visibility.Visible : Visibility.Collapsed;
        bool wasSearching = SearchBox.Text.Length > 0;
        SetSearchOpen(false, animate: false, focus: false);
        var today = DateOnly.FromDateTime(DateTime.Now);
        bool reusable = !_dataDirty && !wasSearching && _builtFor == today && _items.Count > 0
                        && CurrentCard is { IsPinned: false } cc && cc.Date == today;
        if (reusable)
        {
            // nothing changed since the panel last closed: keep the built list, just reset to the top
            ItemList.SelectedIndex = 0;
            ItemList.ScrollIntoView(ItemList.SelectedItem);
            ApplyDateLayout();
        }
        else
        {
            RebuildCards(null);
            LoadItems(0);
            _dataDirty = false;
            _builtFor = today;
        }
        LastOpenReusedList = reusable;
        double tData = t0.Elapsed.TotalMilliseconds;
        var anchor = await anchorTask;
        double tAnchor = t0.Elapsed.TotalMilliseconds;
        if (request != _showRequest) return;
        IsOpening = false;
        if (target != IntPtr.Zero && Native.GetForegroundWindow() != target) return;
        _target = target;

        CalOverlay.Visibility = Visibility.Collapsed;
        PlaceNear(anchor);
        FinishAnimation(AnimatePanel ? 0 : 1); // set the collapsed rect before the HWND becomes visible
        Show();
        PlayOpenAnimation();
        Activate();
        Native.ForceForeground(_hwnd);
        FocusPanelInput();
        ScrollCardsToStart();
        LastOpenTiming = (tAnchor, tData, t0.Elapsed.TotalMilliseconds);
        App.Log($"open timing: data ready {tData:0}ms{(LastOpenReusedList ? " (list reused)" : "")}, anchor ready {tAnchor:0}ms, shown at {t0.Elapsed.TotalMilliseconds:0}ms");
    }

    bool _dataDirty = true;   // the store changed since the list was last built
    DateOnly _builtFor;       // the day "today" was when the list was built (midnight rolls it over)
    internal bool LastOpenReusedList;

    /// <summary>(anchor ready, data ready, shown) in ms since Win+V for the last open — for the log and benchmark.</summary>
    internal (double Anchor, double Data, double Total) LastOpenTiming;

    // ───────────── open / close animation ─────────────

    /// <summary>
    /// Unfolds the window from its anchor edge: placement above the anchor grows bottom→top,
    /// and placement below or beside it grows top→bottom. The real window
    /// rect is animated (not a render transform), so the glass backdrop and its shadow follow every frame.
    /// </summary>
    bool AnimatePanel => !Snapshot && SystemParameters.ClientAreaAnimation;

    void PlayOpenAnimation()
    {
        _closing = false;
        AnimateTo(1);
    }

    void AnimateTo(double to)
    {
        if (!AnimatePanel) { FinishAnimation(to); return; }
        double from = Math.Clamp(AnimProgress, 0, 1);
        // Short reversals cover only the remaining distance instead of restarting a full-length animation.
        var animation = new DoubleAnimation(from, to,
            TimeSpan.FromMilliseconds((to == 1 ? 170 : 120) * Math.Abs(to - from)))
        {
            EasingFunction = new CubicEase { EasingMode = to == 1 ? EasingMode.EaseOut : EasingMode.EaseIn }
        };
        int version = ++_animationVersion;
        animation.Completed += (_, _) =>
        {
            if (version == _animationVersion) FinishAnimation(to);
        };
        BeginAnimation(AnimProgressProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    void FinishAnimation(double progress)
    {
        ++_animationVersion; // a replaced close must never hide a newly reopened panel
        if (progress == 0 && _closing)
        {
            Hide(); // hide BEFORE resetting search or animation state
            SetSearchOpen(false, animate: false, focus: false);
        }
        SetValue(AnimProgressProperty, progress);
        BeginAnimation(AnimProgressProperty, null);
        ApplyAnimRect(); // also initialize a new placement when the progress value has not changed
        _closing = false;
    }

    /// <summary>Anchor edge of the unfold: true = panel sits below the caret, grows downward; false = sits above, grows upward.</summary>
    bool _expandDown = true;
    int _fx, _fy, _fw, _fh; // final physical window rect from the last placement (= progress 1.0)
    double _wDip, _hDip;    // design size in DIP, captured once
    bool _closing;          // a close animation is in flight; Hide() happens when it completes
    int _animationVersion;
    internal bool IsClosing => _closing;

    static readonly DependencyProperty AnimProgressProperty = DependencyProperty.Register(
        nameof(AnimProgress), typeof(double), typeof(PanelWindow),
        new PropertyMetadata(1.0, (d, _) => ((PanelWindow)d).ApplyAnimRect()));
    double AnimProgress => (double)GetValue(AnimProgressProperty);

    /// <summary>
    /// Animation frame: the window keeps its full size and only slides a few pixels out of the anchor edge. Resizing the HWND every frame (the previous approach) made WPF re-layout
    /// and rebuild its render surface per frame, which dropped frames — moving a window is nearly free, and
    /// the glass backdrop still travels with it.
    /// </summary>
    void ApplyAnimRect()
    {
        if (_fh <= 0 || _hwnd == IntPtr.Zero) return;
        double p = Math.Clamp(AnimProgress, 0.0, 1.0);
        int slide = (int)Math.Round(_slidePx * (1 - p));
        int y = _expandDown ? _fy - slide : _fy + slide; // emerge away from the caret/field
        bool resize = _appliedW != _fw || _appliedH != _fh;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, _fx, y, _fw, _fh, 0x0004 | 0x0010 | (resize ? 0u : 0x0001u)); // NOZORDER|NOACTIVATE[|NOSIZE]
        (_appliedW, _appliedH) = (_fw, _fh);
        // No content fade: Opacity on the whole panel forces WPF to re-render all of it every frame.
        // A pure move costs WPF nothing (DWM shifts the already-composed surface).
    }

    int _appliedW, _appliedH;
    int _slidePx = 10;
    internal int SlidePx => _slidePx;

    // ───────────── search ─────────────

    void FocusPanelInput()
    {
        if (_searchOpen)
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }
        else
        {
            // The window keeps arrow/Enter shortcuts without focusing a hidden text field.
            Focus();
            Keyboard.Focus(this);
        }
    }

    void Search_Click(object sender, RoutedEventArgs e) => SetSearchOpen(!_searchOpen);

    void SetSearchOpen(bool open, bool animate = true, bool focus = true)
    {
        _searchOpen = open;
        int animationVersion = ++_searchAnimationVersion;
        SearchBox.IsEnabled = open;
        SearchReveal.IsHitTestVisible = open;
        SearchButton.ToolTip = open ? "收起搜索 (Esc)" : "搜索全部历史 (Ctrl+F)";
        System.Windows.Automation.AutomationProperties.SetName(SearchButton, open ? "收起搜索" : "搜索全部历史");
        if (open)
        {
            CalOpen = false;
            SearchReveal.Visibility = Visibility.Visible;
            SearchButton.SetResourceReference(ForegroundProperty, "Accent");
            SearchButton.SetResourceReference(BackgroundProperty, "SelBg");
        }
        else
        {
            SearchButton.ClearValue(ForegroundProperty);
            SearchButton.ClearValue(BackgroundProperty);
            bool pendingSearch = _searchDebounce.IsEnabled;
            _searchDebounce.Stop();
            bool hadQuery = SearchBox.Text.Length > 0;
            SearchBox.TextChanged -= Search_TextChanged;
            SearchBox.Clear();
            SearchBox.TextChanged += Search_TextChanged;
            // The textbox may already be empty while a debounced clear has not refreshed the list.
            if (hadQuery || pendingSearch) LoadItems(0);
        }

        double height = open ? 41 : 0; // field + space for its focus ring
        double opacity = open ? 1 : 0;
        if (animate && !Snapshot && SystemParameters.ClientAreaAnimation && IsVisible)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            var duration = TimeSpan.FromMilliseconds(open ? 190 : 150);
            // Explicit starts make rapid toggles reverse from the current visual state.
            var reveal = new DoubleAnimation(SearchReveal.ActualHeight, height, duration)
                { EasingFunction = ease };
            if (!open) reveal.Completed += (_, _) =>
            {
                // Replaced WPF clocks can still complete; only the latest close may hide the row.
                if (animationVersion == _searchAnimationVersion && !_searchOpen)
                    SearchReveal.Visibility = Visibility.Collapsed;
            };
            SearchReveal.BeginAnimation(HeightProperty, reveal);
            SearchReveal.BeginAnimation(OpacityProperty,
                new DoubleAnimation(SearchReveal.Opacity, opacity, duration) { EasingFunction = ease });
        }
        else
        {
            SearchReveal.BeginAnimation(HeightProperty, null);
            SearchReveal.BeginAnimation(OpacityProperty, null);
            SearchReveal.Height = height;
            SearchReveal.Opacity = opacity;
            SearchReveal.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        }

        if (focus)
        {
            // Layout must run before focusing the newly visible field; keep text/IME input available
            // throughout the reveal, rather than waiting for the animation to finish.
            SearchReveal.UpdateLayout();
            FocusPanelInput();
        }
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
        SetSearchOpen(search != null, animate: false, focus: false);
        if (search != null) SearchBox.Text = search;
        LoadItems(0);
        if (openCalendar)
        {
            _calMonth = new DateOnly(DateTime.Now.Year, DateTime.Now.Month, 1);
            _calCursor = DateOnly.FromDateTime(DateTime.Now);
            BuildCalendar();
            CalOpen = true;
        }
        else CalOpen = false;
    }


    /// <summary>
    /// Closes the panel (✕, Esc, Win+V). Collapses back toward the edge it unfolded from — the exact
    /// reverse of the open animation — and hides when it finishes. Closing explicitly also un-pins it.
    /// </summary>
    public void HidePanel()
    {
        ++_showRequest;
        IsOpening = false;
        if (_sticky) SetSticky(false);
        CalOpen = false;
        if (EditOpen) { _editing = null; EditOverlay.Visibility = Visibility.Collapsed; }
        EndDrag(commit: false);
        if (!IsVisible) { SetSearchOpen(false, animate: false, focus: false); return; }
        if (_closing) return;
        if (!AnimatePanel)
        {
            Hide();
            SetSearchOpen(false, animate: false, focus: false);
            FinishAnimation(1);
            return;
        }
        _closing = true;
        AnimateTo(0);
    }

    void PlaceNear(PanelAnchor anchor)
    {
        var (x, y, w, h, down) = PanelPlacement.Calculate(anchor, _wDip, _hDip, out _scale, out _);
        _expandDown = down;
        SetPlacement(x, y, w, h);
    }

    double _scale = 1; // physical px per DIP on the panel's monitor

    void SetPlacement(int x, int y, int w, int h)
    {
        (_fx, _fy, _fw, _fh) = (x, y, w, h);
        _slidePx = Math.Max(4, (int)Math.Round(10.0 * _scale)); // 10 DIP at the monitor's scale
        Root.Height = h / _scale; // equals _hDip unless the work area is smaller than the remembered size
        Root.VerticalAlignment = _expandDown ? VerticalAlignment.Top : VerticalAlignment.Bottom;
        ApplyAnimRect();
        Native.SetWindowPos(_hwnd, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010); // NOSIZE | NOMOVE | NOACTIVATE, topmost
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
    /// <summary>Issue #5: without the date-card strip, the list title carries ‹ › and the calendar.</summary>
    bool Compact => !App.Current.Settings.ShowDateCards;

    void ApplyDateLayout()
    {
        bool compact = Compact, searching = SearchBox.Text.Trim().Length > 0;
        DateStrip.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        var v = compact && !searching ? Visibility.Visible : Visibility.Collapsed;
        DayOlder.Visibility = DayNewer.Visibility = CompactCalButton.Visibility = v;
        SectionRow.Margin = compact ? new Thickness(0, 8, -4, 6) : new Thickness(0, 12, -4, 7);
        DayOlder.IsEnabled = CompactTarget(older: true) >= 0;
        DayNewer.IsEnabled = CompactTarget(older: false) >= 0;
    }

    /// <summary>
    /// Issue #12, compact mode: ‹ / ← go to the next older day with records, › / → to the next newer one.
    /// The 固定 list is not a day, so ‹ › skip it (it lives in the calendar); from 固定, ‹ returns to today.
    /// </summary>
    int CompactTarget(bool older)
    {
        int i = DayList.SelectedIndex;
        if (i < 0) return -1;
        int firstDay = _cards.Count > 0 && _cards[0].IsPinned ? 1 : 0;
        if (_cards[i].IsPinned) return older && firstDay < _cards.Count ? firstDay : -1;
        int j = older ? i + 1 : i - 1;
        return j >= firstDay && j < _cards.Count ? j : -1;
    }

    void DayOlder_Click(object sender, RoutedEventArgs e) => MoveCompact(older: true);
    void DayNewer_Click(object sender, RoutedEventArgs e) => MoveCompact(older: false);

    void MoveCompact(bool older)
    {
        int j = CompactTarget(older);
        if (j >= 0) MoveDay(j - DayList.SelectedIndex);
    }

    void CalPinned_Click(object sender, RoutedEventArgs e)
    {
        CalOpen = false;
        if (_cards.FirstOrDefault(c => c.IsPinned) is { } pinned) DayList.SelectedItem = pinned;
        FocusPanelInput();
    }

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
                SectionTitle.Text = Compact ? $"固定 · {rows.Count} 条 · 不会被自动清理" : $"固定的条目 · {rows.Count} 条 · 不会被自动清理";
                EmptyText.Text = "还没有固定的条目";
            }
            else
            {
                rows = Store.ItemsOnDay(card.Date);
                var d = card.Date;
                if (Compact)
                {
                    // issue #5: "今天 · 11/19 周四 · 20 条"
                    var today = DateOnly.FromDateTime(DateTime.Now);
                    int ago = today.DayNumber - d.DayNumber;
                    string rel = ago switch { 0 => "今天 · ", 1 => "昨天 · ", 2 => "前天 · ", _ => "" };
                    string date = d.Year == today.Year ? $"{d.Month}/{d.Day}" : $"{d.Year}/{d.Month}/{d.Day}";
                    SectionTitle.Text = $"{rel}{date} {DayCard.WeekName(d)} · {rows.Count} 条";
                }
                else SectionTitle.Text = $"{d.Year}年{d.Month}月{d.Day}日 {DayCard.WeekName(d)} · {rows.Count} 条";
                EmptyText.Text = card.Date == DateOnly.FromDateTime(DateTime.Now)
                    ? "今天还没有复制过内容\n复制文字或图片后会出现在这里"
                    : "这一天没有记录";
            }
        }

        _items.Clear();
        foreach (var r in rows) _items.Add(new ItemVm(r, showDate));
        ApplyDateLayout();
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
            var formats = vm.Item.Formats.Count > 0 ? Store.GetFormats(vm.Item.Id) : null;
            if (!Paster.SetClipboard(vm.Item, formats, out var problem))
            {
                EmptyText.Text = problem ?? "无法写入剪贴板（图片文件可能已被删除）";
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

    void OnKey(object sender, KeyEventArgs e) => e.Handled = HandleKey(e.Key, Keyboard.Modifiers);

    internal bool HandleKey(Key key, ModifierKeys modifiers)
    {
        bool ctrl = (modifiers & ModifierKeys.Control) != 0;
        if (_dragActive)
        {
            if (key == Key.Escape) EndDrag(commit: false);
            return true;
        }
        if (EditOpen)
        {
            // the editor's text box gets every other key (arrows, Enter for new lines, Delete…)
            if (key == Key.Escape) { CloseEditor(); return true; }
            if (key == Key.Enter && ctrl) { SaveEditor(); return true; }
            return false;
        }
        if (PreviewOpen) return HandlePreviewKey(key, ctrl);
        if (CalOpen) return HandleCalendarKey(key, ctrl);
        if ((key == Key.F2 || (key == Key.E && ctrl)) && Selected is { IsText: true } toEdit) { OpenEditor(toEdit); return true; }
        if (key == Key.Space && !_searchOpen && Selected is { } toPreview) { OpenPreview(toPreview); return true; }
        if (key == Key.T && ctrl && Selected is { CanTranslate: true } toTranslate) { Translate(toTranslate.Item); return true; }
        bool searching = SearchBox.Text.Length > 0;
        switch (key)
        {
            case Key.Escape:
                if (searching)
                {
                    SearchBox.Clear();
                    _searchDebounce.Stop();
                    LoadItems(0);
                }
                else if (_searchOpen) SetSearchOpen(false);
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
            // Issue #12: arrows follow the screen. Compact: ← = ‹ (older), → = › (newer).
            // Date strip: cards run newest → oldest from left to right, so ← = the card on the left.
            case Key.Left when !searching:
                if (Compact) MoveCompact(older: true); else MoveDay(-1);
                break;
            case Key.Right when !searching:
                if (Compact) MoveCompact(older: false); else MoveDay(+1);
                break;
            case Key.Delete when !searching || ctrl:
                if (Selected is { } d) DeleteItem(d);
                break;
            case Key.F when ctrl:
                SetSearchOpen(true);
                SearchBox.SelectAll();
                break;
            case Key.P when ctrl:
                if (Selected is { } p) TogglePin(p);
                break;
            case Key.Tab:
                break;
            default:
                return false;
        }
        return true;
    }

    // ───────────── resize (issue #11) ─────────────
    // Edges and corners are thin transparent thumbs. The size is remembered (DIP, so it survives a DPI
    // change); double-click an edge to go back to the default size.

    internal const double DefaultW = 400, DefaultH = 580, MinWDip = 340, MinHDip = 420;
    Native.POINT _rsCursor;
    (int L, int T, int R, int B) _rsStart;
    Native.RECT _rsWork;

    void Resize_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        FinishAnimation(1);
        Native.GetCursorPos(out _rsCursor);
        _rsStart = (_fx, _fy, _fx + _fw, _fy + _fh);
        _scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var r = new Native.RECT { Left = _fx, Top = _fy, Right = _fx + _fw, Bottom = _fy + _fh };
        var mon = Native.MonitorFromRect(ref r, 2);
        var mi = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        _rsWork = Native.GetMonitorInfo(mon, ref mi) ? mi.rcWork : r;
    }

    void Resize_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string edges }) return;
        Native.GetCursorPos(out var c);
        ResizeBy(edges, c.X - _rsCursor.X, c.Y - _rsCursor.Y);
    }

    /// <summary>Moves the given edges ("L", "T", "R", "B" or a corner like "BR") by a physical-pixel offset from the drag start.</summary>
    internal void ResizeBy(string edges, int dx, int dy)
    {
        var (l, t, r, b) = _rsStart;
        int minW = (int)Math.Round(MinWDip * _scale), minH = (int)Math.Round(MinHDip * _scale);
        if (edges.Contains('L')) l = Math.Clamp(l + dx, _rsWork.Left, r - minW);
        if (edges.Contains('R')) r = Math.Clamp(r + dx, l + minW, Math.Max(l + minW, _rsWork.Right));
        if (edges.Contains('T')) t = Math.Clamp(t + dy, _rsWork.Top, b - minH);
        if (edges.Contains('B')) b = Math.Clamp(b + dy, t + minH, Math.Max(t + minH, _rsWork.Bottom));
        ApplySize(l, t, r - l, b - t);
    }

    void ApplySize(int x, int y, int w, int h)
    {
        _wDip = w / _scale;
        _hDip = h / _scale;
        Root.Height = _hDip;
        (_fx, _fy, _fw, _fh) = (x, y, w, h);
        ApplyAnimRect();
    }

    void Resize_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e) => SaveSize();

    void SaveSize()
    {
        var st = App.Current.Settings;
        st.PanelWidth = Math.Abs(_wDip - DefaultW) < 1 ? 0 : Math.Round(_wDip);
        st.PanelHeight = Math.Abs(_hDip - DefaultH) < 1 ? 0 : Math.Round(_hDip);
        try { st.Save(); } catch (Exception ex) { App.Log("save panel size: " + ex.Message); }
    }

    /// <summary>Double-click an edge: back to 400 × 580, keeping the anchor edge where it is.</summary>
    void Resize_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        Resize_DragStarted(sender, null!);
        int w = (int)Math.Round(DefaultW * _scale), h = (int)Math.Round(DefaultH * _scale);
        int x = Math.Clamp(_fx, _rsWork.Left, Math.Max(_rsWork.Left, _rsWork.Right - w));
        int y = _expandDown ? _fy : _fy + _fh - h;
        y = Math.Clamp(y, _rsWork.Top, Math.Max(_rsWork.Top, _rsWork.Bottom - h));
        ApplySize(x, y, w, h);
        SaveSize();
        e.Handled = true;
    }

    /// <summary>Self-test: start a resize from the current rect as if a thumb was pressed.</summary>
    internal void BeginResizeForTest() => Resize_DragStarted(this, null!);
    internal void EndResizeForTest() => SaveSize();
    internal (int W, int H) PhysicalSize => (_fw, _fh);
    internal (double W, double H) SizeDip => (_wDip, _hDip);

    // ───────────── drag to reorder (issue #13) ─────────────
    // Press anywhere on a card except its buttons and drag: the card moves through the list live and the
    // order is saved on release. A press without movement is still a click (paste). Search results
    // can't be arranged (they mix days). Esc while dragging puts the card back.

    ItemVm? _pressVm;
    Point _pressPt;
    bool _dragActive;
    int _dragFrom;
    DispatcherTimer? _dragTimer;
    internal bool DragActive => _dragActive;

    bool CanArrange => Query.Length == 0 && _items.Count > 1 && CurrentCard != null;

    void ItemList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _pressVm = null;
        if (!CanArrange || e.OriginalSource is not DependencyObject src) return;
        for (var d = src; d != null && d != ItemList; d = VisualTreeHelper.GetParent(d))
        {
            if (d is System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.ScrollBar) return;
            if (d is ListBoxItem { DataContext: ItemVm vm }) { _pressVm = vm; _pressPt = e.GetPosition(ItemList); return; }
        }
    }

    void ItemList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressVm == null) return;
        if (e.LeftButton != MouseButtonState.Pressed) { if (!_dragActive) _pressVm = null; return; }
        var p = e.GetPosition(ItemList);
        if (!_dragActive)
        {
            if (Math.Abs(p.Y - _pressPt.Y) < 6 && Math.Abs(p.X - _pressPt.X) < 6) return;
            BeginDrag(_pressVm);
        }
        DragTo(p.Y);
    }

    void ItemList_PreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragActive) { EndDrag(commit: true); e.Handled = true; } // a drag is not a click: don't paste
        _pressVm = null;
    }

    void BeginDrag(ItemVm vm)
    {
        _dragActive = true;
        _dragFrom = _items.IndexOf(vm);
        vm.Dragging = true;
        ItemList.SelectedItem = vm;
        Mouse.OverrideCursor = Cursors.SizeNS;
        ItemList.CaptureMouse();
        ItemList.LostMouseCapture += ItemList_LostCapture;
        _dragTimer ??= new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Input, (_, _) => AutoScroll(), Dispatcher);
        _dragTimer.Start();
    }

    void ItemList_LostCapture(object sender, MouseEventArgs e) { if (_dragActive) EndDrag(commit: true); }

    /// <summary>Near the top or bottom edge the list scrolls by itself, faster the closer the pointer gets.</summary>
    void AutoScroll()
    {
        if (!_dragActive || FindChild<ScrollViewer>(ItemList) is not { } sv) return;
        double y = Mouse.GetPosition(ItemList).Y, h = ItemList.ActualHeight, zone = 36;
        double speed = y < zone ? -(zone - y) : y > h - zone ? y - (h - zone) : 0;
        if (speed == 0) return;
        sv.ScrollToVerticalOffset(sv.VerticalOffset + Math.Clamp(speed, -zone, zone) * 0.5);
        DragTo(Math.Clamp(y, 0, h));
    }

    /// <summary>
    /// Moves the dragged card past a neighbour once the pointer crosses that neighbour's middle. Using the
    /// middle (not the edge) keeps cards of different heights from swapping back and forth.
    /// </summary>
    void DragTo(double y)
    {
        var vm = _items.FirstOrDefault(v => v.Dragging);
        if (vm == null) return;
        int cur = _items.IndexOf(vm), target = cur;
        for (int i = 0; i < _items.Count; i++)
        {
            if (i == cur || ItemList.ItemContainerGenerator.ContainerFromIndex(i) is not ListBoxItem c || !c.IsVisible) continue;
            double top = c.TranslatePoint(new Point(), ItemList).Y, mid = top + c.ActualHeight / 2;
            if (i < cur && y < mid) { target = Math.Min(target, i); }
            else if (i > cur && y > mid) { target = Math.Max(target, i); }
        }
        if (target != cur)
        {
            _items.Move(cur, target);
            ItemList.SelectedItem = vm;
        }
    }

    internal void EndDrag(bool commit)
    {
        _pressVm = null;
        if (!_dragActive) return;
        _dragActive = false;
        _dragTimer?.Stop();
        ItemList.LostMouseCapture -= ItemList_LostCapture;
        if (ItemList.IsMouseCaptured) ItemList.ReleaseMouseCapture();
        Mouse.OverrideCursor = null;
        var vm = _items.FirstOrDefault(v => v.Dragging);
        if (vm == null) return;
        vm.Dragging = false;
        int now = _items.IndexOf(vm);
        if (!commit) { if (now != _dragFrom && _dragFrom >= 0 && _dragFrom < _items.Count) _items.Move(now, _dragFrom); ItemList.SelectedItem = vm; return; }
        if (now == _dragFrom) return;
        SaveOrder(vm, now);
    }

    void SaveOrder(ItemVm vm, int index)
    {
        long? above = index > 0 ? _items[index - 1].Item.Id : null;
        long? below = index < _items.Count - 1 ? _items[index + 1].Item.Id : null;
        _muteStoreChange = true;
        try { Store.Reorder(vm.Item.Id, above, below, CurrentCard is { IsPinned: true }); }
        finally { _muteStoreChange = false; }
        ItemList.SelectedItem = vm;
    }

    /// <summary>Self-test: the same path as a mouse drag from one position to another.</summary>
    internal void MoveItemForTest(int from, int to)
    {
        var vm = _items[from];
        _dragActive = true; _dragFrom = from; vm.Dragging = true;
        _items.Move(from, to);
        EndDrag(commit: true);
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
        FocusPanelInput();
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

    // ───────────── edit a text item (issue #6) ─────────────

    ItemVm? _editing;
    internal bool EditOpen => EditOverlay.Visibility == Visibility.Visible;

    void Translate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ItemVm { CanTranslate: true } vm }) { ItemList.SelectedItem = vm; Translate(vm.Item); }
        e.Handled = true;
    }

    /// <summary>
    /// Opens the translation window for a text item. Rich text (HTML/RTF) is translated with its formatting,
    /// so 复制译文 puts a formatted translation on the clipboard.
    /// </summary>
    internal static TextToolWindow? Translate(ClipItem item)
    {
        if (item.Kind != ClipKind.Text || string.IsNullOrWhiteSpace(item.Text) || !App.Current.Settings.EnableTranslate) return null;
        var rich = RichTranslator.Supports(item.Formats) ? Store.GetFormats(item.Id) : null;
        return TextToolWindow.ShowTranslate(item.Text, null, rich);
    }

    void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ItemVm { IsText: true } vm }) OpenEditor(vm);
        e.Handled = true;
    }

    internal void OpenEditor(ItemVm vm)
    {
        _editing = vm;
        CalOpen = false;
        EditBox.Text = vm.Item.Text ?? "";
        EditHint.Text = vm.Item.Formats.Count > 0 ? "保存后会变成纯文本，原来的格式不保留 · Ctrl+Enter 保存"
                                                  : "Ctrl+Enter 保存 · Esc 取消";
        EditOverlay.Visibility = Visibility.Visible;
        EditBox.Focus();
        Keyboard.Focus(EditBox);
        EditBox.CaretIndex = EditBox.Text.Length;
    }

    void CloseEditor()
    {
        _editing = null;
        EditOverlay.Visibility = Visibility.Collapsed;
        FocusPanelInput();
    }

    internal bool SaveEditor()
    {
        if (_editing == null) return false;
        var text = EditBox.Text;
        if (string.IsNullOrWhiteSpace(text)) { EditHint.Text = "内容不能为空"; return false; }
        var id = _editing.Item.Id;
        bool ok;
        _muteStoreChange = true;
        try { ok = text == _editing.Item.Text || Store.UpdateText(id, text); }
        finally { _muteStoreChange = false; }
        CloseEditor();
        Reload(keepIndex: true);
        if (!ok) { EditText("这条记录已经不存在了"); }
        return ok;
    }

    void EditText(string msg) { EmptyText.Text = msg; EmptyState.Visibility = Visibility.Visible; }

    // ───────────── preview (issue #10): a separate window, see ItemPreviewWindow ─────────────

    static ItemPreviewWindow Pv => ItemPreviewWindow.Current;
    internal bool PreviewOpen => ItemPreviewWindow.IsOpen;
    internal string PreviewMode => ItemPreviewWindow.IsOpen ? Pv.PreviewMode : "";
    internal double CurrentZoom => Pv.CurrentZoom;
    internal TextBox PreviewText => Pv.PreviewText;
    internal Image PreviewImage => Pv.PreviewImage;
    internal TextBlock PreviewMessage => Pv.PreviewMessage;
    internal static string ReadTextFile(string path) => ItemPreviewWindow.ReadTextFile(path);

    void Preview_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ItemVm vm }) { ItemList.SelectedItem = vm; OpenPreview(vm); }
        e.Handled = true;
    }

    internal void OpenPreview(ItemVm vm)
    {
        CalOpen = false;
        var w = Pv;
        if (!w.IsVisible) w.PlaceBeside(IsVisible ? this : null);
        if (!_previewHooked)
        {
            _previewHooked = true;
            w.Closed_ += () =>
            {
                // back where the user was, if the panel is still open
                if (!IsVisible) return;
                if (Selected is { } s) ItemList.ScrollIntoView(s);
                Activate();
                FocusPanelInput();
            };
        }
        w.OpenPreview(vm);
    }

    bool _previewHooked;

    internal void ClosePreview() => Pv.ClosePreview();

    bool HandlePreviewKey(Key key, bool ctrl) => Pv.HandlePreviewKey(key, ctrl);

    void EditCancel_Click(object sender, RoutedEventArgs e) => CloseEditor();
    void EditSave_Click(object sender, RoutedEventArgs e) => SaveEditor();

    void Cal_Click(object sender, RoutedEventArgs e)
    {
        var basis = CurrentCard is { IsPinned: false } c ? c.Date : DateOnly.FromDateTime(DateTime.Now);
        _calMonth = new DateOnly(basis.Year, basis.Month, 1);
        _calCursor = basis;
        if (CalOpen) { CalOpen = false; FocusPanelInput(); return; }
        // Keep the calendar anchored below the date strip in both search states.
        var anchorBtn = sender as FrameworkElement is { IsVisible: true } fe ? fe : (Compact ? CompactCalButton : CalButton);
        double top = anchorBtn.TranslatePoint(new Point(0, anchorBtn.ActualHeight), CalOverlay).Y + 6;
        CalCard.Margin = CalShadow.Margin = new Thickness(0, top, 14, 0);
        BuildCalendar();
        CalOpen = true;
    }

    void CalBackdrop_Click(object sender, MouseButtonEventArgs e) { CalOpen = false; e.Handled = true; }

    /// <summary>Keyboard cursor inside the open calendar (issue #12).</summary>
    DateOnly _calCursor;
    internal DateOnly CalCursor => _calCursor;
    internal DateOnly CalMonth => _calMonth;
    internal bool CalOpenForTest => CalOpen;
    internal DayCard? CurrentCardForTest => CurrentCard;

    /// <summary>
    /// Issue #12: while the calendar is open, keys drive the calendar and never the list underneath.
    /// ←→ ±1 day, ↑↓ ±1 week, PgUp/PgDn ±1 month, Home = today, Enter opens the day (if it has records), Esc closes.
    /// </summary>
    bool HandleCalendarKey(Key key, bool ctrl)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        int step = key switch
        {
            Key.Left => -1, Key.Right => 1, Key.Up => -7, Key.Down => 7, _ => 0,
        };
        if (step != 0) MoveCalCursor(_calCursor.AddDays(step));
        else switch (key)
        {
            case Key.PageUp: MoveCalCursor(_calCursor.AddMonths(-1)); break;
            case Key.PageDown: MoveCalCursor(_calCursor.AddMonths(1)); break;
            case Key.Home: MoveCalCursor(today); break;
            case Key.Escape: CalOpen = false; FocusPanelInput(); break;
            case Key.Enter or Key.Space:
                if (_counts.GetValueOrDefault(_calCursor) > 0 || _calCursor == today) PickCalendarDay(_calCursor);
                break;
            case Key.F when ctrl: CalOpen = false; SetSearchOpen(true); break;
            case Key.Tab: break;
            default:
                // plain typing and other shortcuts must not reach the list under the calendar
                return key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.System);
        }
        return true;
    }

    void MoveCalCursor(DateOnly d)
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var earliest = _counts.Count > 0 && _counts.Keys.First() < today ? _counts.Keys.First() : today;
        if (d > today) d = today;
        if (d < earliest) d = earliest;
        _calCursor = d;
        _calMonth = new DateOnly(d.Year, d.Month, 1);
        BuildCalendar();
    }

    void PickCalendarDay(DateOnly date)
    {
        CalOpen = false;
        var card = _cards.FirstOrDefault(x => !x.IsPinned && x.Date == date);
        if (card != null) { DayList.SelectedItem = card; DayList.ScrollIntoView(card); }
        FocusPanelInput();
    }

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
            // keyboard position: a ring (today keeps its accent ring)
            if (date == today) btn.SetResourceReference(BorderBrushProperty, "Accent");
            else if (date == _calCursor) btn.SetResourceReference(BorderBrushProperty, "SubFg");
            if (date == selected) btn.SetResourceReference(BackgroundProperty, "SelBg");
            else if (date == _calCursor) btn.SetResourceReference(BackgroundProperty, "Track");
            btn.Click += (_, _) => PickCalendarDay(date);
            CalGrid.Children.Add(btn);
        }
        {
            // issue #5: the 固定 entry lives in the calendar when the date strip is hidden
            var pinned = _cards.FirstOrDefault(c => c.IsPinned);
            CalPinned.Content = pinned != null ? $"固定的条目 · {pinned.Count} 条" : "";
            CalPinned.Visibility = pinned != null && Compact ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}
