using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace AgentDock.Models;

/// <summary>An agent in the settings page's list (bound with x:Bind). Dragged to reorder.</summary>
public sealed partial class AgentListRow(AgentDefinition definition, ImageSource? icon, bool installed)
{
    /// <summary>The object in <see cref="DockSettings.Agents"/>.</summary>
    public AgentDefinition Definition { get; } = definition;
    public string Name => Definition.Name;
    public ImageSource? Icon { get; } = icon;
    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisibility => Icon is null ? Visibility.Visible : Visibility.Collapsed;
    public string Glyph => Definition.Kind == AgentKind.Cli ? "" : "";
    public string Note => installed ? "" : "Not installed";
    public Visibility NoteVisibility => installed ? Visibility.Collapsed : Visibility.Visible;
}
