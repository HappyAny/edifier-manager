using System.Windows;
using System.Windows.Media;

namespace EdifierManager;

public sealed class BatteryGauge : FrameworkElement
{
    public static readonly DependencyProperty PercentProperty = DependencyProperty.Register(nameof(Percent), typeof(double), typeof(BatteryGauge), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Percent { get => (double)GetValue(PercentProperty); set => SetValue(PercentProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var outline = (Brush)FindResource("SecondaryBrush");
        dc.DrawRoundedRectangle(null, new Pen(outline, 1), new Rect(0.5, 1.5, 24, 12), 2, 2);
        dc.DrawRoundedRectangle(outline, null, new Rect(25.5, 5, 2, 5), 1, 1);
        if (double.IsFinite(Percent))
        {
            double value = Math.Clamp(Percent, 0, 100);
            var color = SystemParameters.HighContrast ? SystemColors.HighlightColor : value >= 50 ? Color.FromRgb(25, 164, 103) : value >= 25 ? Color.FromRgb(213, 155, 20) : Color.FromRgb(217, 63, 59);
            if (value > 0) dc.DrawRoundedRectangle(new SolidColorBrush(color), null, new Rect(3, 4, 19 * value / 100, 7), 0.8, 0.8);
        }
        else dc.DrawLine(new Pen(outline, 1.5), new Point(9, 7.5), new Point(16, 7.5));
    }
}
