using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace VSL.VisualStudio.Views;

/// <summary>
/// Endless decorative motion for the panel (a turning spark, a breathing icon, shimmering status text) that runs
/// only while the element is visible, so a hidden panel or a finished turn costs no frames.
/// </summary>
public static class Motion
{
    /// <summary>Seconds per full turn (0: none).</summary>
    public static readonly DependencyProperty SpinProperty = DependencyProperty.RegisterAttached("Spin", typeof(double), typeof(Motion),
        new PropertyMetadata(0.0, OnChanged));

    /// <summary>Seconds per breath in or out (0: none).</summary>
    public static readonly DependencyProperty BreatheProperty = DependencyProperty.RegisterAttached("Breathe", typeof(double), typeof(Motion),
        new PropertyMetadata(0.0, OnChanged));

    /// <summary>A light band that sweeps across the text (TextBlock only), in the accent color.</summary>
    public static readonly DependencyProperty ShimmerProperty = DependencyProperty.RegisterAttached("Shimmer", typeof(bool), typeof(Motion),
        new PropertyMetadata(false, OnChanged));

    static readonly DependencyProperty HookedProperty = DependencyProperty.RegisterAttached("Hooked", typeof(bool), typeof(Motion));

    public static double GetSpin(DependencyObject element) => (double)element.GetValue(SpinProperty);

    public static void SetSpin(DependencyObject element, double value) => element.SetValue(SpinProperty, value);

    public static double GetBreathe(DependencyObject element) => (double)element.GetValue(BreatheProperty);

    public static void SetBreathe(DependencyObject element, double value) => element.SetValue(BreatheProperty, value);

    public static bool GetShimmer(DependencyObject element) => (bool)element.GetValue(ShimmerProperty);

    public static void SetShimmer(DependencyObject element, bool value) => element.SetValue(ShimmerProperty, value);

    static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not UIElement element)
            return;
        if (!(bool)element.GetValue(HookedProperty))
        {
            element.SetValue(HookedProperty, true);
            element.IsVisibleChanged += (_, _) => Update(element);
        }
        Update(element);
    }

    static void Update(UIElement element)
    {
        var running = element.IsVisible;
        var spin = GetSpin(element);
        var breathe = GetBreathe(element);
        if (spin > 0 || breathe > 0)
        {
            var (scale, rotate) = Transforms(element);
            rotate.BeginAnimation(RotateTransform.AngleProperty, running && spin > 0
                ? new DoubleAnimation(0, 360, TimeSpan.FromSeconds(spin)) { RepeatBehavior = RepeatBehavior.Forever }
                : null);
            var breath = running && breathe > 0
                ? new DoubleAnimation(0.88, 1.08, TimeSpan.FromSeconds(breathe))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
                }
                : null;
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, breath);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, breath);
        }
        if (GetShimmer(element) && element is TextBlock text)
        {
            if (text.Foreground is not LinearGradientBrush { RelativeTransform: TranslateTransform } brush)
                text.Foreground = brush = ShimmerBrush();
            brush.RelativeTransform.BeginAnimation(TranslateTransform.XProperty, running
                ? new DoubleAnimation(-1, 1, TimeSpan.FromSeconds(1.8)) { RepeatBehavior = RepeatBehavior.Forever }
                : null);
        }
    }

    static (ScaleTransform Scale, RotateTransform Rotate) Transforms(UIElement element)
    {
        if (element.RenderTransform is TransformGroup { Children: [ScaleTransform s, RotateTransform r] })
            return (s, r);
        var scale = new ScaleTransform();
        var rotate = new RotateTransform();
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = new TransformGroup { Children = { scale, rotate } };
        return (scale, rotate);
    }

    static LinearGradientBrush ShimmerBrush()
    {
        var accent = (Color)ColorConverter.ConvertFromString("#D97757");
        var light = (Color)ColorConverter.ConvertFromString("#FFD9C9");
        return new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 0),
            RelativeTransform = new TranslateTransform(-1, 0),
            GradientStops =
            {
                new GradientStop(accent, 0),
                new GradientStop(accent, 0.35),
                new GradientStop(light, 0.5),
                new GradientStop(accent, 0.65),
                new GradientStop(accent, 1),
            },
        };
    }
}
