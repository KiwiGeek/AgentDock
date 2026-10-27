using AgentDock.Models;
using AgentDock.Services.Agents;
using AgentDock.Services.Launching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AgentDock.Views;

/// <summary>
/// Presets: an agent pinned to a folder, with their rows, menu, renaming and saving.
/// </summary>
public sealed partial class FlyoutWindow
{
    /// <summary>Right-click, Shift+F10 or the menu key on a row.</summary>
    private void Agent_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        // From the mouse, OriginalSource is something inside the row; from the keyboard, the row itself.
        if (sender is not ListView list || ContainerOf(e.OriginalSource as DependencyObject) is not { } anchor
            || list.ItemFromContainer(anchor) is not AgentItem item)
            return;

        var menu = new MenuFlyout();
        if (item.Preset is { } preset)
        {
            menu.Items.Add(MenuItem("Rename", "", () => StartRename(preset, anchor)));
            menu.Items.Add(MenuItem("Remove", "", () => RemovePreset(preset)));
        }
        else
        {
            var others = KnownAgents.OtherLaunches(item.Definition);
            foreach (var other in others)
            {
                var entry = MenuItem(other.Text, other.Glyph, () => Launch(item, other));
                entry.IsEnabled = item.IsAgentAvailable;
                menu.Items.Add(entry);
            }
            if (others.Count > 0)
                menu.Items.Add(new MenuFlyoutSeparator());

            if (item.IsHostedRunning)
            {
                menu.Items.Add(MenuItem("Stop", "\uE71A", () =>
                    HostedAgents.Stop(item.Preset?.Agent ?? item.Definition.Id)));
                menu.Items.Add(new MenuFlyoutSeparator());
            }

            // Most desktop apps don't open in a folder, so they can't be presets (Claude can).
            if (item.TakesFolder)
            {
                var folder = CurrentFolder;
                menu.Items.Add(MenuItem($"Save as preset for {new FolderItem(folder).Name}", "\uE718", () => SavePreset(item, folder)));
                menu.Items.Add(new MenuFlyoutSeparator());
            }
            menu.Items.Add(MenuItem("Settings", "\uE713", () => OpenSettings(agentId: item.Definition.Id)));
            var remove = MenuItem("Remove from list", "\uE74D", () => ConfirmRemoveAgent(item, anchor));
            remove.IsEnabled = _settingsError is null; // agents.json that couldn't be read isn't written
            menu.Items.Add(remove);
        }

        HoldWhileOpen(menu);
        if (e.TryGetPosition(anchor, out var point))
            menu.ShowAt(anchor, point);
        else
            menu.ShowAt(anchor);
        e.Handled = true;
    }

    /// <summary>Asks beside the row, then takes the agent out of the list. Its presets stay.</summary>
    private void ConfirmRemoveAgent(AgentItem item, FrameworkElement anchor)
    {
        var confirm = new Button { Content = "Remove", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        var flyout = new Flyout
        {
            Content = new StackPanel
            {
                Spacing = 12, MaxWidth = 260,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Remove {item.Definition.Name} from your agents? You can add it again from settings.",
                        TextWrapping = TextWrapping.Wrap
                    },
                    confirm
                }
            }
        };
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            if (!_settings.Agents.Remove(item.Definition))
                return;
            // Don't leave a hosted server running after its row is gone.
            if (HostedAgents.IsHosted(item.Definition))
                HostedAgents.Stop(item.Definition.Id);
            SaveAgentList();
            if (AppWindow.IsVisible)
                PlaceWindow();
            DispatcherQueue.TryEnqueue(FocusFirstAgent);
        };
        HoldWhileOpen(flyout);
        flyout.ShowAt(anchor);
    }

    private static ListViewItem? ContainerOf(DependencyObject? element)
    {
        while (element is not null and not ListViewItem)
            element = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(element);
        return element as ListViewItem;
    }

    private MenuFlyoutItem MenuItem(string text, string glyph, Action action)
    {
        var menuItem = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
        // After the menu has closed, so a follow-up flyout (rename) can open.
        menuItem.Click += (_, _) => DispatcherQueue.TryEnqueue(() => action());
        return menuItem;
    }

    private void SavePreset(AgentItem agent, string folder)
    {
        if (_presetsError is not null)
        {
            SetStatus(_presetsError); // never overwrite a file the user is part-way through editing
            return;
        }

        var existing = _presetList.FirstOrDefault(p =>
            p.Agent.Equals(agent.Definition.Id, StringComparison.OrdinalIgnoreCase) && SamePath(p.Folder, folder));
        if (existing is not null)
        {
            SetStatus($"That's already a preset: \"{existing.Name}\".");
            return;
        }

        var preset = new Preset { Name = UniquePresetName(new FolderItem(folder).Name), Agent = agent.Definition.Id, Folder = folder };
        _presetList.Add(preset);
        SavePresetsAndRefresh($"Saved \"{preset.Name}\". Right-click it to rename it.");
    }

    private void RemovePreset(Preset preset)
    {
        if (_presetsError is not null)
        {
            SetStatus(_presetsError);
            return;
        }
        _presetList.Remove(preset);
        SavePresetsAndRefresh($"Removed \"{preset.Name}\".");
    }

    /// <summary>A small flyout with the name in a text box. Enter saves, Esc cancels.</summary>
    private void StartRename(Preset preset, FrameworkElement anchor)
    {
        var box = new TextBox { Text = preset.Name, Width = 260 };
        AutomationProperties.SetName(box, "Preset name");
        var error = new TextBlock
        {
            Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap, MaxWidth = 260,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemFillColorCriticalBrush"]
        };
        var save = new Button
        {
            Content = "Rename", HorizontalAlignment = HorizontalAlignment.Right,
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        var flyout = new Flyout { Content = new StackPanel { Spacing = 8, Children = { box, error, save } } };

        void Commit()
        {
            if (TryRenamePreset(preset, box.Text) is { } message)
            {
                error.Text = message;
                error.Visibility = Visibility.Visible;
                return;
            }
            flyout.Hide();
        }

        box.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        save.Click += (_, _) => Commit();
        flyout.Opened += (_, _) =>
        {
            box.Focus(FocusState.Programmatic);
            box.SelectAll();
        };
        HoldWhileOpen(flyout);
        flyout.ShowAt(anchor);
    }

    /// <summary>Returns null on success, or why the name can't be used.</summary>
    private string? TryRenamePreset(Preset preset, string newName)
    {
        if (_presetsError is not null)
            return _presetsError;

        // Quotes would break the jump list's --preset "<name>" argument.
        var name = newName.Replace("\"", "").Trim();
        if (name.Length == 0)
            return "Enter a name.";
        if (_presetList.Any(p => p != preset && p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            return "Another preset already has that name.";

        preset.Name = name;
        SavePresetsAndRefresh(null);
        return null;
    }

    private void SavePresetsAndRefresh(string? status)
    {
        _store.SavePresets(_presetList);
        ShowAgents(_agents);
        StatusText.Text = status ?? "";
        PlaceWindow();
        UpdateJumpList();
    }

    private string UniquePresetName(string baseName)
    {
        baseName = baseName.Replace("\"", "").Trim();
        var name = baseName;
        for (var n = 2; _presetList.Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); n++)
            name = $"{baseName} {n}";
        return name;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
}
