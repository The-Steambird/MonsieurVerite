using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MonsieurVerite.Themes;

/// <summary>
/// The progress-bar glide and the pills' fade-in play only when their own row changes, never when
/// the queue recycles a container for a different row while scrolling.
/// </summary>
public static class Motion
{
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.RegisterAttached(
            "Progress", typeof(double), typeof(Motion),
            new PropertyMetadata(0.0, OnProgressChanged));

    /// <summary>The row state whose change fades the element in.</summary>
    public static readonly DependencyProperty FadeOnProperty =
        DependencyProperty.RegisterAttached(
            "FadeOn", typeof(object), typeof(Motion),
            new PropertyMetadata(null, OnFadeOnChanged));

    private static readonly DependencyProperty ShownRowProperty =
        DependencyProperty.RegisterAttached("ShownRow", typeof(object), typeof(Motion));

    private static readonly Duration GlideDuration = new(TimeSpan.FromMilliseconds(220));

    private static readonly Duration FadeDuration = new(TimeSpan.FromMilliseconds(250));

    public static double GetProgress(DependencyObject element) =>
        (double)element.GetValue(ProgressProperty);

    public static void SetProgress(DependencyObject element, double value) =>
        element.SetValue(ProgressProperty, value);

    public static object? GetFadeOn(DependencyObject element) => element.GetValue(FadeOnProperty);

    public static void SetFadeOn(DependencyObject element, object? value) =>
        element.SetValue(FadeOnProperty, value);

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RangeBase bar)
        {
            return;
        }

        var target = (double)e.NewValue;
        if (!ShowsSameRow(bar) || target <= 0 || target < bar.Value)
        {
            bar.BeginAnimation(RangeBase.ValueProperty, null);
            bar.Value = target;
            return;
        }

        bar.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(target, GlideDuration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    private static void OnFadeOnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        if (!ShowsSameRow(element))
        {
            element.BeginAnimation(UIElement.OpacityProperty, null);
            return;
        }

        element.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, FadeDuration));
    }

    /// <summary>Whether the element still shows the row it showed at its last change.</summary>
    private static bool ShowsSameRow(FrameworkElement element)
    {
        if (element.ReadLocalValue(ShownRowProperty) == DependencyProperty.UnsetValue)
        {
            element.DataContextChanged += OnDataContextChanged;
        }

        var same = ReferenceEquals(element.GetValue(ShownRowProperty), element.DataContext);
        element.SetValue(ShownRowProperty, element.DataContext);
        return same;
    }

    // A container handed a row in the same state as its last one sees no change, and without this
    // the new row's first real change would count as a different row. The new row is recorded after
    // the bindings have settled, because WPF does not order this event against their updates.
    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var element = (FrameworkElement)sender;
        _ = element.Dispatcher.InvokeAsync(
            () => element.SetValue(ShownRowProperty, element.DataContext),
            DispatcherPriority.Background);
    }
}
