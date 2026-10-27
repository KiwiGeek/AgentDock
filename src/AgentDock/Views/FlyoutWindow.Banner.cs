using AgentDock.Interop;
using AgentDock.Models;
using AgentDock.Services.Agents;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.System;

namespace AgentDock.Views;

/// <summary>
/// The banner down the list's left edge: its gradient, the agent's colour, and the hearts.
/// </summary>
public sealed partial class FlyoutWindow
{
    private const double BannerInsetDip = 14;
    /// <summary>Matches the fill's Opacity in the XAML.</summary>
    private const double BannerFillOpacity = 0.7;

    private void Banner_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutBanner();

    /// <summary>The heart goes with the waifu pictures: shown when they're on.</summary>
    private void UpdateBannerHeart()
    {
        BannerHeart.Visibility = _state.WaifuModeEnabled ? Visibility.Visible : Visibility.Collapsed;
        LayoutBanner();
        SyncHearts();
    }

    // Banner colours: the accent by default, the highlighted agent's colour while one is.

    private readonly Dictionary<string, Windows.UI.Color?> _themeColors = new(StringComparer.OrdinalIgnoreCase);
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _bannerResetTimer;
    private Storyboard? _bannerColorFade;
    /// <summary>The picture whose colour the banner should show; a late result for another one is dropped.</summary>
    private string? _bannerColorSource;

    /// <summary>
    /// The agent's picture when it has one, otherwise its logo. The picture still supplies its curated
    /// hover colour when backgrounds are hidden, so muted monochrome logos do not take over the banner.
    /// </summary>
    private string? ThemeSourceFor(AgentItem item) =>
        item.BackgroundSource is { } background ? background : item.IconSource;

    /// <summary>
    /// The agent's "color" setting, else the colour of its picture (or logo when no picture exists):
    /// hand-picked for built-in pictures, worked out from the image otherwise.
    /// </summary>
    private async void ColorBannerFor(AgentItem? item)
    {
        _bannerResetTimer?.Stop();
        if (item is not null && ThemeColors.Parse(item.Definition.Color) is { } configured)
        {
            _bannerColorSource = item.Definition.Id;
            SetBannerColors(configured);
            return;
        }

        // Do not make this depend on the image-source lookup: the built-in backgrounds have curated
        // colours, and that identity remains available even while the pictures themselves are hidden.
        if (item is not null)
        {
            var knownId = item.Definition.Catalog ?? item.Definition.Id;
            if (ThemeColors.ForBundledBackgroundName(knownId) is { } knownColor)
            {
                _bannerColorSource = knownId;
                SetBannerColors(knownColor);
                return;
            }
        }

        var source = item is not null ? ThemeSourceFor(item) : null;
        _bannerColorSource = source;
        if (source is null)
        {
            SetBannerColors(null);
            return;
        }

        if (AgentIcons.IsBundledBackground(source) && ThemeColors.ForBundledBackground(source) is { } picked)
        {
            SetBannerColors(picked);
            return;
        }

        if (!_themeColors.TryGetValue(source, out var color))
        {
            color = await LoadThemeColorAsync(source);
            _themeColors[source] = color;
            if (!string.Equals(_bannerColorSource, source, StringComparison.OrdinalIgnoreCase))
                return;
        }
        SetBannerColors(color);
    }

    private static async Task<Windows.UI.Color?> LoadThemeColorAsync(string source)
    {
        try
        {
            using var stream = AgentIcons.IsBundledBackground(source)
                ? AgentIcons.OpenBundledBackground(source)?.AsRandomAccessStream()
                : await ShellIcons.OpenAsync(source);
            return stream is null ? null : await ThemeColors.FromStreamAsync(stream);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null; // the accent colour, then
        }
    }

    /// <summary>
    /// Back to the accent colour shortly after the pointer leaves, unless it lands on another agent (or
    /// the drawer) first, so moving between rows doesn't flash the accent.
    /// </summary>
    private void ScheduleBannerReset()
    {
        // A pop-up (a menu, a confirmation) took the pointer; the drawer and the agent's colour stay.
        if (_popupsOpen > 0)
            return;
        if (_bannerResetTimer is null)
        {
            _bannerResetTimer = DispatcherQueue.CreateTimer();
            _bannerResetTimer.Interval = TimeSpan.FromMilliseconds(250);
            _bannerResetTimer.IsRepeating = false;
            _bannerResetTimer.Tick += (_, _) =>
            {
                _bannerColorSource = null;
                SetBannerColors(null);
            };
        }
        _bannerResetTimer.Start();
    }

    /// <summary>
    /// Fades the banner to an agent's colour, or to the accent colours for the current theme when
    /// <paramref name="agentColor"/> is null.
    /// </summary>
    private void SetBannerColors(Windows.UI.Color? agentColor, bool animate = true)
    {
        Windows.UI.Color bottom, top;
        if (agentColor is { } color)
        {
            var tamed = ThemeColors.Tame(color);
            (bottom, top) = (ThemeColors.Shade(tamed, 0.15), ThemeColors.Shade(tamed, -0.2));
        }
        else
        {
            var light = Banner.ActualTheme == ElementTheme.Light;
            bottom = AccentColor(light ? "SystemAccentColorDark2" : "SystemAccentColorLight2");
            top = AccentColor(light ? "SystemAccentColorDark1" : "SystemAccentColorLight1");
        }
        // The fill is translucent, so pick the text colour against what shows: the fill over the flyout.
        var behind = Banner.ActualTheme == ElementTheme.Light
            ? Windows.UI.Color.FromArgb(255, 0xF3, 0xF3, 0xF3)
            : Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20);
        var text = ThemeColors.TextOn(ThemeColors.Over(bottom, behind, BannerFillOpacity));

        _bannerColorFade?.Stop();
        if (!animate || !AppWindow.IsVisible || !new Windows.UI.ViewManagement.UISettings().AnimationsEnabled)
        {
            BannerStopBottom.Color = bottom;
            BannerStopTop.Color = top;
            BannerTextBrush.Color = text;
            return;
        }

        var storyboard = new Storyboard();
        Add(BannerStopBottom, bottom);
        Add(BannerStopTop, top);
        Add(BannerTextBrush, text);
        _bannerColorFade = storyboard;
        storyboard.Begin();

        void Add(DependencyObject target, Windows.UI.Color to)
        {
            // Gradient stops and brushes aren't composition properties, so these run on the UI thread.
            var fade = new ColorAnimation
            {
                To = to, Duration = new Duration(TimeSpan.FromMilliseconds(450)), EnableDependentAnimation = true,
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(fade, target);
            Storyboard.SetTargetProperty(fade, "Color");
            storyboard.Children.Add(fade);
        }
    }

    private static Windows.UI.Color AccentColor(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Windows.UI.Color color
            ? color
            : Windows.UI.Color.FromArgb(255, 0, 120, 212);

    // Hearts: the banner's heart beats, and small hearts float up from it.

    private Storyboard? _heartbeat;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _floatTimer;
    private readonly Random _random = new();
    private const double HeartbeatSeconds = 1.6;

    /// <summary>Runs the hearts only while they can be seen.</summary>
    private void SyncHearts()
    {
        var run = AppWindow.IsVisible && BannerHeart.Visibility == Visibility.Visible
                  && new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
        if (run)
            StartHearts();
        else
            StopHearts();
    }

    private void StartHearts()
    {
        if (_heartbeat is null)
        {
            _heartbeat = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            foreach (var property in new[] { "ScaleX", "ScaleY" })
            {
                // Two quick beats, then a rest.
                var beat = new DoubleAnimationUsingKeyFrames();
                foreach (var (seconds, scale) in new[] { (0.0, 1.0), (0.12, 1.3), (0.24, 1.0), (0.36, 1.2), (0.52, 1.0), (HeartbeatSeconds, 1.0) })
                    beat.KeyFrames.Add(new EasingDoubleKeyFrame
                    {
                        KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)), Value = scale,
                        EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                    });
                Storyboard.SetTarget(beat, BannerHeartTransform);
                Storyboard.SetTargetProperty(beat, property);
                _heartbeat.Children.Add(beat);
            }
        }
        _heartbeat.Begin();

        if (_floatTimer is null)
        {
            // One-shot and rescheduled each time, so the gaps between hearts vary (owner's request, 2026-10-19).
            _floatTimer = DispatcherQueue.CreateTimer();
            _floatTimer.IsRepeating = false;
            _floatTimer.Tick += (_, _) =>
            {
                ReleaseHearts();
                ScheduleNextHeart();
            };
        }
        SeedHearts();
        ScheduleNextHeart();
    }

    /// <summary>
    /// Opening the flyout starts with a few hearts already on their way up, at different heights,
    /// instead of an empty banner that fills over several seconds (owner's request, 2026-10-19).
    /// </summary>
    private void SeedHearts()
    {
        if (BannerCanvas.Children.OfType<TextBlock>().Any(t => t.Tag is "float"))
            return;
        // This runs just after the window is placed, before layout has caught up with its new height,
        // so the big heart may still be where the last show left it; seeds from there sat on the name.
        Banner.UpdateLayout();
        LayoutBanner();
        // Spread over the trip, one per slice, so they don't bunch up.
        var count = 3 + _random.Next(3);
        for (var i = 0; i < count; i++)
            FloatHeart(TimeSpan.Zero, (i + 0.15 + _random.NextDouble() * 0.7) / count * 0.9);
    }

    private void ScheduleNextHeart()
    {
        if (_floatTimer is null)
            return;
        _floatTimer.Interval = TimeSpan.FromSeconds(0.4 + _random.NextDouble() * 2.2);
        _floatTimer.Start();
    }

    /// <summary>Usually one heart, now and then a little burst of two or three.</summary>
    private void ReleaseHearts()
    {
        var count = _random.NextDouble() switch { < 0.7 => 1, < 0.92 => 2, _ => 3 };
        for (var i = 0; i < count; i++)
            FloatHeart(TimeSpan.FromSeconds(i * (0.15 + _random.NextDouble() * 0.35)));
    }

    private void StopHearts()
    {
        _heartbeat?.Stop();
        _floatTimer?.Stop();
        foreach (var floater in BannerCanvas.Children.OfType<TextBlock>().Where(t => t.Tag is "float").ToList())
            BannerCanvas.Children.Remove(floater);
    }

    /// <summary>
    /// What floats up, with weights: mostly hearts, now and then a kiss, a blossom or sparkles
    /// (owner's request, 2026-10-19). Faces were left out: at 7–14 DIP they read as clutter.
    /// </summary>
    private static readonly (string Text, bool Sideways, int Weight)[] Floaters =
    [
        ("❤️", true, 4), ("💕", true, 3), ("💖", true, 3), ("💗", true, 3), ("💞", true, 2), ("🩷", true, 3),
        ("💋", false, 1), ("🌸", false, 1), ("✨", false, 1)
    ];

    private static readonly int FloaterWeights = Floaters.Sum(f => f.Weight);

    private (string Text, bool Sideways) PickFloater()
    {
        var roll = _random.Next(FloaterWeights);
        foreach (var (text, sideways, weight) in Floaters)
        {
            if (roll < weight)
                return (text, sideways);
            roll -= weight;
        }
        return (BannerHeart.Text, true);
    }

    /// <summary>At most this many floating hearts at once, so a burst on a tall flyout doesn't pile up.</summary>
    private const int MaxFloatingHearts = 8;

    /// <summary>
    /// A small sideways heart that drifts from the big one all the way to the top of the banner, and
    /// fades out there. Each is a little different (size, tilt, wobble, speed, brightness), and they
    /// rise at a steady speed, so a trip takes several seconds and a few are usually on the way up.
    /// <paramref name="progress"/> (0–1) starts it that far through its trip.
    /// </summary>
    private void FloatHeart(TimeSpan delay, double progress = 0)
    {
        if (BannerCanvas.Children.OfType<TextBlock>().Count(t => t.Tag is "float") >= MaxFloatingHearts)
            return;
        var heartTop = Canvas.GetTop(BannerHeart) + BannerHeart.DesiredSize.Height / 2 - BannerHeart.DesiredSize.Width / 2;
        var room = heartTop - 4;
        if (room < 30)
            return;

        var (text, sideways) = PickFloater();
        var tilt = (_random.NextDouble() - 0.5) * 36; // ±18° off sideways
        // Hearts lie sideways like the banner's; a kiss, a blossom or sparkles look odd that way, so stay nearly upright.
        var angle = sideways ? -90 + tilt : tilt * 0.5;
        var transform = new Microsoft.UI.Xaml.Media.CompositeTransform { Rotation = angle };
        var heart = new TextBlock
        {
            Text = text, FontSize = 7 + _random.NextDouble() * 7, Tag = "float", Opacity = 0,
            IsHitTestVisible = false, RenderTransform = transform
        };
        heart.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = heart.DesiredSize;
        transform.CenterX = size.Width / 2;
        transform.CenterY = size.Height / 2;
        // Turned sideways, its height is what takes up the banner's width; keep the wobble inside it.
        var slack = Math.Max(0, (BannerWidthDip - (sideways ? size.Height : size.Width)) / 2 - 1);
        var start = (_random.NextDouble() - 0.5) * slack;
        Canvas.SetLeft(heart, BannerWidthDip / 2 - size.Width / 2 + start);
        Canvas.SetTop(heart, heartTop - size.Height / 2);
        BannerCanvas.Children.Add(heart);

        var distance = room - size.Width / 2;
        var duration = TimeSpan.FromSeconds(distance / (55 + _random.NextDouble() * 55)); // 55–110 DIP/s
        var storyboard = new Storyboard { BeginTime = delay };

        var rise = new DoubleAnimation
        {
            To = -distance, Duration = duration,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseIn } // drifts off slowly, then floats
        };
        Storyboard.SetTarget(rise, transform);
        Storyboard.SetTargetProperty(rise, "TranslateY");

        var wobble = Math.Min(slack - Math.Abs(start), 1.5 + _random.NextDouble() * 3.5);
        var sway = new DoubleAnimation
        {
            From = -wobble, To = wobble, Duration = TimeSpan.FromSeconds(0.5 + _random.NextDouble() * 0.9),
            AutoReverse = true, RepeatBehavior = new RepeatBehavior(duration),
            BeginTime = TimeSpan.FromSeconds(_random.NextDouble() * 0.4),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(sway, transform);
        Storyboard.SetTargetProperty(sway, "TranslateX");

        var rock = new DoubleAnimation
        {
            From = angle - 8, To = angle + 8, Duration = TimeSpan.FromSeconds(0.7 + _random.NextDouble() * 0.8),
            AutoReverse = true, RepeatBehavior = new RepeatBehavior(duration),
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        Storyboard.SetTarget(rock, transform);
        Storyboard.SetTargetProperty(rock, "Rotation");

        var peak = 0.55 + _random.NextDouble() * 0.4;
        var total = duration.TotalSeconds;
        var fade = new DoubleAnimationUsingKeyFrames();
        foreach (var (seconds, opacity) in new[] { (0.0, 0.0), (0.4, peak), (total * 0.75, peak * 0.85), (total, 0.0) })
            fade.KeyFrames.Add(new LinearDoubleKeyFrame { KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds)), Value = opacity });
        Storyboard.SetTarget(fade, heart);
        Storyboard.SetTargetProperty(fade, "Opacity");

        storyboard.Children.Add(rise);
        storyboard.Children.Add(sway);
        storyboard.Children.Add(rock);
        storyboard.Children.Add(fade);
        storyboard.Completed += (_, _) => BannerCanvas.Children.Remove(heart);
        storyboard.Begin();
        if (progress > 0)
            storyboard.Seek(TimeSpan.FromSeconds(total * progress));
    }

    /// <summary>
    /// Turns the banner text to read bottom to top, ending <see cref="BannerInsetDip"/> above the
    /// bottom edge, with the heart upright above it.
    /// </summary>
    private void LayoutBanner()
    {
        var height = Banner.ActualHeight;
        if (height <= 0)
            return;

        var unbounded = new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity);
        BannerText.Measure(unbounded);
        var length = BannerText.DesiredSize.Width;
        var thickness = BannerText.DesiredSize.Height;
        var bottom = height - BannerInsetDip;

        // Rotating -90° about the top-left corner puts the text in x 0..thickness, y -length..0.
        BannerText.RenderTransform = new Microsoft.UI.Xaml.Media.CompositeTransform
        {
            Rotation = -90,
            TranslateX = Math.Round((BannerWidthDip - thickness) / 2),
            TranslateY = Math.Round(bottom)
        };

        // The heart turns about its centre, so its (unturned) width is how much banner it takes up.
        BannerHeart.Measure(unbounded);
        var heart = BannerHeart.DesiredSize;
        BannerHeartTransform.CenterX = heart.Width / 2;
        BannerHeartTransform.CenterY = heart.Height / 2;
        var centreY = bottom - length - 8 - heart.Width / 2;
        Canvas.SetLeft(BannerHeart, Math.Round(BannerWidthDip / 2 - heart.Width / 2));
        Canvas.SetTop(BannerHeart, Math.Round(centreY - heart.Height / 2));
    }
}
