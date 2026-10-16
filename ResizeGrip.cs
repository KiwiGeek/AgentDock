using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AgentDock;

/// <summary>
/// The strip along the flyout's edge that resizes its height. A class of its own because WinUI only
/// lets a subclass set the pointer cursor (<c>ProtectedCursor</c>).
/// </summary>
public sealed partial class ResizeGrip : Grid
{
    public ResizeGrip()
    {
        // Transparent, not null, so the whole strip receives the pointer.
        Background = new SolidColorBrush(Colors.Transparent);
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
    }
}
