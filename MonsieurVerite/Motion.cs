using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Animation;

namespace MonsieurVerite;

public static class Motion
{
    public static readonly DependencyProperty ProgressProperty =
        DependencyProperty.RegisterAttached(
            "Progress", typeof(double), typeof(Motion),
            new PropertyMetadata(0.0, OnProgressChanged));

    private static readonly Duration GlideDuration = new(TimeSpan.FromMilliseconds(220));

    public static double GetProgress(DependencyObject element)
    {
        ArgumentNullException.ThrowIfNull(element);
        return (double)element.GetValue(ProgressProperty);
    }

    public static void SetProgress(DependencyObject element, double value)
    {
        ArgumentNullException.ThrowIfNull(element);
        element.SetValue(ProgressProperty, value);
    }

    private static void OnProgressChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not RangeBase bar)
        {
            return;
        }

        var target = (double)e.NewValue;
        if (target <= 0 || target < bar.Value)
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
}
