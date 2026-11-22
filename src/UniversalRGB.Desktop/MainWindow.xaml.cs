using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using UniversalRGB.Core.Storage;
using UniversalRGB.Desktop.Services;
using UniversalRGB.Desktop.ViewModels;

namespace UniversalRGB.Desktop;

public partial class MainWindow : Window
{
    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;
    private const int DBT_DEVICEARRIVAL = 0x8000;
    private const int DBT_DEVICEREMOVECOMPLETE = 0x8004;

    private readonly MainViewModel _viewModel;
    private readonly AppSettings _settings;
    private readonly DispatcherTimer _deviceChangeDebounce;

    public MainWindow(MainViewModel viewModel, AppSettings settings)
    {
        InitializeComponent();
        _viewModel = viewModel;
        _settings = settings;
        DataContext = viewModel;

        // Plug/unplug events arrive in bursts; rescan once things settle.
        _deviceChangeDebounce = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _deviceChangeDebounce.Tick += async (_, _) =>
        {
            _deviceChangeDebounce.Stop();
            await _viewModel.Center.RescanAsync();
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowHelpers.ApplyDarkTitleBar(this);
        if (PresentationSource.FromVisual(this) is HwndSource source)
        {
            source.AddHook(WndProc);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE && _settings.RescanOnDeviceChange)
        {
            var change = wParam.ToInt32();
            if (change is DBT_DEVNODES_CHANGED or DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE)
            {
                _deviceChangeDebounce.Stop();
                _deviceChangeDebounce.Start();
            }
        }

        return IntPtr.Zero;
    }
}
