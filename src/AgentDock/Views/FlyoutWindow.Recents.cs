using AgentDock.Models;
using AgentDock.Services.Agents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Windows.System;

namespace AgentDock.Views;

/// <summary>
/// Recent folders under the drawer's usage: the rows, fitting them in, and removing one.
/// </summary>
public sealed partial class FlyoutWindow
{
    /// <summary>The folders currently offered for the hovered agent, newest first, at most five.</summary>
    private List<string> _shownRecents = [];
    /// <summary>Drawer height the recents were last fitted to, so a width change doesn't rebuild them.</summary>
    private double _recentsFitHeight;

    /// <summary>
    /// The last few folders the agent (Claude Code or Codex) was opened in, under the usage. Never
    /// more than five, and never more than fit in the drawer without scrolling. Clicking one opens it there.
    /// </summary>
    private bool ShowRecents(AgentItem item)
    {
        var id = item.Preset?.Agent ?? item.Definition.Id;
        _shownRecents = RecentLaunches.For(_state, id).Take(RecentLaunches.MaxCount).ToList();
        if (_shownRecents.Count == 0)
        {
            RecentSection.Visibility = Visibility.Collapsed;
            return false;
        }
        FillRecentRows(_shownRecents.Count);
        RecentSection.Visibility = Visibility.Visible;
        _recentsFitHeight = 0;
        DispatcherQueue.TryEnqueue(FitRecentsToDrawer);
        return true;
    }

    private void FillRecentRows(int count)
    {
        RecentRows.Children.Clear();
        foreach (var folder in _shownRecents.Take(count))
        {
            var shown = new FolderItem(folder);
            var button = new Button
            {
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new Thickness(8, 4, 8, 4),
                Tag = folder,
                Content = new StackPanel
                {
                    Spacing = 0,
                    Children =
                    {
                        new TextBlock { Text = shown.Name, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200 },
                        new TextBlock
                        {
                            Text = shown.Parent, FontSize = 12, Opacity = 0.7,
                            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 200
                        }
                    }
                }
            };
            AutomationProperties.SetName(button, $"Open {_drawerItem?.Definition.Name ?? "the agent"} in {folder}");
            ToolTipService.SetToolTip(button, folder);
            button.Click += RecentFolder_Click;

            // Right-click, Shift+F10 or the menu key.
            var remove = new MenuFlyoutItem { Text = "Remove from recents", Icon = new FontIcon { Glyph = "" }, Tag = folder };
            remove.Click += RemoveRecent_Click;
            var menu = new MenuFlyout { Items = { remove } };
            // Shown by WinUI itself, so the hold starts as it opens.
            menu.Opening += (_, _) => HoldWhileOpen(menu);
            button.ContextFlyout = menu;
            RecentRows.Children.Add(button);
        }
    }

    private void RemoveRecent_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuFlyoutItem { Tag: string folder } || _drawerItem is not { } item)
            return;
        var id = item.Preset?.Agent ?? item.Definition.Id;
        if (!RecentLaunches.Forget(_state, id, folder))
            return;
        _store.SaveState(_state);
        // The drawer shows what's left; with nothing left and no usage to show, it closes (or shows the overview).
        if (!ShowRecents(item) && UsagePanel.Visibility != Visibility.Visible && !UsageOffer.IsOpen && !ShowSummary())
            CloseDrawer(animate: true);
    }

    /// <summary>Drops recent rows from the bottom until the drawer doesn't have to scroll.</summary>
    private void FitRecentsToDrawer()
    {
        if (_shownRecents.Count == 0 || DrawerHost.ActualHeight <= 1)
            return;

        RecentSection.Visibility = Visibility.Visible;
        var count = _shownRecents.Count;
        FillRecentRows(count);
        while (count > 0 && !RecentsFit())
        {
            count--;
            FillRecentRows(count);
        }
        _recentsFitHeight = DrawerHost.ActualHeight;
        if (count == 0)
            RecentSection.Visibility = Visibility.Collapsed;
    }

    private bool RecentsFit()
    {
        DrawerPanel.UpdateLayout();
        if (RecentSection.ActualHeight <= 0)
            return true;
        try
        {
            var top = RecentSection.TransformToVisual(DrawerHost).TransformPoint(new Windows.Foundation.Point()).Y;
            return top + RecentSection.ActualHeight <= DrawerHost.ActualHeight + 0.5;
        }
        catch (ArgumentException)
        {
            // Not in the live tree yet; the next size change fits them.
            return true;
        }
    }

    private void RecentFolder_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string folder } || _drawerItem is null)
            return;
        // The base agent, so a preset's own arguments aren't applied to a different folder.
        var id = _drawerItem.Preset?.Agent ?? _drawerItem.Definition.Id;
        var item = _agents.FirstOrDefault(a => a.Preset is null && a.Definition.Id.Equals(id, StringComparison.OrdinalIgnoreCase))
            ?? _drawerItem;
        Launch(item, folder);
    }
}
