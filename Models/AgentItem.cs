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

    /// <summary>A preset row: <paramref name="agent"/>'s definition with the preset's arguments, launched in its folder.</summary>
    public AgentItem(Preset preset, AgentItem agent)
    {
        Definition = agent.Definition.WithArguments(preset.Arguments ?? agent.Definition.Arguments);
        Preset = preset;
        ExecutablePath = agent.ExecutablePath;
        AppId = agent.AppId;
        IconSource = agent.IconSource;
        FolderExists = Directory.Exists(preset.Folder);
    }

    public AgentDefinition Definition { get; }

    /// <summary>Set when this row is a preset: the agent's definition, launched in the preset's folder.</summary>
    public Preset? Preset { get; init; }

    public string Name => Preset?.Name ?? Definition.Name;
    public bool IsCli => Definition.Kind == AgentKind.Cli;

    /// <summary>Resolved executable (CLI agents) or exe path (desktop agents).</summary>
    public string? ExecutablePath { get; init; }

    /// <summary>shell:AppsFolder id (desktop agents).</summary>
    public string? AppId { get; init; }

    /// <summary>
    /// Opens in a folder: every CLI agent, and a desktop app that can (Claude and ChatGPT by link,
    /// Cursor by its exe). These are the agents with presets, recent folders, and the working folder applied.
    /// </summary>
    public bool TakesFolder => IsCli || Services.KnownAgents.TakesFolder(Definition);

    public bool IsAvailable => IsAgentAvailable && (Preset is null || (TakesFolder && FolderExists));
    public double ItemOpacity => IsAvailable ? 1.0 : 0.5;

    /// <summary>The agent is installed. For a preset, its folder may still be missing.</summary>
    public bool IsAgentAvailable => IsCli ? ExecutablePath is not null : ExecutablePath is not null || AppId is not null;

    /// <summary>Checked once, when the row is built (rows are rebuilt every time the flyout opens).</summary>
    private bool FolderExists { get; } = true;

    /// <summary>Segoe Fluent Icons glyph shown until (or instead of) a real icon.</summary>
    public string Glyph => IsCli ? "\uE756" : "\uE71D";

    public string Subtitle => Preset is not null ? PresetSubtitle
        : !IsAvailable
        ? (IsCli ? $"{Definition.Command} not found on PATH" : "Not installed")
        : IsCli
            ? CliSubtitle
            : "Desktop app";

    /// <summary>
    /// Known CLI agents can name what they do (DeepSeek Harness opens in the browser); otherwise the
    /// command line. A hosted agent that's running says so, so the Stop button isn't the only clue.
    /// </summary>
    private string CliSubtitle
    {
        get
        {
            var label = Services.KnownAgents.CliSubtitle(Definition)
                ?? string.Join(' ', new[] { Definition.Command, Definition.Arguments }.Where(s => !string.IsNullOrWhiteSpace(s)));
            return IsHostedRunning ? $"Running · {label.ToLowerInvariant()}" : label;
        }
    }

    /// <summary>Whether Agent Dock is hosting this agent's process (no Terminal window).</summary>
    public bool IsHostedRunning =>
        Services.HostedAgents.IsHosted(Definition)
        && Services.HostedAgents.IsRunning(Preset?.Agent ?? Definition.Id);

    public Visibility StopVisibility => IsHostedRunning ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>
    /// Where the icon comes from: an image file, a compiled-in icon, or a shell parsing name to
    /// extract one from. Null shows the glyph. See <see cref="Services.AgentIcons"/>.
    /// </summary>
    public string? IconSource { get; init; }

    /// <summary>The agent's hover background: its "background" image file, or a compiled-in one.</summary>
    public string? BackgroundSource => Services.AgentIcons.FindBackground(Definition);

    private string PresetSubtitle =>
        !TakesFolder ? "Presets only work with agents that open in a folder"
        : !IsAgentAvailable ? (!IsCli ? "Not installed"
            : string.IsNullOrEmpty(Definition.Command) ? $"There's no agent \"{Preset!.Agent}\"" : $"{Definition.Command} not found on PATH")
        : !FolderExists ? $"Folder not found: {Preset!.Folder}"
        : $"{Definition.Name} · {Preset!.Folder}";

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

    /// <summary>
    /// The row's button: the first of the agent's other ways to open (Claude's new chat, ChatGPT
    /// without a folder). Agent rows only; a preset is about its folder.
    /// </summary>
    public Services.OtherLaunch? QuickLaunch =>
        Preset is null && IsAgentAvailable ? Services.KnownAgents.OtherLaunches(Definition).FirstOrDefault() : null;

    public Visibility QuickLaunchVisibility =>
        QuickLaunch is null || IsHostedRunning ? Visibility.Collapsed : Visibility.Visible;
    public string QuickLaunchText => QuickLaunch?.Text ?? "";
    public string QuickLaunchGlyph => QuickLaunch?.Glyph ?? "";

    /// <summary>Call when a hosted process starts or stops, so the subtitle and Stop button refresh.</summary>
    public void RefreshHostedState()
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(IsHostedRunning));
        OnPropertyChanged(nameof(StopVisibility));
        OnPropertyChanged(nameof(QuickLaunchVisibility));
    }

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
