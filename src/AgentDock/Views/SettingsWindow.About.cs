using System.Diagnostics;
using AgentDock.Services;
using AgentDock.Services.Agents;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace AgentDock.Views;

/// <summary>
/// Settings → About (issue #4; owner's choice, 2026-10-26: laid out like Windows' Settings > System >
/// About). What's running and which kind of copy it is, the updater's state with Check for updates
/// and Restart now, links, the data folders, and the licence. With waifus on, the waifu portraits
/// (JPEGs of the taskbar icon sources) cycle behind it, fading in with a slow zoom and pan (owner's
/// request: "ken burns" them).
/// </summary>
public sealed partial class SettingsWindow
{
    private const string TagAbout = "about";

    private void ShowAbout()
    {
        _editingAgent = null;
        _editingApi = null;
        _selectedTag = TagAbout;
        HideAllPanes();
        AboutPage.Visibility = Visibility.Visible;
        SettingsTitle.Text = "About";

        if (AboutIcon.Source is null)
        {
            try { AboutIcon.Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AgentDock.ico"))); }
            catch (Exception ex) when (ex is UriFormatException or ArgumentException) { }
        }
        AboutVersion.Text = $"Version {Updates.Version}";
        AboutCopyKind.Text = $"{Updates.CopyKind} · {Updates.Architecture}";
        ReleaseNotesTitle.Text = Updates.IsInstalled ? $"Release notes for {Updates.Version}" : "Release notes";
        FillAboutFolders();

        Updates.Changed -= OnUpdatesChanged;
        Updates.Changed += OnUpdatesChanged;
        UpdateAboutStatus();

        SyncAgentBackdrop();
        StartSlideshow();
        SettingsScroller.ChangeView(null, 0, null, disableAnimation: true);
    }

    private void OnUpdatesChanged() => DispatcherQueue.TryEnqueue(() =>
    {
        if (_selectedTag == TagAbout)
            UpdateAboutStatus();
    });

    /// <summary>The Updates card: what the updater says, and the button that fits.</summary>
    private void UpdateAboutStatus()
    {
        var status = Updates.Status;
        var busy = status is UpdateStatus.Checking or UpdateStatus.Downloading;
        UpdateRing.IsActive = busy;
        UpdateRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateGlyph.Visibility = busy ? Visibility.Collapsed : Visibility.Visible;
        UpdateProgress.Visibility = status == UpdateStatus.Downloading ? Visibility.Visible : Visibility.Collapsed;
        UpdateProgress.Value = Updates.Progress;
        RestartUpdateButton.Visibility = status == UpdateStatus.Ready ? Visibility.Visible : Visibility.Collapsed;
        CheckUpdatesButton.Visibility = status is UpdateStatus.NotInstalled or UpdateStatus.Ready ? Visibility.Collapsed : Visibility.Visible;
        CheckUpdatesButton.IsEnabled = !busy;

        var checkedAt = Updates.LastChecked is { } at ? $"Last checked {at.LocalDateTime:t}" : "Not checked yet since Agent Dock started";
        (UpdateGlyph.Glyph, UpdateTitle.Text, UpdateDetail.Text) = status switch
        {
            UpdateStatus.NotInstalled => ("", "Updates come with the installed copy",
#if DEBUG
                "This is a Debug build, so it doesn't update itself."),
#else
                "This copy wasn't installed with Setup, so it doesn't update itself. New versions are on GitHub."),
#endif
            UpdateStatus.Checking => ("", "Checking for updates", "Asking GitHub for the latest release."),
            UpdateStatus.Downloading => ("", "Downloading an update", $"{Updates.Progress}%"),
            UpdateStatus.UpToDate => ("", "You're up to date", checkedAt),
            UpdateStatus.Ready => ("", $"Version {Updates.PendingVersion} is ready",
                "Restart Agent Dock to finish updating, or it updates the next time it starts."),
            UpdateStatus.Failed => ("", "Couldn't check for updates", $"{Updates.Error} {checkedAt}."),
            _ => ("", "Agent Dock checks for updates every six hours", checkedAt)
        };
    }

    private async void CheckUpdates_Click(object sender, RoutedEventArgs e) => await Updates.CheckNowAsync();

    private void RestartUpdate_Click(object sender, RoutedEventArgs e) =>
        ((App)Application.Current).RestartToUpdate();

    private async void AboutLink_Click(object sender, RoutedEventArgs e)
    {
        var url = (sender as FrameworkElement)?.Tag switch
        {
            "repo" => Updates.RepoUrl,
            "notes" => Updates.IsInstalled ? $"{Updates.RepoUrl}/releases/tag/v{Updates.Version}" : $"{Updates.RepoUrl}/releases",
            "issue" => $"{Updates.RepoUrl}/issues/new",
            "licence" => $"{Updates.RepoUrl}/blob/master/LICENSE",
            _ => null
        };
        if (url is not null)
            await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
    }

    /// <summary>Puts the version, the kind of copy and Windows' version on the clipboard, for a bug report.</summary>
    private void AboutCopy_Click(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText($"Agent Dock {Updates.Version}\r\n{Updates.CopyKind}, {Updates.Architecture}\r\nWindows {Environment.OSVersion.Version}\r\n");
        try
        {
            Clipboard.SetContent(package);
            Clipboard.Flush();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Another app holding the clipboard; nothing useful to say.
            return;
        }

        AboutCopyGlyph.Glyph = "";
        AboutCopyText.Text = "Copied";
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromSeconds(2);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            AboutCopyGlyph.Glyph = "";
            AboutCopyText.Text = "Copy";
        };
        timer.Start();
    }

    /// <summary>Settings, this copy's own data, and (when a Debug build keeps them apart) the shared usage files.</summary>
    private void FillAboutFolders()
    {
        AboutFoldersHost.Children.Clear();
        AddAboutFolder("", "Settings", "Your agent list, presets and state.", AppIdentity.RoamingFolder);
        AddAboutFolder("", "Data", "Icons, taskbar icons and saved usage.", AppIdentity.LocalFolder);
        if (!string.Equals(AppIdentity.LocalFolder, AppIdentity.SharedLocalFolder, StringComparison.OrdinalIgnoreCase))
            AddAboutFolder("", "Usage", "Shared with the installed copy.", AppIdentity.SharedLocalFolder);
    }

    private void AddAboutFolder(string glyph, string title, string note, string path)
    {
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title });
        text.Children.Add(Note(note));
        text.Children.Add(new TextBlock
        {
            Text = path,
            IsTextSelectionEnabled = true,
            TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush")
        });
        var open = new Button { Content = "Open", VerticalAlignment = VerticalAlignment.Center, IsEnabled = Directory.Exists(path) };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(open, $"Open the {title.ToLowerInvariant()} folder");
        open.Click += (_, _) =>
        {
            try { Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = false }); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        };

        var card = new Grid
        {
            Style = (Style)AboutPage.Resources["AboutCardStyle"],
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(24) },
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        var icon = new FontIcon { Glyph = glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(text, 1);
        Grid.SetColumn(open, 2);
        card.Children.Add(icon);
        card.Children.Add(text);
        card.Children.Add(open);
        AboutFoldersHost.Children.Add(card);
    }

    // ---------- The slideshow (waifus on) ----------

    /// <summary>How long each picture is the one on top, and how long its zoom and pan runs (longer, so it's still moving as it fades out).</summary>
    private static readonly TimeSpan SlideInterval = TimeSpan.FromSeconds(9);
    private static readonly TimeSpan SlideMotion = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan SlideFade = TimeSpan.FromSeconds(1.8);
    private const double SlideOpacity = 0.15;
    /// <summary>The portraits are head and shoulders, the face centred a little above the middle, so the zoom heads there.</summary>
    private static readonly Windows.Foundation.Point SlideSubject = new(0.5, 0.42);

    private readonly Random _slideRandom = new();
    private List<string> _slideOrder = [];
    private int _slideIndex;
    private bool _slideOnA;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _slideTimer;
    /// <summary>Bumped whenever the slideshow starts or stops, so a picture that loads late doesn't show.</summary>
    private int _slideEpoch;

    private void StartSlideshow()
    {
        StopSlideshow();
        if (!_state.ShowHoverBackgrounds || AgentIcons.Portraits.Count == 0)
            return;

        // Shuffled, so each visit starts somewhere new; the whole set plays before anyone repeats.
        _slideOrder = AgentIcons.Portraits.OrderBy(_ => _slideRandom.Next()).ToList();
        _slideIndex = 0;
        AboutSlides.Visibility = Visibility.Visible;
        _ = NextSlideAsync(_slideEpoch);

        // With Windows' animations off, one still picture and no cycling.
        if (!new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
            return;
        _slideTimer = DispatcherQueue.CreateTimer();
        _slideTimer.Interval = SlideInterval;
        _slideTimer.Tick += (_, _) => _ = NextSlideAsync(_slideEpoch);
        _slideTimer.Start();
    }

    /// <summary>Stops cycling and lets go of the pictures (each is several MB decoded).</summary>
    private void StopSlideshow()
    {
        _slideEpoch++;
        _slideTimer?.Stop();
        _slideTimer = null;
        foreach (var slide in (Image[])[AboutSlideA, AboutSlideB])
        {
            slide.Opacity = 0;
            slide.Source = null;
        }
        AboutSlides.Visibility = Visibility.Collapsed;
    }

    /// <summary>Loads the next picture onto the hidden layer, then fades it in over the other with a fresh zoom and pan.</summary>
    private async Task NextSlideAsync(int epoch)
    {
        if (_slideOrder.Count == 0)
            return;
        var source = _slideOrder[_slideIndex];
        _slideIndex = (_slideIndex + 1) % _slideOrder.Count;

        var image = new BitmapImage();
        try
        {
            using var stream = AgentIcons.OpenPortrait(source);
            if (stream is null)
                return;
            await image.SetSourceAsync(stream.AsRandomAccessStream());
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return; // skip it; the next tick tries the next one
        }
        if (epoch != _slideEpoch)
            return;

        var incoming = _slideOnA ? AboutSlideB : AboutSlideA;
        var outgoing = _slideOnA ? AboutSlideA : AboutSlideB;
        _slideOnA = !_slideOnA;
        incoming.Source = image;
        incoming.Tag = image.PixelHeight > 0 ? (double)image.PixelWidth / image.PixelHeight : 16.0 / 9;
        LayoutSlide(incoming);
        incoming.RenderTransformOrigin = SlideSubject;
        // The new picture fades in on top of the old, which fades out under it (still moving).
        Canvas.SetZIndex(incoming, 1);
        Canvas.SetZIndex(outgoing, 0);

        var animate = _slideTimer is not null;
        var fade = animate ? SlideFade : TimeSpan.Zero;
        var oldSource = outgoing.Source;
        var storyboard = new Storyboard();
        storyboard.Children.Add(Fade(incoming, 0, SlideOpacity, fade));
        if (oldSource is not null)
            storyboard.Children.Add(Fade(outgoing, null, 0, fade));
        if (animate)
            AddKenBurns(storyboard, incoming);
        else
            ResetTransform(incoming);
        storyboard.Completed += (_, _) =>
        {
            // The old picture faded out long ago; let go of it, unless its layer has a new one by now.
            if (epoch == _slideEpoch && oldSource is not null && ReferenceEquals(outgoing.Source, oldSource))
                outgoing.Source = null;
        };
        storyboard.Begin();
    }

    /// <summary>An opacity fade; with no <paramref name="from"/>, from wherever it is now.</summary>
    private static DoubleAnimation Fade(UIElement target, double? from, double to, TimeSpan duration)
    {
        var fade = new DoubleAnimation { From = from, To = to, Duration = duration };
        Storyboard.SetTarget(fade, target);
        Storyboard.SetTargetProperty(fade, "Opacity");
        return fade;
    }

    /// <summary>
    /// Sizes a picture to cover the pane and slides it so her face is in the middle, as far as the
    /// picture's edges allow. A plain centred crop would cut her chin off in a wide pane.
    /// </summary>
    private void LayoutSlide(Image slide)
    {
        if (slide.Tag is not double aspect)
            return;
        var (paneWidth, paneHeight) = (AboutSlides.ActualWidth, AboutSlides.ActualHeight);
        var height = Math.Max(paneHeight, paneWidth / aspect);
        var width = height * aspect;
        slide.Width = width;
        slide.Height = height;
        slide.Margin = new Thickness(
            -Math.Clamp(width * SlideSubject.X - paneWidth / 2, 0, width - paneWidth),
            -Math.Clamp(height * SlideSubject.Y - paneHeight / 2, 0, height - paneHeight), 0, 0);
    }

    /// <summary>
    /// A slow zoom (in or out, between 108% and 135%) towards the character, with a drift from one
    /// spot to another. The drift stays small enough to keep her in view, and within what the
    /// picture has spare, so an edge never shows.
    /// </summary>
    private void AddKenBurns(Storyboard storyboard, Image target)
    {
        double Scale() => 1.08 + _slideRandom.NextDouble() * 0.27;
        var (fromScale, toScale) = (Scale(), Scale());
        if (Math.Abs(fromScale - toScale) < 0.08)
            toScale = fromScale > 1.2 ? fromScale - 0.1 : fromScale + 0.1;

        // Spare picture on each side of the pane before any zoom.
        var (paneWidth, paneHeight) = (AboutSlides.ActualWidth, AboutSlides.ActualHeight);
        var (spareLeft, spareTop) = (-target.Margin.Left, -target.Margin.Top);
        var (spareRight, spareBottom) = (target.Width - paneWidth - spareLeft, target.Height - paneHeight - spareTop);
        // Zooming by s about the origin (ox of the picture's size) adds ox·size·(s-1) of spare on the
        // near side and (1-ox)·size·(s-1) on the far side.
        double Offset(double pane, double size, double origin, double nearSpare, double farSpare, double scale)
        {
            var wanted = (_slideRandom.NextDouble() * 2 - 1) * pane * (scale - 1) * 0.6;
            return Math.Clamp(wanted, -(farSpare + (1 - origin) * size * (scale - 1)), nearSpare + origin * size * (scale - 1));
        }
        double X(double s) => Offset(paneWidth, target.Width, SlideSubject.X, spareLeft, spareRight, s);
        double Y(double s) => Offset(paneHeight, target.Height, SlideSubject.Y, spareTop, spareBottom, s);
        var (fromX, fromY, toX, toY) = (X(fromScale), Y(fromScale), X(toScale), Y(toScale));

        var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
        void Animate(string property, double from, double to)
        {
            var animation = new DoubleAnimation { From = from, To = to, Duration = SlideMotion, EasingFunction = ease };
            Storyboard.SetTarget(animation, target);
            Storyboard.SetTargetProperty(animation, $"(UIElement.RenderTransform).(CompositeTransform.{property})");
            storyboard.Children.Add(animation);
        }
        Animate("ScaleX", fromScale, toScale);
        Animate("ScaleY", fromScale, toScale);
        Animate("TranslateX", fromX, toX);
        Animate("TranslateY", fromY, toY);
    }

    private static void ResetTransform(Image target)
    {
        if (target.RenderTransform is CompositeTransform transform)
        {
            transform.ScaleX = transform.ScaleY = 1;
            transform.TranslateX = transform.TranslateY = 0;
        }
    }

    /// <summary>The zoom must stay inside the pane, so its rounded corner and the left list aren't drawn over.</summary>
    private void AboutSlides_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        AboutSlides.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        LayoutSlide(AboutSlideA);
        LayoutSlide(AboutSlideB);
    }
}
