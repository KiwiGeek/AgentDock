using Microsoft.UI.Xaml.Media;

namespace AgentDock.Models;

/// <summary>An icon in the settings page's taskbar icon picker. Bound with x:Bind from FlyoutWindow.xaml.</summary>
public sealed partial class TaskbarIconChoice(string id, string name, ImageSource? preview)
{
    public string Id { get; } = id;
    public string Name { get; } = name;
    public ImageSource? Preview { get; } = preview;
}
