using System.Globalization;
using System.Windows;
using System.Windows.Input;
using UniversalRGB.Drivers.Verification;

namespace UniversalRGB.Desktop.Views;

public partial class DialogWindow : Window
{
    private DialogWindow()
    {
        InitializeComponent();
        var main = Application.Current?.MainWindow;
        if (main is { IsVisible: true } && !ReferenceEquals(main, this))
        {
            Owner = main;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    public static bool Confirm(string title, string message, string confirmText = "OK", bool danger = false, string cancelText = "Cancel")
    {
        var dialog = Create(title, message, danger ? "" : "", danger);
        dialog.ConfirmButton.Content = confirmText;
        dialog.CancelButton.Content = cancelText;
        if (danger)
        {
            dialog.ConfirmButton.Style = (Style)dialog.FindResource("DangerButton");
        }

        return dialog.ShowDialog() == true;
    }

    public static void Alert(string title, string message, bool error = false)
    {
        var dialog = Create(title, message, error ? "" : "", error);
        dialog.CancelButton.Visibility = Visibility.Collapsed;
        dialog.ShowDialog();
    }

    public static string? Prompt(string title, string message, string initial, string confirmText = "Save")
    {
        var dialog = Create(title, message, "", false);
        dialog.ConfirmButton.Content = confirmText;
        dialog.InputBox.Visibility = Visibility.Visible;
        dialog.InputBox.Text = initial;
        dialog.Loaded += (_, _) =>
        {
            dialog.InputBox.Focus();
            dialog.InputBox.SelectAll();
        };
        return dialog.ShowDialog() == true && !string.IsNullOrWhiteSpace(dialog.InputBox.Text) ? dialog.InputBox.Text.Trim() : null;
    }

    /// <summary>The explicit consent step before any installer runs. Returns true only on an explicit "Install".</summary>
    public static bool ConsentToInstall(PackageVerification v)
    {
        var package = v.Package;
        var title = package is null ? "Install unrecognized package?" : $"Install {package.Name}?";
        var message = package is null
            ? "This file is not one of the curated packages. Review the details carefully."
            : package.Purpose;

        var dialog = Create(title, message, "", false);
        var rows = new List<KeyValuePair<string, string>>
        {
            new("File", System.IO.Path.GetFileName(v.FilePath)),
            new("Size", string.Format(CultureInfo.CurrentCulture, "{0:0.0} MB", v.SizeBytes / 1024d / 1024d)),
            new("SHA-256", v.Sha256),
            new("Signature", v.SignatureDetail),
        };
        if (v.SignerSubject is not null)
        {
            rows.Add(new("Signed by", $"{v.SignerSubject} (issued by {v.SignerIssuer}, valid until {v.SignerValidUntil:d})"));
        }

        if (package is not null)
        {
            rows.Add(new("Publisher", package.Publisher));
            rows.Add(new("Official source", package.OfficialPage));
            rows.Add(new("Type", package.Kind == "kernel-driver" ? "Kernel driver" : package.Kind));
            rows.Add(new("Restart", package.Restart switch { "yes" => "Required", "no" => "Not required", _ => "May be required" }));
        }

        rows.Add(new("Changes", package?.RequiresAdministrator == true
            ? "Runs the installer with administrator rights (Windows will show a UAC prompt)."
            : "Runs the installer as your user account."));

        dialog.DetailRows.ItemsSource = rows;
        dialog.DetailRows.Visibility = Visibility.Visible;

        if (v.Blockers.Count > 0)
        {
            dialog.BlockerList.ItemsSource = v.Blockers;
            dialog.BlockerList.Visibility = Visibility.Visible;
            dialog.ConfirmButton.IsEnabled = false;
            dialog.ConfirmButton.Content = "Cannot install";
            dialog.CancelButton.Content = "Close";
        }
        else
        {
            dialog.ConfirmButton.Content = "Install";
        }

        if (v.Notices.Count > 0)
        {
            dialog.NoticeList.ItemsSource = v.Notices;
            dialog.NoticeList.Visibility = Visibility.Visible;
        }

        return dialog.ShowDialog() == true && v.CanInstall;
    }

    private static DialogWindow Create(string title, string message, string glyph, bool warning)
    {
        var dialog = new DialogWindow();
        dialog.Title = title;
        dialog.TitleText.Text = title;
        dialog.MessageText.Text = message;
        dialog.MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        dialog.IconGlyph.Text = glyph;
        if (warning)
        {
            dialog.IconGlyph.Foreground = (System.Windows.Media.Brush)dialog.FindResource("Red");
            dialog.IconBadge.Background = (System.Windows.Media.Brush)dialog.FindResource("RedSoft");
            dialog.IconBadge.BorderBrush = (System.Windows.Media.Brush)dialog.FindResource("Line2");
        }

        return dialog;
    }

    private void OnConfirm(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed && e.OriginalSource is not System.Windows.Controls.TextBox)
        {
            DragMove();
        }
    }
}
