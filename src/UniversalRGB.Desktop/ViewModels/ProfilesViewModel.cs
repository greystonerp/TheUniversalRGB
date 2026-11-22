using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using UniversalRGB.Core.Lighting;
using UniversalRGB.Core.Profiles;
using UniversalRGB.Desktop.Services;
using UniversalRGB.Desktop.Views;

namespace UniversalRGB.Desktop.ViewModels;

public sealed partial class ProfileCard : ObservableObject
{
    [ObservableProperty] private bool _scheduleEnabled;
    [ObservableProperty] private string _scheduleStart;
    [ObservableProperty] private string _scheduleEnd;

    public ProfileCard(LightingProfile profile, bool isActive, bool isStartup)
    {
        Profile = profile;
        IsActive = isActive;
        IsStartup = isStartup;
        _scheduleEnabled = profile.Schedule.Enabled;
        _scheduleStart = profile.Schedule.Start.ToString("HH:mm", CultureInfo.InvariantCulture);
        _scheduleEnd = profile.Schedule.End.ToString("HH:mm", CultureInfo.InvariantCulture);
        Swatch = Shell.SwatchFor(profile.GlobalEffect);
    }

    public LightingProfile Profile { get; }
    public string Name => Profile.Name;
    public string Description => string.IsNullOrWhiteSpace(Profile.Description) ? EffectSummary : Profile.Description;
    public string EffectSummary
    {
        get
        {
            var effect = LightingEffect.DisplayName(Profile.GlobalEffect.Kind);
            var overrides = Profile.SyncAll ? 0 : Profile.Devices.Values.Count(d => d.Effect is not null || d.Zones.Count > 0);
            var disabled = Profile.Devices.Values.Count(d => !d.Enabled);
            var parts = new List<string> { Profile.SyncAll ? $"{effect} · synced" : $"{effect} · per device" };
            if (overrides > 0)
            {
                parts.Add($"{overrides} custom device(s)");
            }

            if (disabled > 0)
            {
                parts.Add($"{disabled} excluded");
            }

            if (Profile.MasterBrightness < 100)
            {
                parts.Add($"{Profile.MasterBrightness:0}% brightness");
            }

            return string.Join(" · ", parts);
        }
    }

    public Brush Swatch { get; }
    public bool IsActive { get; }
    public bool IsStartup { get; }
    public string Modified => "Edited " + Profile.ModifiedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly MainViewModel _main;

    public ProfilesViewModel(ControlCenter center, MainViewModel main)
    {
        Center = center;
        _main = main;
        center.Profiles.CollectionChanged += (_, _) => Rebuild();
        center.PropertyChanged += OnCenterChanged;
        Rebuild();
    }

    public ControlCenter Center { get; }

    public ObservableCollection<ProfileCard> Cards { get; } = [];

    [RelayCommand]
    private void Apply(ProfileCard card) => Center.ActivateProfile(card.Profile);

    [RelayCommand]
    private void Edit(ProfileCard card) => _main.EditProfile(card.Profile);

    [RelayCommand]
    private void Duplicate(ProfileCard card)
    {
        var copy = Center.Duplicate(card.Profile);
        Center.Notify($"Created {copy.Name}.", ToastKind.Success);
    }

    [RelayCommand]
    private void Rename(ProfileCard card)
    {
        var name = DialogWindow.Prompt("Rename profile", "New name:", card.Profile.Name, "Rename");
        if (name is null)
        {
            return;
        }

        var updated = card.Profile.Clone();
        updated.Name = name;
        Center.SaveProfile(updated);
        Rebuild();
    }

    [RelayCommand]
    private void Delete(ProfileCard card)
    {
        if (Center.Profiles.Count <= 1)
        {
            Center.Notify("Keep at least one profile.", ToastKind.Warning);
            return;
        }

        if (DialogWindow.Confirm("Delete profile?", $"\"{card.Name}\" will be permanently deleted.", "Delete", danger: true))
        {
            Center.DeleteProfile(card.Profile);
        }
    }

    [RelayCommand]
    private void ToggleStartup(ProfileCard card)
    {
        Center.SetStartupProfile(card.IsStartup ? null : card.Profile);
        Rebuild();
    }

    [RelayCommand]
    private void SaveSchedule(ProfileCard card)
    {
        if (!TryParseTime(card.ScheduleStart, out var start) || !TryParseTime(card.ScheduleEnd, out var end))
        {
            Center.Notify("Use 24-hour times like 19:00 and 23:30.", ToastKind.Warning);
            return;
        }

        if (start == end)
        {
            Center.Notify("Start and end time must differ.", ToastKind.Warning);
            return;
        }

        var updated = card.Profile.Clone();
        updated.Schedule = new ProfileSchedule { Enabled = card.ScheduleEnabled, Start = start, End = end };
        Center.SaveProfile(updated);
        Center.Notify(updated.Schedule.Enabled
            ? $"{updated.Name} runs daily {start:HH\\:mm}–{end:HH\\:mm}."
            : $"Schedule for {updated.Name} turned off.", ToastKind.Success);
        Rebuild();
    }

    [RelayCommand]
    private void NewProfile()
    {
        var name = DialogWindow.Prompt("New profile", "Name for the new profile (starts from the current lighting):", "My profile", "Create");
        if (name is null)
        {
            return;
        }

        var created = Center.SaveAsNew(Center.LiveProfile ?? new LightingProfile(), name);
        _main.EditProfile(created);
    }

    [RelayCommand]
    private void Export(ProfileCard? card)
    {
        var profiles = card is null ? Center.Profiles.ToList() : [card.Profile];
        var dialog = new SaveFileDialog
        {
            Title = card is null ? "Export all profiles" : $"Export {card.Name}",
            Filter = "Universal RGB profiles (*.json)|*.json",
            FileName = card is null ? "UniversalRGB-profiles.json" : $"{Sanitize(card.Name)}.json",
        };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            Center.Services.Profiles.Export(profiles, dialog.FileName);
            Center.Notify($"Exported {profiles.Count} profile(s).", ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Center.Notify("Export failed: " + ex.Message, ToastKind.Error);
        }
    }

    [RelayCommand]
    private void Import()
    {
        var dialog = new OpenFileDialog { Title = "Import profiles", Filter = "Universal RGB profiles (*.json)|*.json" };
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var imported = Center.Services.Profiles.Import(dialog.FileName);
            Center.LoadProfiles();
            Center.Notify($"Imported {imported.Count} profile(s).", ToastKind.Success);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            Center.Notify("Import failed: " + ex.Message, ToastKind.Error);
        }
    }

    private void OnCenterChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ControlCenter.ActiveProfile) or nameof(ControlCenter.IsTransient) or nameof(ControlCenter.Profiles))
        {
            Rebuild();
        }
    }

    private void Rebuild()
    {
        Cards.Clear();
        var startup = Center.Services.Settings.StartupProfileId;
        foreach (var profile in Center.Profiles.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var active = !Center.IsTransient && Center.ActiveProfile?.Id == profile.Id;
            Cards.Add(new ProfileCard(profile, active, profile.Id == startup));
        }
    }

    private static bool TryParseTime(string text, out TimeOnly time) =>
        TimeOnly.TryParseExact(text.Trim(), ["H:mm", "HH:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out time);

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
}
