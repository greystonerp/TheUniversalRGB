using UniversalRGB.Core.Devices;

namespace UniversalRGB.Core.Lighting;

/// <summary>
/// Finds how many LEDs are chained on an addressable (ARGB) header. Standard 5 V ARGB only sends data one way, so the
/// controller cannot report what is connected. Instead one LED is lit and the user says whether they can see it; a
/// binary search narrows the count down in about log2(maximum) questions.
/// </summary>
public sealed class LedCountSearch
{
    private int _low;  // LEDs known to exist
    private int _high; // most LEDs there can be

    public LedCountSearch(int maximum)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximum, 1);
        Maximum = maximum;
        _high = maximum;
        TotalSteps = (int)Math.Ceiling(Math.Log2(maximum + 1));
    }

    public int Maximum { get; }

    /// <summary>Questions answered so far.</summary>
    public int Step { get; private set; }

    /// <summary>Questions needed at most.</summary>
    public int TotalSteps { get; }

    public bool IsDone => _low == _high;

    /// <summary>The number of LEDs found (valid once <see cref="IsDone"/>).</summary>
    public int Result => _low;

    /// <summary>1-based number of the LED to light for the current question.</summary>
    public int Probe => (_low + _high + 1) / 2;

    public void Answer(bool visible)
    {
        if (IsDone)
        {
            return;
        }

        if (visible)
        {
            _low = Probe;
        }
        else
        {
            _high = Probe - 1;
        }

        Step++;
    }
}

/// <summary>The light patterns shown on a header while its LEDs are counted.</summary>
public static class LedCountPattern
{
    /// <summary>Marks the last LED of the count (and the single LED of a search question).</summary>
    public static readonly RgbColor Marker = RgbColor.White;

    /// <summary>Distinct colors for consecutive fans, so a wrong "LEDs per fan" shows as a fan with two colors.</summary>
    public static readonly IReadOnlyList<RgbColor> FanColors =
    [
        new(0, 190, 255),
        new(255, 0, 150),
        new(255, 160, 0),
        new(0, 255, 90),
        new(150, 70, 255),
        new(255, 50, 0),
    ];

    /// <summary>LEDs 1..count lit (the last one white, so the user can see where the count ends); everything after it off.</summary>
    public static void Fill(Span<RgbColor> zone, int count, int ledsPerFan)
    {
        zone.Clear();
        var lit = Math.Min(Math.Max(count, 0), zone.Length);
        for (var i = 0; i < lit; i++)
        {
            zone[i] = i == count - 1
                ? Marker
                : FanColors[ledsPerFan > 0 ? i / ledsPerFan % FanColors.Count : 0].Scale(0.55);
        }
    }

    /// <summary>Only LED number <paramref name="ledNumber"/> (1-based) lit.</summary>
    public static void Probe(Span<RgbColor> zone, int ledNumber)
    {
        zone.Clear();
        if (ledNumber >= 1 && ledNumber <= zone.Length)
        {
            zone[ledNumber - 1] = Marker;
        }
    }

    /// <summary>"3 fans × 8 LEDs", "26 LEDs", "3 fans × 8 + 2 LEDs".</summary>
    public static string Describe(int count, int ledsPerFan)
    {
        if (count <= 0)
        {
            return "No LEDs";
        }

        if (ledsPerFan <= 0)
        {
            return count == 1 ? "1 LED" : $"{count} LEDs";
        }

        var fans = count / ledsPerFan;
        var rest = count % ledsPerFan;
        var fanText = fans == 1 ? "1 fan" : $"{fans} fans";
        return fans == 0 ? $"{count} LEDs (less than one fan)"
            : rest == 0 ? $"{fanText} × {ledsPerFan} LEDs"
            : $"{fanText} × {ledsPerFan} + {rest} LEDs";
    }
}
