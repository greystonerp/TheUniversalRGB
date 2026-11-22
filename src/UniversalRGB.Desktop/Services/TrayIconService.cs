using System.Windows;
using Forms = System.Windows.Forms;

namespace UniversalRGB.Desktop.Services;

/// <summary>Notification-area icon so animated effects keep running while the window is closed.</summary>
public sealed class TrayIconService : IDisposable
{
    private readonly ControlCenter _center;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ContextMenuStrip _menu = new();
    private bool _balloonShown;

    public TrayIconService(ControlCenter center, Action show, Action exit)
    {
        _center = center;
        _show = show;
        _exit = exit;

        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"))!.Stream;
        _icon = new Forms.NotifyIcon
        {
            Icon = new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize),
            Text = "Universal RGB Control",
            Visible = true,
            ContextMenuStrip = _menu,
        };
        _icon.DoubleClick += (_, _) => _show();
        _menu.Opening += (_, _) => BuildMenu();
        BuildMenu();
    }

    public void ShowHiddenHint()
    {
        if (_balloonShown)
        {
            return;
        }

        _balloonShown = true;
        _icon.ShowBalloonTip(3000, "Universal RGB is still running", "Lighting stays active. Use the tray icon to reopen or exit.", Forms.ToolTipIcon.Info);
    }

    private void BuildMenu()
    {
        _menu.Items.Clear();
        _menu.Items.Add("Open Universal RGB", null, (_, _) => _show());
        _menu.Items.Add(new Forms.ToolStripSeparator());

        var profiles = new Forms.ToolStripMenuItem("Profiles");
        foreach (var profile in _center.Profiles)
        {
            var item = new Forms.ToolStripMenuItem(profile.Name)
            {
                Checked = !_center.IsTransient && _center.ActiveProfile?.Id == profile.Id,
            };
            var captured = profile;
            item.Click += (_, _) => _center.ActivateProfile(captured);
            profiles.DropDownItems.Add(item);
        }

        _menu.Items.Add(profiles);
        _menu.Items.Add(_center.LightingPaused ? "Resume lighting" : "Pause lighting", null,
            async (_, _) => await _center.SetPausedAsync(!_center.LightingPaused));
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => _exit());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
    }
}
