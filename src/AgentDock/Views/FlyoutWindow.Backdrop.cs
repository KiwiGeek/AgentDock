using AgentDock.Models;
using AgentDock.Services.Agents;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AgentDock.Views;

/// <summary>
/// The hover picture behind the flyout: two layers, cross-faded, sized to the drawer's state.
/// </summary>
public sealed partial class FlyoutWindow
{
    /// <summary>
    /// One agent's picture. Two layers, so moving from one agent to another cross-fades.
    /// The picture is a single image across the whole window; the window growing uncovers more of it.
    /// </summary>
    private sealed class BackdropLayer(Image image)
    {
        public Image Image { get; } = image;
        public string? Path { get; set; }
    }

    /// <summary>
    /// Where the subject of the built-in pictures stands, as a fraction of the picture's width
    /// (measured on the Amp and Claude pictures: 0.72–0.73).
    /// </summary>
    private const double BackdropSubjectX = 0.72;

    private readonly Dictionary<string, BitmapImage> _backdrops = new(StringComparer.OrdinalIgnoreCase);
    private BackdropLayer[]? _layers;
    private int _activeLayer;
    private Storyboard? _backdropFade;

    private BackdropLayer[] Layers => _layers ??=
        [new BackdropLayer(HoverImageA), new BackdropLayer(HoverImageB)];

    /// <summary>The row under the pointer, if any.</summary>
    private AgentItem? _hoverItem;

    private void AgentRow_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var item = ((FrameworkElement)sender).DataContext as AgentItem;
        _hoverItem = item;
        ColorBannerFor(item);
        var previous = _drawerOpen && !_drawerClosing ? _drawerItem : null;
        var fromSummary = _drawerOpen && !_drawerClosing && _summaryShowing;
        var drawer = item is not null && PrepareDrawer(item);
        // Switching an open drawer to another agent (or from the overview): fade its new content in rather than swapping it.
        if (drawer && (fromSummary || (previous is not null
            && KnownAgents.CommandName(previous.Definition.Command) != KnownAgents.CommandName(item!.Definition.Command))))
            FadeInDrawerContent();
        // The drawer first: the image layout depends on which side it opens.
        if (drawer)
            OpenDrawer();
        else
            ScheduleDrawerClose();
        // The settings page can turn the pictures off. The usage drawer is separate and still opens.
        if (_state.ShowHoverBackgrounds)
            FadeBackdrop(item?.BackgroundSource);
    }

    private void AgentRow_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        _hoverItem = null;
        ScheduleBannerReset();
        // Moving into the drawer should keep the picture. It fades when the drawer actually closes,
        // or straight away when the row had no drawer.
        if (_state.ShowHoverBackgrounds && !_drawerOpen && _popupsOpen == 0)
            FadeBackdrop(null);
        ScheduleDrawerClose();
    }

    /// <summary>
    /// Fades the given picture in behind the flyout, cross-fading from whatever picture was showing,
    /// or fades out when <paramref name="path"/> is null. Where it sits follows the drawer (LayoutBackdrop).
    /// </summary>
    private void FadeBackdrop(string? path)
    {
        var layers = Layers;
        if (path is not null)
        {
            if (!_backdrops.TryGetValue(path, out var image))
            {
                try
                {
                    _backdrops[path] = image = new BitmapImage();
                    // The layout needs the picture's proportions, which are known once it has loaded.
                    image.ImageOpened += (_, _) => LayoutLayersShowing(image);
                    if (AgentIcons.IsBundledBackground(path))
                        _ = LoadBundledBackgroundAsync(image, path);
                    else
                        image.UriSource = new Uri(path);
                }
                catch (Exception ex) when (ex is UriFormatException or ArgumentException)
                {
                    return;
                }
            }

            // Moving back onto the agent already showing (its fade-out not finished) fades the same
            // layer back in; a different agent's picture goes on the other layer.
            if (!string.Equals(layers[_activeLayer].Path, path, StringComparison.OrdinalIgnoreCase))
            {
                _activeLayer = 1 - _activeLayer;
                var incoming = layers[_activeLayer];
                incoming.Path = path;
                incoming.Image.Source = image;
            }
            LayoutBackdrop(layers[_activeLayer]);
        }

        // Targets: the active layer to the picture's opacity (or 0), the other always to 0.
        var targets = layers.Select((layer, i) => (Layer: layer, To: path is not null && i == _activeLayer ? HoverBackdropOpacity : 0)).ToList();

        // Stopping a storyboard snaps back to the values before it ran, so carry the current ones over.
        var current = targets.Select(t => t.Layer.Image.Opacity).ToList();
        _backdropFade?.Stop();
        for (var i = 0; i < targets.Count; i++)
            targets[i].Layer.Image.Opacity = current[i];

        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled || !AppWindow.IsVisible)
        {
            foreach (var (layer, to) in targets)
                layer.Image.Opacity = to;
            return;
        }

        var storyboard = new Storyboard();
        for (var i = 0; i < targets.Count; i++)
        {
            var (layer, to) = targets[i];
            // Fading in is slower than fading out (tuned with the owner).
            var duration = new Duration(TimeSpan.FromMilliseconds(to > current[i] ? 900 : 600));
            var fade = new DoubleAnimation
            {
                From = current[i], To = to, Duration = duration,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, layer.Image);
            Storyboard.SetTargetProperty(fade, "Opacity");
            storyboard.Children.Add(fade);
        }
        _backdropFade = storyboard;
        storyboard.Begin();
    }

    private Storyboard? _drawerContentFade;

    private void FadeInDrawerContent()
    {
        _drawerContentFade?.Stop();
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            DrawerPanel.Opacity = 1;
            return;
        }
        var fade = new DoubleAnimation
        {
            From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(300)),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        Storyboard.SetTarget(fade, DrawerPanel);
        Storyboard.SetTargetProperty(fade, "Opacity");
        _drawerContentFade = new Storyboard { Children = { fade } };
        _drawerContentFade.Begin();
    }

    /// <summary>A compiled-in background, decoded from the exe's resources.</summary>
    private async Task LoadBundledBackgroundAsync(BitmapImage image, string source)
    {
        try
        {
            using var stream = AgentIcons.OpenBundledBackground(source);
            if (stream is null)
                return;
            await image.SetSourceAsync(stream.AsRandomAccessStream());
            LayoutLayersShowing(image);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A missing picture just means no background.
        }
    }

    private void LayoutLayersShowing(BitmapImage image)
    {
        foreach (var layer in Layers.Where(l => l.Image.Source == image))
            LayoutBackdrop(layer);
    }

    /// <summary>Lays out every layer again, e.g. when the drawer opens on a different side.</summary>
    private void LayoutBackdrop()
    {
        foreach (var layer in Layers)
            LayoutBackdrop(layer);
    }

    /// <summary>
    /// Places a picture by how far the drawer is open, whichever agent it belongs to. Closed: the
    /// subject is centred in the list. Open: the picture spans the list and the drawer. In between
    /// it slides with the drawer, so switching to an agent without a drawer doesn't leave the
    /// closing drawer empty, and the reverse doesn't jump (owner's request).
    /// </summary>
    private void LayoutBackdrop(BackdropLayer layer)
    {
        if (layer.Image.Source is not BitmapImage { PixelWidth: > 0, PixelHeight: > 0 } image)
            return;

        // Always big enough for the list and an open drawer, so sliding never needs a zoom.
        var height = Root.ActualHeight;
        var scale = Math.Max((_listWidthDip + DrawerWidthDip) / image.PixelWidth, height / image.PixelHeight);
        var width = image.PixelWidth * scale;
        var imageHeight = image.PixelHeight * scale;

        // Both ends in list coordinates (0 = the list's left edge, which never moves on screen).
        // Closed: subject over the middle of the list (past the banner). Open: subject over the middle
        // of list and drawer together (the drawer is right of the list, or left at negative x). Each
        // only as far as the picture still covers what's on screen. Pinning the open picture's edge to
        // the window's instead left the subject well left of centre (owner's screenshot, 2026-10-19).
        var closed = Math.Clamp(BannerWidthDip + (_listWidthDip - BannerWidthDip) / 2 - width * BackdropSubjectX,
            _listWidthDip - width, 0);
        var spanLeft = _drawerSide > 0 ? 0 : -DrawerWidthDip;
        var spanRight = spanLeft + _listWidthDip + DrawerWidthDip;
        var open = Math.Clamp((spanLeft + spanRight + BannerWidthDip) / 2 - width * BackdropSubjectX,
            spanRight - width, spanLeft);
        var drawer = DrawerDip();
        var t = Math.Clamp(drawer / DrawerWidthDip, 0, 1);
        var leftInList = closed + (open - closed) * t;
        // On the left the drawer comes first in the window, so the list starts that far in.
        var listLeft = _drawerOpen && _drawerSide < 0 ? drawer : 0;

        layer.Image.Width = width;
        layer.Image.Height = imageHeight;
        layer.Image.Margin = new Thickness(listLeft + leftInList, (height - imageHeight) / 2, 0, 0);
    }
}
