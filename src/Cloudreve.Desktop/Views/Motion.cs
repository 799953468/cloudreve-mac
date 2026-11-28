using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;

namespace Cloudreve.Desktop;

/// <summary>Short entrance animations for content that appears from code.</summary>
internal static class Motion
{
    private static readonly Easing EaseOut = new CubicEaseOut();

    /// <summary>
    /// Turns on the window's color transitions (styles under <c>Window.motion</c>) once its first frame has
    /// settled, so controls don't visibly fade from theme defaults to their styled colors when it opens.
    /// </summary>
    internal static void EnableTransitions(Window window)
    {
        window.Opened += (_, _) =>
        {
            DispatcherTimer.RunOnce(() => { window.Classes.Add("motion"); }, TimeSpan.FromMilliseconds(250));
        };
    }

    /// <summary>Fades a control in while it moves from <paramref name="offsetY"/> to its resting position.</summary>
    internal static void Enter(Visual visual, double offsetY = 6, double durationMs = 220, double delayMs = 0)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(durationMs),
            Delay = TimeSpan.FromMilliseconds(delayMs),
            Easing = EaseOut,
            FillMode = FillMode.Backward,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0d), new Setter(TranslateTransform.YProperty, offsetY) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1d), new Setter(TranslateTransform.YProperty, 0d) } }
            }
        };
        _ = animation.RunAsync(visual);
    }

    /// <summary>Fades and scales a window's content in, like a menu bar popover opening.</summary>
    internal static void Pop(Visual visual)
    {
        var animation = new Animation
        {
            Duration = TimeSpan.FromMilliseconds(180),
            Easing = EaseOut,
            Children =
            {
                new KeyFrame { Cue = new Cue(0), Setters = { new Setter(Visual.OpacityProperty, 0d), new Setter(ScaleTransform.ScaleXProperty, 0.97), new Setter(ScaleTransform.ScaleYProperty, 0.97) } },
                new KeyFrame { Cue = new Cue(1), Setters = { new Setter(Visual.OpacityProperty, 1d), new Setter(ScaleTransform.ScaleXProperty, 1d), new Setter(ScaleTransform.ScaleYProperty, 1d) } }
            }
        };
        _ = animation.RunAsync(visual);
    }
}
