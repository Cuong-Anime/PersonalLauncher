using System.Windows;
using System.Windows.Media;

namespace PersonalLauncher.App.Controls;

/// <summary>
/// True rounded-rectangle / circular clipping for WPF images, grids, icons, and live video surfaces.
/// Unlike Border.ClipToBounds (which only clips to a sharp rectangle), this sets an exact
/// RectangleGeometry or EllipseGeometry Clip whenever element dimensions change.
/// </summary>
public static class ClipHelper
{
    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.RegisterAttached(
        "CornerRadius",
        typeof(double),
        typeof(ClipHelper),
        new PropertyMetadata(0.0, OnClipPropertyChanged));

    public static readonly DependencyProperty IsCircularProperty = DependencyProperty.RegisterAttached(
        "IsCircular",
        typeof(bool),
        typeof(ClipHelper),
        new PropertyMetadata(false, OnClipPropertyChanged));

    public static double GetCornerRadius(DependencyObject obj) => (double)obj.GetValue(CornerRadiusProperty);
    public static void SetCornerRadius(DependencyObject obj, double value) => obj.SetValue(CornerRadiusProperty, value);

    public static bool GetIsCircular(DependencyObject obj) => (bool)obj.GetValue(IsCircularProperty);
    public static void SetIsCircular(DependencyObject obj, bool value) => obj.SetValue(IsCircularProperty, value);

    private static void OnClipPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe)
            return;

        fe.Loaded -= OnElementLayoutUpdated;
        fe.SizeChanged -= OnElementSizeChanged;

        bool active = GetIsCircular(fe) || GetCornerRadius(fe) > 0;
        if (active)
        {
            fe.Loaded += OnElementLayoutUpdated;
            fe.SizeChanged += OnElementSizeChanged;
            ApplyClip(fe);
        }
        else
        {
            fe.Clip = null;
        }
    }

    private static void OnElementLayoutUpdated(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            ApplyClip(fe);
    }

    private static void OnElementSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is FrameworkElement fe)
            ApplyClip(fe);
    }

    private static void ApplyClip(FrameworkElement fe)
    {
        double w = fe.ActualWidth;
        double h = fe.ActualHeight;
        if (w <= 0 || h <= 0)
            return;

        if (GetIsCircular(fe))
        {
            double rx = w / 2.0;
            double ry = h / 2.0;
            var circle = new EllipseGeometry(new Point(rx, ry), rx, ry);
            circle.Freeze();
            fe.Clip = circle;
            return;
        }

        double r = GetCornerRadius(fe);
        if (r > 0)
        {
            var rect = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
            rect.Freeze();
            fe.Clip = rect;
        }
    }
}
