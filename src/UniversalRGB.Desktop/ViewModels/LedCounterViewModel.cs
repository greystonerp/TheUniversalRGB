using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Lighting;
using UniversalRGB.Desktop.Services;

namespace UniversalRGB.Desktop.ViewModels;

public sealed record PerFanOption(string Label, int Value);

/// <summary>
/// The "Count LEDs" helper for one addressable header. ARGB headers cannot report what is plugged into them, so the
/// header is lit LED by LED while the user answers whether they can see it, then the result is confirmed on a slider.
/// The other lights on the same controller are switched off meanwhile, so it is clear which fans belong to the header.
/// </summary>
public sealed partial class LedCounterViewModel : ObservableObject
{
    /// <summary>Most LEDs the counter checks for; headers often accept far more than any real build uses.</summary>
    private const int SearchLimit = 300;

    private readonly ControlCenter _center;
    private readonly RgbDevice _device;
    private readonly RgbZone _zone;
    private readonly int _originalCount;
    private LedCountSearch? _search;
    private bool _previewing;
    private int _currentSize;

    // Read by the lighting engine thread every frame.
    private volatile int _shownCount;
    private volatile int _shownPerFan;
    private volatile int _shownProbe;

    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private int _count;
    [ObservableProperty] private int _ledsPerFan;
    [ObservableProperty] private string _questionStep = "";
    [ObservableProperty] private string? _resultMessage;

    public LedCounterViewModel(ControlCenter center, RgbDevice device, RgbZone zone)
    {
        _center = center;
        _device = device;
        _zone = zone;
        _originalCount = zone.LedCount;
        _currentSize = zone.LedCount;
        Minimum = zone.LedsMin;
        Maximum = Math.Min(zone.LedsMax, Math.Max(SearchLimit, zone.LedCount));
        _count = Math.Clamp(zone.LedCount, Minimum, Maximum);
        _shownCount = _count;
    }

    public string ZoneName => _zone.Name;

    public string DeviceName => _device.Name;

    public int Minimum { get; }

    public int Maximum { get; }

    public IReadOnlyList<PerFanOption> PerFanOptions { get; } =
    [
        new("Not sure", 0), new("6", 6), new("8", 8), new("9", 9), new("12", 12), new("16", 16), new("18", 18), new("20", 20), new("24", 24),
    ];

    public bool IsAdjusting => !IsSearching;

    public bool HasPerFan => LedsPerFan > 0;

    public string Summary => LedCountPattern.Describe(Count, LedsPerFan);

    public string SaveText => Count == 1 ? "Save 1 LED" : $"Save {Count} LEDs";

    /// <summary>Takes over the header and asks the first question. Returns false (after telling the user why) if it cannot.</summary>
    public async Task<bool> StartAsync()
    {
        if (_center.LightingPaused)
        {
            _center.Notify("Resume the lighting engine to count LEDs.", ToastKind.Info);
            return false;
        }

        IsBusy = true;
        try
        {
            // The header must be long enough for every LED the search might light.
            if (Maximum != _currentSize)
            {
                if (!await _center.ResizeZoneAsync(_device, _zone, Maximum, announce: false))
                {
                    return false;
                }

                _currentSize = Maximum;
            }

            if (!_center.Services.Engine.BeginPreview(_device.Id, Render))
            {
                _center.Notify($"{_device.Name} has no Direct mode, so its LEDs cannot be counted here. Type the number instead.", ToastKind.Info);
                await RestoreSizeAsync(_originalCount);
                return false;
            }

            _previewing = true;
            StartSearch();
            return true;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>Gives the header back to the lighting profile, saving the count or restoring the previous one.</summary>
    public async Task FinishAsync(bool save)
    {
        IsBusy = true;
        var target = save ? Count : _originalCount;
        var ok = await RestoreSizeAsync(target);
        if (_previewing)
        {
            _center.Services.Engine.EndPreview(_device.Id);
            _previewing = false;
        }

        if (save && ok)
        {
            _center.Notify($"{_zone.Name} set to {LedCountPattern.Describe(Count, LedsPerFan)}.", ToastKind.Success);
        }
    }

    [RelayCommand]
    private void StartSearch()
    {
        _search = new LedCountSearch(Maximum);
        ResultMessage = null;
        IsSearching = true;
        ShowQuestion();
    }

    [RelayCommand]
    private void AnswerYes() => Answer(visible: true);

    [RelayCommand]
    private void AnswerNo() => Answer(visible: false);

    [RelayCommand]
    private void UseSlider()
    {
        _search = null;
        _shownProbe = 0;
        ResultMessage = null;
        IsSearching = false;
    }

    [RelayCommand]
    private void Decrease() => Count = Math.Max(Minimum, Count - 1);

    [RelayCommand]
    private void Increase() => Count = Math.Min(Maximum, Count + 1);

    [RelayCommand]
    private void PreviousFan()
    {
        if (LedsPerFan > 0)
        {
            Count = Math.Max(Minimum, Count % LedsPerFan == 0 ? Count - LedsPerFan : Count / LedsPerFan * LedsPerFan);
        }
    }

    [RelayCommand]
    private void NextFan()
    {
        if (LedsPerFan > 0)
        {
            Count = Math.Min(Maximum, (Count / LedsPerFan + 1) * LedsPerFan);
        }
    }

    private void Answer(bool visible)
    {
        if (_search is null)
        {
            return;
        }

        _search.Answer(visible);
        if (!_search.IsDone)
        {
            ShowQuestion();
            return;
        }

        var found = _search.Result;
        _search = null;
        _shownProbe = 0;
        Count = Math.Clamp(found, Minimum, Maximum);
        IsSearching = false;
        ResultMessage = found == 0
            ? "No LED lit up. Check that the fans' 3-pin 5 V ARGB cable is on this header (not a 4-pin 12 V RGB one), then ask again, or set the number with the slider."
            : found >= Maximum
                ? $"Every LED up to {Maximum} lit up, which is as far as the counter checks. If there are more, type the number on the Devices page."
                : $"Found {found} LEDs. Every LED on this header should now be lit, with the white one at the very end. Adjust it if not, then save.";
    }

    private void ShowQuestion()
    {
        QuestionStep = $"QUESTION {_search!.Step + 1} OF ABOUT {_search.TotalSteps}";
        _shownProbe = _search.Probe;
    }

    private async Task<bool> RestoreSizeAsync(int size)
    {
        if (size == _currentSize)
        {
            return true;
        }

        if (!await _center.ResizeZoneAsync(_device, _zone, size, announce: false))
        {
            return false;
        }

        _currentSize = size;
        return true;
    }

    /// <summary>Runs on the lighting engine thread: this header shows the pattern, everything else on the controller is off.</summary>
    private RgbColor[] Render(RgbDevice device)
    {
        var colors = new RgbColor[device.LedCount];
        var zone = device.FindZone(_zone.Id);
        if (zone is null || zone.StartIndex < 0 || zone.StartIndex + zone.LedCount > colors.Length)
        {
            return colors;
        }

        var span = colors.AsSpan(zone.StartIndex, zone.LedCount);
        var probe = _shownProbe;
        if (probe > 0)
        {
            LedCountPattern.Probe(span, probe);
        }
        else
        {
            LedCountPattern.Fill(span, _shownCount, _shownPerFan);
        }

        return colors;
    }

    partial void OnIsSearchingChanged(bool value) => OnPropertyChanged(nameof(IsAdjusting));

    partial void OnCountChanged(int value)
    {
        _shownCount = value;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(SaveText));
    }

    partial void OnLedsPerFanChanged(int value)
    {
        _shownPerFan = value;
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasPerFan));
    }
}
