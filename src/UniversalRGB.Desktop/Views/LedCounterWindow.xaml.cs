using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using UniversalRGB.Core.Devices;
using UniversalRGB.Desktop.Services;
using UniversalRGB.Desktop.ViewModels;

namespace UniversalRGB.Desktop.Views;

public partial class LedCounterWindow : Window
{
    private readonly LedCounterViewModel _vm;
    private bool _finished;

    private LedCounterWindow(LedCounterViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;
        var main = Application.Current?.MainWindow;
        if (main is { IsVisible: true } && !ReferenceEquals(main, this))
        {
            Owner = main;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    /// <summary>Opens the LED counter for one addressable header and waits until it is saved or cancelled.</summary>
    public static void Run(ControlCenter center, RgbDevice device, RgbZone zone) =>
        new LedCounterWindow(new LedCounterViewModel(center, device, zone)).ShowDialog();

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!await _vm.StartAsync())
        {
            _finished = true;
            Close();
        }
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (_finished || _vm.IsBusy)
        {
            return;
        }

        await _vm.FinishAsync(save: true);
        _finished = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e) => Close();

    // Closing any other way (Cancel, Esc, Alt+F4) puts the header back the way it was.
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_finished)
        {
            return;
        }

        e.Cancel = true;
        if (_vm.IsBusy)
        {
            return;
        }

        await _vm.FinishAsync(save: false);
        _finished = true;
        Close();
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.Thumb
            and not System.Windows.Controls.Primitives.ButtonBase)
        {
            DragMove();
        }
    }
}
