using AgentDock.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.Graphics;

namespace AgentDock.Views;

/// <summary>
/// Where the flyout opens, its open animation, and the width and height grips.
/// </summary>
public sealed partial class FlyoutWindow
{
    // ---------- Placement and motion ----------

    /// <summary>
    /// Sizes the window to its content and places it just above (or beside) the taskbar where it
    /// was clicked, clamped to that monitor's work area. If opened some other way, it centres on
    /// the screen. Works in physical pixels so mixed-DPI setups land correctly.
    /// </summary>
    private void PlaceWindow()
    {
        if (Win32.MonitorInfoAt(_anchor, out var monitor) is not { } info)
            return;

        var scale = Win32.ScaleFor(monitor);
        var work = info.rcWork;
        var gap = (int)Math.Round(12 * scale);

        // Position the list as its own window, then add the drawer on beside it. The anchor math
        // stays about the list, so opening the drawer doesn't drag the list off the taskbar button.
        var drawerDip = DrawerDip();
        // A wider list dragged on a bigger monitor is kept for the next time it opens on one.
        var widthDip = Math.Clamp(_requestedListWidthDip, DefaultListWidthDip,
            Math.Max(DefaultListWidthDip, (work.Width - 2 * gap) / scale - drawerDip));
        if (Math.Abs(widthDip - _listWidthDip) > 0.01)
            ApplyListWidth(widthDip);
        UpdateRowColumns();
        Root.Measure(new Windows.Foundation.Size(widthDip + drawerDip, double.PositiveInfinity));
        var workHeightDip = (work.Height - 2 * gap) / scale;
        var fitDip = Root.DesiredSize.Height > 1 ? Root.DesiredSize.Height : AppWindow.ClientSize.Height / scale;
        // The height the user dragged to (the list scrolls if its content is taller); without one,
        // the list fits its content.
        var heightDip = _state.FlyoutHeight is { } chosen
            ? Math.Clamp(chosen, Math.Min(MinHeightDip, workHeightDip), workHeightDip)
            : Math.Min(fitDip, Math.Min(MaxHeightDip, workHeightDip));
        // The grip goes on the edge away from the taskbar; the edge by the taskbar stays put.
        _gripAtTop = !(_anchor.Y < work.Top && !work.Contains(_anchor));
        HeightGrip.VerticalAlignment = _gripAtTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;

        // Window size includes the thin border around the client area.
        var borderW = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        var borderH = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        var listW = (int)Math.Ceiling(_listWidthDip * scale) + borderW;
        var h = (int)Math.Ceiling(heightDip * scale) + borderH;

        int x, y;
        if (work.Contains(_anchor))
        {
            x = work.Left + (work.Width - listW) / 2;
            y = work.Top + (work.Height - h) / 2;
        }
        else if (_anchor.Y >= work.Bottom) { x = _anchor.X - listW / 2; y = work.Bottom - h - gap; }
        else if (_anchor.Y < work.Top)     { x = _anchor.X - listW / 2; y = work.Top + gap; }
        else if (_anchor.X < work.Left)    { x = work.Left + gap;       y = _anchor.Y - h / 2; }
        else                               { x = work.Right - listW - gap; y = _anchor.Y - h / 2; }

        x = Math.Clamp(x, work.Left + gap, Math.Max(work.Left + gap, work.Right - listW - gap));
        y = Math.Clamp(y, work.Top + gap, Math.Max(work.Top + gap, work.Bottom - h - gap));

        _listScreenX = x;
        var drawerPx = (int)Math.Round(drawerDip * scale);
        var w = listW + drawerPx;
        if (drawerPx > 0 && _drawerSide < 0)
            x -= drawerPx;

        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    // ---------- Resizing the width ----------

    /// <summary>
    /// Lays the list out at a width: the two XAML columns, the footer, and the rows' column count.
    /// The drawer keeps its own width and moves with the list's edge, so a wider list pushes it
    /// further out rather than making it wider.
    /// </summary>
    private void ApplyListWidth(double widthDip)
    {
        _listWidthDip = widthDip;
        ListColumn.Width = widthDip;
        FooterContent.Width = widthDip;
        UpdateRowColumns();
        LayoutBackdrop();
    }

    /// <summary>
    /// How many columns the rows fill: two once each would still get a row's worth of width, one
    /// otherwise. They share the extra width instead of leaving a gap, so a very wide list gives
    /// names and subtitles more room.
    /// </summary>
    private void UpdateRowColumns()
    {
        var itemWidth = RowItemWidth();
        foreach (var list in Lists)
            if (list.ItemsPanelRoot is ItemsWrapGrid panel)
                panel.ItemWidth = itemWidth;
    }

    private double RowItemWidth()
    {
        var content = _listWidthDip - BannerWidthDip - 8; // the lists' Margin="4,0"
        var columns = content >= ColumnWidthDip * 2 ? 2 : 1;
        return Math.Floor(content / columns);
    }

    /// <summary>
    /// A list's panel only exists once the list is first laid out while visible, which can be after
    /// <see cref="UpdateRowColumns"/> last ran. Without a width it sizes every row to the first one's
    /// content, which packs narrow, cut-off rows several to a line.
    /// </summary>
    private void RowsPanel_Loaded(object sender, RoutedEventArgs e) =>
        ((ItemsWrapGrid)sender).ItemWidth = RowItemWidth();

    private bool _widthResizing;
    private int _widthResizeStartCursorX;
    private RectInt32 _widthResizeStart;

    private void WidthGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!WidthGrip.CapturePointer(e.Pointer) || !Win32.GetCursorPos(out var cursor))
            return;
        // Screen coordinates throughout: the grip moves with the window while it's dragged.
        _widthResizeStartCursorX = cursor.X;
        _widthResizeStart = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _widthResizing = true;
        // The pointer left the drawer to reach the grip: keep it open while it changes underneath.
        _drawerCloseTimer?.Stop();
        e.Handled = true;
    }

    private void WidthGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_widthResizing || !Win32.GetCursorPos(out var cursor) || Win32.MonitorInfoAt(_anchor, out var monitor) is not { } info)
            return;

        var scale = Win32.ScaleFor(monitor);
        var work = info.rcWork;
        var gap = (int)Math.Round(12 * scale);
        var borderW = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        var drawerPx = (int)Math.Round(DrawerDip() * scale);
        var minW = (int)Math.Ceiling(DefaultListWidthDip * scale) + drawerPx + borderW;
        var maxW = Math.Max(minW, work.Right - gap - _widthResizeStart.X);
        var w = Math.Clamp(_widthResizeStart.Width + (cursor.X - _widthResizeStartCursorX), minW, maxW);
        // The window's left edge stays put, so the list's right edge (and the drawer's) follows the pointer.
        var widthDip = (w - drawerPx - borderW) / scale;
        if (Math.Abs(widthDip - _requestedListWidthDip) < 0.5)
            return;
        _requestedListWidthDip = widthDip;
        ApplyListWidth(widthDip);
        ApplyWindowForDrawer();
        e.Handled = true;
    }

    private void WidthGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        WidthGrip.ReleasePointerCapture(e.Pointer);
        EndWidthResize();
    }

    private void WidthGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndWidthResize();

    /// <summary>
    /// Remembers the width, then settles: a second column may have made the list shorter, and the
    /// list's left edge returns to its place over the taskbar button's anchor.
    /// </summary>
    private void EndWidthResize()
    {
        if (!_widthResizing)
            return;
        _widthResizing = false;
        if (Win32.MonitorInfoAt(_anchor, out var monitor) is not null)
        {
            _state.FlyoutWidth = Math.Round(_listWidthDip);
            _store.SaveState(_state);
        }
        // The pointer is still over the grip, so nothing else reports leaving the drawer.
        if (_drawerOpen && !PointerOverRowOrDrawer())
            ScheduleDrawerClose();
        PlaceWindow();
    }

    private void WidthGrip_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _state.FlyoutWidth = null;
        _store.SaveState(_state);
        _requestedListWidthDip = DefaultListWidthDip;
        ApplyListWidth(DefaultListWidthDip);
        PlaceWindow();
        e.Handled = true;
    }

    // ---------- Resizing the height ----------

    private bool _gripAtTop = true;
    private bool _resizing;
    private int _resizeStartCursorY;
    private RectInt32 _resizeStart;

    private void HeightGrip_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (!HeightGrip.CapturePointer(e.Pointer) || !Win32.GetCursorPos(out var cursor))
            return;
        // Screen coordinates throughout: the grip moves with the window while it's dragged.
        _resizeStartCursorY = cursor.Y;
        _resizeStart = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
        _resizing = true;
        e.Handled = true;
    }

    private void HeightGrip_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_resizing || !Win32.GetCursorPos(out var cursor) || Win32.MonitorInfoAt(_anchor, out var monitor) is not { } info)
            return;

        var scale = Win32.ScaleFor(monitor);
        var work = info.rcWork;
        var gap = (int)Math.Round(12 * scale);
        var borderH = AppWindow.Size.Height - AppWindow.ClientSize.Height;
        var minH = (int)Math.Ceiling(MinHeightDip * scale) + borderH;
        var dy = cursor.Y - _resizeStartCursorY;

        int y, h;
        if (_gripAtTop)
        {
            var bottom = _resizeStart.Y + _resizeStart.Height;
            h = Math.Clamp(_resizeStart.Height - dy, minH, Math.Max(minH, bottom - (work.Top + gap)));
            y = bottom - h;
        }
        else
        {
            h = Math.Clamp(_resizeStart.Height + dy, minH, Math.Max(minH, work.Bottom - gap - _resizeStart.Y));
            y = _resizeStart.Y;
        }
        if (h != AppWindow.Size.Height)
            AppWindow.MoveAndResize(new RectInt32(AppWindow.Position.X, y, AppWindow.Size.Width, h));
        e.Handled = true;
    }

    private void HeightGrip_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        HeightGrip.ReleasePointerCapture(e.Pointer);
        EndResize();
    }

    private void HeightGrip_PointerCaptureLost(object sender, PointerRoutedEventArgs e) => EndResize();

    /// <summary>Remembers the height the list was dragged to.</summary>
    private void EndResize()
    {
        if (!_resizing)
            return;
        _resizing = false;
        if (Win32.MonitorInfoAt(_anchor, out var monitor) is null)
            return;
        _state.FlyoutHeight = Math.Round(AppWindow.ClientSize.Height / Win32.ScaleFor(monitor));
        _store.SaveState(_state);
    }

    private void HeightGrip_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        _state.FlyoutHeight = null;
        _store.SaveState(_state);
        PlaceWindow();
        e.Handled = true;
    }

    private void PlayOpenAnimation()
    {
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            return;

        var fromTop = Win32.MonitorInfoAt(_anchor, out _) is { } info && _anchor.Y < info.rcWork.Top;
        var duration = new Duration(TimeSpan.FromMilliseconds(250));

        var slide = new DoubleAnimation
        {
            From = fromTop ? -20 : 20,
            To = 0,
            Duration = duration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(slide, RootTranslate);
        Storyboard.SetTargetProperty(slide, "Y");

        var fade = new DoubleAnimation { From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(150)) };
        Storyboard.SetTarget(fade, Root);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var storyboard = new Storyboard { Children = { slide, fade } };
        storyboard.Begin();
    }
}
