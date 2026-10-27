using System.Windows;
using System.Windows.Media;

namespace EdifierManager;

public sealed class HeadsetArt : FrameworkElement
{
    public static readonly DependencyProperty TwsProperty = DependencyProperty.Register(nameof(Tws), typeof(bool), typeof(HeadsetArt), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public bool Tws { get => (bool)GetValue(TwsProperty); set => SetValue(TwsProperty, value); }
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var accent = (Brush)FindResource("AccentBrush"); var fill = (Brush)FindResource("AccentSoftBrush");
        var pen = new Pen(accent, 3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        dc.PushTransform(new ScaleTransform(ActualWidth / 90, ActualHeight / 76));
        if (!Tws)
        {
            var arc = Geometry.Parse("M 15,44 L 15,34 C 15,0 75,0 75,34 L 75,44"); dc.DrawGeometry(null, pen, arc);
            dc.DrawRoundedRectangle(fill, pen, new Rect(9,34,17,29),6,6);
            dc.DrawRoundedRectangle(fill, pen, new Rect(64,34,17,29),6,6);
            dc.DrawLine(pen, new(29,16), new(61,16));
        }
        else
        {
            dc.PushTransform(new RotateTransform(-12,25,35));
            dc.DrawRoundedRectangle(fill, pen, new Rect(22,27,10,36),5,5); dc.DrawEllipse(fill,pen,new Point(24,23),13,12);
            dc.DrawEllipse(accent,null,new Point(18,22),3,5); dc.Pop();
            dc.PushTransform(new RotateTransform(12,65,35));
            dc.DrawRoundedRectangle(fill,pen,new Rect(58,27,10,36),5,5); dc.DrawEllipse(fill,pen,new Point(66,23),13,12);
            dc.DrawEllipse(accent,null,new Point(72,22),3,5); dc.Pop();
        }
        dc.Pop();
    }
}
