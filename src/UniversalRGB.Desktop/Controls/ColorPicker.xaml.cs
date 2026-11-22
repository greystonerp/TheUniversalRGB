using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UniversalRGB.Core.Devices;

namespace UniversalRGB.Desktop.Controls;

/// <summary>HSV color picker: saturation/value square, hue slider, HEX/RGB entry and presets.</summary>
public partial class ColorPicker : UserControl
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
        nameof(Color), typeof(Color), typeof(ColorPicker),
        new FrameworkPropertyMetadata(Colors.Cyan, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnColorPropertyChanged));

    private static readonly string[] PresetHex =
    [
        "#22D3EE", "#3B82F6", "#34D399", "#FACC15", "#FB923C", "#F43F5E", "#FFF4E6", "#FFFFFF",
    ];

    private double _hue;
    private double _saturation;
    private double _value;
    private bool _updating;
    private bool _dragging;

    public ColorPicker()
    {
        InitializeComponent();
        Presets.ItemsSource = PresetHex.Select(h => (Brush)new BrushConverter().ConvertFromString(h)!).ToList();
        Loaded += (_, _) => SyncFromColor(Color);
    }

    public Color Color
    {
        get => (Color)GetValue(ColorProperty);
        set => SetValue(ColorProperty, value);
    }

    private static void OnColorPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var picker = (ColorPicker)d;
        if (!picker._updating)
        {
            picker.SyncFromColor((Color)e.NewValue);
        }
    }

    private void SyncFromColor(Color color)
    {
        var (h, s, v) = new RgbColor(color.R, color.G, color.B).ToHsv();

        // Keep the current hue for greys/black so the square does not jump to red.
        if (s > 0.001 && v > 0.001)
        {
            _hue = h;
        }

        _saturation = s;
        _value = v;
        _updating = true;
        try
        {
            HueSlider.Value = _hue * 360;
            UpdateVisuals(color);
        }
        finally
        {
            _updating = false;
        }
    }

    private void Commit()
    {
        var rgb = RgbColor.FromHsv(_hue, _saturation, _value);
        var color = Color.FromRgb(rgb.R, rgb.G, rgb.B);
        _updating = true;
        try
        {
            Color = color;
            UpdateVisuals(color);
        }
        finally
        {
            _updating = false;
        }
    }

    private void UpdateVisuals(Color color)
    {
        var hueColor = RgbColor.FromHsv(_hue, 1, 1);
        HueLayer.Background = new SolidColorBrush(Color.FromRgb(hueColor.R, hueColor.G, hueColor.B));
        Preview.Background = new SolidColorBrush(color);
        HexBox.Text = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        RBox.Text = color.R.ToString(CultureInfo.InvariantCulture);
        GBox.Text = color.G.ToString(CultureInfo.InvariantCulture);
        BBox.Text = color.B.ToString(CultureInfo.InvariantCulture);
        PlaceThumb();
    }

    private void PlaceThumb()
    {
        var w = SvArea.ActualWidth;
        var h = SvArea.ActualHeight;
        if (w <= 0 || h <= 0)
        {
            return;
        }

        Canvas.SetLeft(SvThumb, (_saturation * w) - (SvThumb.Width / 2));
        Canvas.SetTop(SvThumb, ((1 - _value) * h) - (SvThumb.Height / 2));
    }

    private void OnSvSizeChanged(object sender, SizeChangedEventArgs e) => PlaceThumb();

    private void OnSvDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        SvArea.CaptureMouse();
        PickAt(e.GetPosition(SvArea));
    }

    private void OnSvMove(object sender, MouseEventArgs e)
    {
        if (_dragging && e.LeftButton == MouseButtonState.Pressed)
        {
            PickAt(e.GetPosition(SvArea));
        }
    }

    private void OnSvUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        SvArea.ReleaseMouseCapture();
    }

    private void PickAt(Point p)
    {
        _saturation = Math.Clamp(p.X / Math.Max(1, SvArea.ActualWidth), 0, 1);
        _value = Math.Clamp(1 - (p.Y / Math.Max(1, SvArea.ActualHeight)), 0, 1);
        Commit();
    }

    private void OnHueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updating)
        {
            return;
        }

        _hue = e.NewValue / 360.0;
        if (_saturation < 0.05)
        {
            _saturation = 1;
        }

        if (_value < 0.05)
        {
            _value = 1;
        }

        Commit();
    }

    private void OnHexKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnHexCommit(sender, null!);
        }
    }

    private void OnHexCommit(object sender, KeyboardFocusChangedEventArgs? e)
    {
        if (RgbColor.TryParseHex(HexBox.Text, out var rgb))
        {
            Color = Color.FromRgb(rgb.R, rgb.G, rgb.B);
        }
        else
        {
            HexBox.Text = $"#{Color.R:X2}{Color.G:X2}{Color.B:X2}";
        }
    }

    private void OnRgbKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            OnRgbCommit(sender, null!);
        }
    }

    private void OnRgbCommit(object sender, KeyboardFocusChangedEventArgs? e)
    {
        static byte Parse(string text, byte fallback) =>
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? (byte)Math.Clamp(v, 0, 255) : fallback;

        var current = Color;
        Color = Color.FromRgb(Parse(RBox.Text, current.R), Parse(GBox.Text, current.G), Parse(BBox.Text, current.B));
        SyncFromColor(Color);
    }

    private void OnPreset(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: SolidColorBrush brush })
        {
            Color = brush.Color;
        }
    }
}
