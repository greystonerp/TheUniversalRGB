using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalRGB.Core.Diagnostics;
using UniversalRGB.Desktop.Services;

namespace UniversalRGB.Desktop.Views;

public enum SetupStepState
{
    Pending,
    Active,
    Done,
    Failed,
}

public sealed partial class SetupStep : ObservableObject
{
    [ObservableProperty] private SetupStepState _state;

    public SetupStep(string text, Action work)
    {
        Text = text;
        Work = work;
    }

    public string Text { get; }

    public Action Work { get; }
}

/// <summary>
/// Shown when the published EXE is launched before it is installed (or when it is newer than the installed copy).
/// A first install asks how Universal RGB should start with Windows; updates run straight through.
/// Afterwards the installed app is opened and setup closes itself.
/// </summary>
public partial class SetupWindow : Window
{
    private readonly LaunchAction _action;
    private readonly string[] _args;
    private readonly IAppLog _log;
    private readonly Action _runPortable;
    private readonly ObservableCollection<SetupStep> _steps = [];

    public SetupWindow(LaunchAction action, string[] args, IAppLog log, Action runPortable)
    {
        InitializeComponent();
        _action = action;
        _args = args;
        _log = log;
        _runPortable = runPortable;

        var version = SelfInstaller.CurrentVersion.ToString(3);
        Footer.Text = "Location: " + SelfInstaller.InstallDirectory;
        Footer.ToolTip = SelfInstaller.InstallDirectory;
        StepList.ItemsSource = _steps;

        if (action == LaunchAction.Update)
        {
            Subtitle.Text = $"Updating to version {version}…";
            Loaded += async (_, _) => await RunAsync();
        }
        else
        {
            Subtitle.Text = $"Install version {version} for {Environment.UserName}. No administrator rights needed.";
            OptionsFooter.Text = $"Installs to {SelfInstaller.InstallDirectory}. You can change these choices later in Settings.";
            OptionsPanel.Visibility = Visibility.Visible;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void BuildSteps()
    {
        var isUpdate = _action == LaunchAction.Update;
        var startWithWindows = StartWithWindowsBox.IsChecked == true;
        var startInTray = StartInTray.IsChecked == true;
        var desktopShortcut = DesktopShortcutBox.IsChecked == true;

        _steps.Clear();
        if (isUpdate || SelfInstaller.OtherCopyRunning())
        {
            _steps.Add(new SetupStep("Closing the copy that is already running", () => SelfInstaller.StopRunningCopies(_log)));
        }

        _steps.Add(new SetupStep(isUpdate ? "Updating program files" : "Copying Universal RGB", SelfInstaller.CopyFiles));
        _steps.Add(new SetupStep(
            isUpdate ? "Refreshing shortcuts" : desktopShortcut ? "Adding Start menu and desktop shortcuts" : "Adding a Start menu shortcut",
            () => SelfInstaller.CreateShortcuts(isUpdate, desktopShortcut)));
        _steps.Add(new SetupStep("Registering in Settings → Apps", SelfInstaller.Register));
        if (!isUpdate)
        {
            _steps.Add(new SetupStep(
                startWithWindows ? (startInTray ? "Starting with Windows, in the tray" : "Starting with Windows, as a window") : "Not starting with Windows",
                () => SelfInstaller.ApplyStartupChoice(startWithWindows, startInTray)));
        }

        _steps.Add(new SetupStep("Opening Universal RGB", () => SelfInstaller.LaunchInstalled(_args, justInstalled: !isUpdate)));
        Progress.Maximum = _steps.Count;
    }

    private async Task RunAsync()
    {
        if (_steps.Count == 0)
        {
            BuildSteps();
        }

        OptionsPanel.Visibility = Visibility.Collapsed;
        ProgressPanel.Visibility = Visibility.Visible;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ErrorButtons.Visibility = Visibility.Collapsed;
        Progress.Value = 0;
        if (_action != LaunchAction.Update)
        {
            Subtitle.Text = $"Installing version {SelfInstaller.CurrentVersion.ToString(3)}…";
        }

        foreach (var step in _steps)
        {
            step.State = SetupStepState.Pending;
        }

        foreach (var step in _steps)
        {
            step.State = SetupStepState.Active;
            try
            {
                await Task.Run(step.Work);
                await Task.Delay(180); // keep each step readable
            }
            catch (Exception ex)
            {
                step.State = SetupStepState.Failed;
                _log.Error($"Setup step failed: {step.Text}", ex);
                Subtitle.Text = _action == LaunchAction.Update ? "The update could not finish." : "Installation could not finish.";
                ErrorText.Text = ex.Message;
                ErrorPanel.Visibility = Visibility.Visible;
                ErrorButtons.Visibility = Visibility.Visible;
                return;
            }

            step.State = SetupStepState.Done;
            Progress.Value += 1;
        }

        _log.Info($"Setup finished ({_action}) → {SelfInstaller.InstalledExe}");
        Subtitle.Text = _action == LaunchAction.Update ? "Updated. Opening Universal RGB…" : "Installed. Opening Universal RGB…";
        await Task.Delay(700);
        Application.Current.Shutdown();
    }

    private void OnStartupToggled(object sender, RoutedEventArgs e)
    {
        if (StartModePanel is not null)
        {
            StartModePanel.IsEnabled = StartWithWindowsBox.IsChecked == true;
            StartModePanel.Opacity = StartModePanel.IsEnabled ? 1 : 0.45;
        }
    }

    private async void OnInstall(object sender, RoutedEventArgs e)
    {
        BuildSteps();
        await RunAsync();
    }

    private async void OnRetry(object sender, RoutedEventArgs e) => await RunAsync();

    private void OnRunPortable(object sender, RoutedEventArgs e)
    {
        Hide();
        _runPortable();
        Close();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase)
        {
            DragMove();
        }
    }
}
