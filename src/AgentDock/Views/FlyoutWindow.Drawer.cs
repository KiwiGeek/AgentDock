using System.Diagnostics;
using AgentDock.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;
using Windows.System;

namespace AgentDock.Views;

/// <summary>
/// The drawer beside the list: opening and closing it, and holding it while a pop-up is open.
/// </summary>
public sealed partial class FlyoutWindow
{
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _drawerCloseTimer;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _drawerSlideTimer;
    private readonly Stopwatch _drawerSlideWatch = new();
    private double _drawerSlideFrom;
    private double _drawerSlideTo;
    private int _drawerSlideMs;
    private Action? _drawerSlideCompleted;
    private bool _drawerOpen;
    private bool _drawerClosing;
    /// <summary>+1 when the drawer opens to the right of the list, -1 to the left.</summary>
    private int _drawerSide = 1;
    /// <summary>Screen X of the list's left edge, so growing the window doesn't slide the list.</summary>
    private int _listScreenX;

    private void Drawer_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _drawerCloseTimer?.Stop();
        // The drawer belongs to the agent, so the banner keeps its colour.
        _bannerResetTimer?.Stop();
        // The row's pointer-exit may already have started fading the picture. Bring it back.
        if (_state.WaifuModeEnabled && _drawerItem?.BackgroundSource is { } source)
            FadeBackdrop(source);
    }

    private void Drawer_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        ScheduleDrawerClose();
        ScheduleBannerReset();
    }

    /// <summary>The animated width changed, so the window grows or shrinks with it.</summary>
    private void OnDrawerWidthChanged(DependencyObject sender, DependencyProperty dp)
    {
        if (!_drawerOpen || !AppWindow.IsVisible)
            return;
        ApplyWindowForDrawer();
        // The pictures slide with the drawer (LayoutBackdrop), in the same step as the window.
        if (_layers is not null)
            LayoutBackdrop();
    }

    /// <summary>The column was arranged, so a picture that covers it needs laying out again.</summary>
    private void DrawerHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_layers is not null)
            LayoutBackdrop();
        if (Math.Abs(e.NewSize.Height - _recentsFitHeight) > 0.5)
            FitRecentsToDrawer();
    }

    /// <summary>
    /// A short grace period, so the pointer can move from the row into the drawer (to click
    /// Set up) without it closing. Then: over an agent without a drawer, the drawer closes (the
    /// window shrinks to the list); over no agent, the usage overview shows if it's on, else it
    /// closes. So this also runs with the drawer shut, to bring the overview back.
    /// </summary>
    private void ScheduleDrawerClose()
    {
        if (_popupsOpen > 0 || _widthResizing || (!_drawerOpen && !_state.ShowUsageSummary))
            return;
        if (_drawerCloseTimer is null)
        {
            _drawerCloseTimer = DispatcherQueue.CreateTimer();
            _drawerCloseTimer.Interval = TimeSpan.FromMilliseconds(250);
            _drawerCloseTimer.IsRepeating = false;
            _drawerCloseTimer.Tick += (_, _) =>
            {
                // The pointer may have moved onto an agent without a drawer, whose picture is fading in.
                if (_state.WaifuModeEnabled && _hoverItem is null)
                    FadeBackdrop(null);
                // The overview is for when no agent is pointed at (owner, 2026-10-16).
                if (_hoverItem is null && ShowSummary())
                    return;
                CloseDrawer(animate: true);
            };
        }
        // Restart the grace period from now.
        _drawerCloseTimer.Stop();
        _drawerCloseTimer.Start();
    }

    private void OpenDrawer()
    {
        _drawerCloseTimer?.Stop();
        if (_drawerOpen && !_drawerClosing)
            return;

        if (!_drawerOpen)
        {
            // Right of the list if it fits on this monitor, otherwise left.
            var info = Win32.MonitorInfoAt(_anchor, out var monitor);
            var scale = info is not null ? Win32.ScaleFor(monitor) : Root.XamlRoot.RasterizationScale;
            var right = AppWindow.Position.X + AppWindow.Size.Width + (int)Math.Ceiling(DrawerWidthDip * scale);
            _drawerSide = info is { } work && right > work.rcWork.Right ? -1 : 1;
            var onRight = _drawerSide > 0;

            // Content stays against the list. The window growing uncovers the rest.
            Grid.SetColumn(ListColumn, onRight ? 0 : 1);
            Grid.SetColumn(DrawerHost, onRight ? 1 : 0);
            DrawerPanel.HorizontalAlignment = onRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            // The toolbar stays with the list, rather than riding the window's far edge.
            FooterContent.HorizontalAlignment = onRight ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            _listScreenX = AppWindow.Position.X;
            DrawerHost.Width = 0;
            DrawerHost.Visibility = Visibility.Visible;
            _drawerOpen = true;
            LayoutBackdrop();
        }
        _drawerClosing = false;
        SlideDrawer(DrawerWidthDip, () =>
        {
            // The wider toolbar may have unwrapped the status line, so fit the height again.
            if (_drawerOpen && !_drawerClosing && AppWindow.IsVisible)
                PlaceWindow();
        });
    }

    private void CloseDrawer(bool animate)
    {
        _drawerCloseTimer?.Stop();
        if (!_drawerOpen)
            return;

        if (!animate || !AppWindow.IsVisible)
        {
            StopDrawerSlide();
            FinishDrawerClose();
            return;
        }

        _drawerClosing = true;
        // Shrinks back to the list, then closes, unless it was reopened meanwhile.
        SlideDrawer(0, () =>
        {
            if (_drawerClosing)
                FinishDrawerClose();
        });
    }

    private void FinishDrawerClose()
    {
        _drawerClosing = false;
        _drawerOpen = false;
        _summaryShowing = false;
        SummaryPanel.Visibility = Visibility.Collapsed;
        DrawerHost.Width = 0;
        DrawerHost.Visibility = Visibility.Collapsed;
        Grid.SetColumn(ListColumn, 0);
        Grid.SetColumn(DrawerHost, 1);
        FooterContent.HorizontalAlignment = HorizontalAlignment.Left;
        // Back to the list on its own, including height: the status line may need to wrap again.
        if (AppWindow.IsVisible)
            PlaceWindow();
    }

    /// <summary>
    /// Animates the drawer's column. A storyboard with <c>EnableDependentAnimation</c> changed the
    /// width during layout and the list slid sideways while the window grew, so this steps the width
    /// from the dispatcher instead. Each step resizes the window to match.
    /// </summary>
    private void SlideDrawer(double toWidth, Action? completed)
    {
        StopDrawerSlide();
        var from = double.IsNaN(DrawerHost.Width) ? 0 : DrawerHost.Width;

        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled || Math.Abs(toWidth - from) < 0.5)
        {
            DrawerHost.Width = toWidth;
            ApplyWindowForDrawer();
            completed?.Invoke();
            return;
        }

        _drawerSlideFrom = from;
        _drawerSlideTo = toWidth;
        _drawerSlideMs = toWidth > from ? 250 : 200;
        _drawerSlideCompleted = completed;
        _drawerSlideWatch.Restart();
        _drawerSlideTimer ??= CreateDrawerSlideTimer();
        _drawerSlideTimer.Start();
    }

    private Microsoft.UI.Dispatching.DispatcherQueueTimer CreateDrawerSlideTimer()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(16);
        timer.IsRepeating = true;
        timer.Tick += (_, _) =>
        {
            var t = Math.Clamp(_drawerSlideWatch.Elapsed.TotalMilliseconds / _drawerSlideMs, 0, 1);
            var opening = _drawerSlideTo > _drawerSlideFrom;
            // Cubic ease-out while opening, ease-in while closing. Same curve as the old storyboard.
            var eased = opening ? 1 - Math.Pow(1 - t, 3) : Math.Pow(t, 3);
            DrawerHost.Width = _drawerSlideFrom + (_drawerSlideTo - _drawerSlideFrom) * eased;
            if (t < 1)
                return;
            var completed = _drawerSlideCompleted;
            StopDrawerSlide();
            completed?.Invoke();
        };
        return timer;
    }

    /// <summary>Drops the slide in progress without running its completion.</summary>
    private void StopDrawerSlide()
    {
        _drawerSlideTimer?.Stop();
        _drawerSlideCompleted = null;
    }

    /// <summary>
    /// Sets the window to the list plus whatever width the drawer column has now. The list's left
    /// edge stays where <see cref="PlaceWindow"/> put it; opening left moves the window's left edge.
    /// </summary>
    private void ApplyWindowForDrawer()
    {
        if (!AppWindow.IsVisible || Root.XamlRoot is null)
            return;
        if (Win32.MonitorInfoAt(_anchor, out var monitor) is not { })
            return;

        var scale = Win32.ScaleFor(monitor);
        var drawerPx = (int)Math.Round(DrawerDip() * scale);
        var borderW = AppWindow.Size.Width - AppWindow.ClientSize.Width;
        var w = (int)Math.Ceiling(_listWidthDip * scale) + drawerPx + borderW;
        var x = _drawerSide > 0 || drawerPx == 0 ? _listScreenX : _listScreenX - drawerPx;
        var pos = AppWindow.Position;
        if (pos.X == x && AppWindow.Size.Width == w)
            return;
        AppWindow.MoveAndResize(new RectInt32(x, pos.Y, w, AppWindow.Size.Height));
    }

    private double DrawerDip()
    {
        if (DrawerHost.Visibility != Visibility.Visible)
            return 0;
        // Width is what the animation sets. ActualWidth can lag while the window is still catching up.
        var width = double.IsNaN(DrawerHost.Width) ? DrawerHost.ActualWidth : DrawerHost.Width;
        return double.IsNaN(width) || width < 0 ? 0 : width;
    }

    /// <summary>
    /// Pop-ups open over the flyout: menus, confirmations, renaming. While any is open the drawer,
    /// the picture and the banner's colour hold.
    /// </summary>
    private int _popupsOpen;

    /// <summary>
    /// Call as <paramref name="flyout"/> is shown. A pop-up takes the pointer, so the row or drawer
    /// under it sees the pointer leave. Without this the drawer would start closing, and closing it
    /// resizes the window, which makes Windows close the pop-up (and the cursor spin meanwhile).
    /// When it closes, things settle as if the pointer had just left, unless it's still over a row
    /// or the drawer.
    /// </summary>
    private void HoldWhileOpen(Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase flyout)
    {
        _popupsOpen++;
        _drawerCloseTimer?.Stop();
        _bannerResetTimer?.Stop();

        void OnClosed(object? sender, object e)
        {
            flyout.Closed -= OnClosed;
            _popupsOpen = Math.Max(0, _popupsOpen - 1);
            // After any follow-up (Remove → its confirmation) has had the chance to open.
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (_popupsOpen > 0 || PointerOverRowOrDrawer())
                    return;
                _hoverItem = null;
                if (_state.WaifuModeEnabled && !_drawerOpen)
                    FadeBackdrop(null);
                ScheduleDrawerClose();
                ScheduleBannerReset();
            });
        }
        flyout.Closed += OnClosed;
    }

    /// <summary>Whether the mouse is over an agent row or the drawer right now (no pointer event says so after a pop-up).</summary>
    private bool PointerOverRowOrDrawer()
    {
        if (!AppWindow.IsVisible || Root.XamlRoot is null || !Win32.GetCursorPos(out var cursor) || !Win32.ScreenToClient(_hwnd, ref cursor))
            return false;
        var scale = Root.XamlRoot.RasterizationScale;
        var point = new Windows.Foundation.Point(cursor.X / scale, cursor.Y / scale);
        foreach (var element in Microsoft.UI.Xaml.Media.VisualTreeHelper.FindElementsInHostCoordinates(point, Root))
        {
            if (element == DrawerHost)
                return true;
            if (element is ListViewItem)
                return true;
        }
        return false;
    }
}
