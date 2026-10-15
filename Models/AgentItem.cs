using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AgentDock.Models;

/// <summary>An agent as shown in the flyout. Bound with x:Bind from FlyoutWindow.xaml.</summary>
public sealed partial class AgentItem : INotifyPropertyChanged
{
    private ImageSource? _icon;
    private string? _shortcut;

    public AgentItem(AgentDefinition definition) => Definition = definition;

    public AgentDefinition Definition { get; }
    public string Name => Definition.Name;
    public bool IsCli => Definition.Kind == AgentKind.Cli;

    /// <summary>Resolved executable (CLI agents) or exe path (desktop agents).</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>shell:AppsFolder id (desktop agents).</summary>
    public string? AppId { get; init; }

    public bool IsAvailable => IsCli ? ExecutablePath is not null : ExecutablePath is not null || AppId is not null;
    public double ItemOpacity => IsAvailable ? 1.0 : 0.5;

    /// <summary>Segoe Fluent Icons glyph shown until (or instead of) a real icon.</summary>
    public string Glyph => IsCli ? "\uE756" : "\uE71D";

    public string Subtitle => !IsAvailable
        ? (IsCli ? $"{Definition.Command} not found on PATH" : "Not installed")
        : IsCli
            ? string.Join(' ', new[] { Definition.Command, Definition.Arguments }.Where(s => !string.IsNullOrWhiteSpace(s)))
            : "Desktop app";

    /// <summary>Parsing name handed to the shell to extract an icon.</summary>
    public string? IconSource =>
        ExecutablePath is { } p && p.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? p
        : AppId is not null ? $@"shell:AppsFolder\{AppId}"
        : null;

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            _icon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(GlyphVisibility));
        }
    }

    public Visibility GlyphVisibility => _icon is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Number key that launches this item ("1"–"9"), if any.</summary>
    public string? Shortcut
    {
        get => _shortcut;
        set
        {
            _shortcut = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ShortcutVisibility));
        }
    }

    public Visibility ShortcutVisibility => _shortcut is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Screen readers announce list items by ToString().</summary>
    public override string ToString() => $"{Name}, {Subtitle}";

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class FolderItem(string path)
{
    public string Path { get; } = path;
    public string Name => System.IO.Path.GetFileName(Path.TrimEnd('\\')) is { Length: > 0 } n ? n : Path;
    public string Parent => System.IO.Path.GetDirectoryName(Path.TrimEnd('\\')) ?? "";
}
