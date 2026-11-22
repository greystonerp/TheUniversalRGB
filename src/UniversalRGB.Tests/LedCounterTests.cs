using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Lighting;
using UniversalRGB.Core.Profiles;

namespace UniversalRGB.Tests;

public class LedCounterTests
{
    /// <summary>Answers the questions the way a user would for a header with <paramref name="actual"/> LEDs.</summary>
    private static (int Result, int Questions) Simulate(int maximum, int actual)
    {
        var search = new LedCountSearch(maximum);
        var questions = 0;
        while (!search.IsDone)
        {
            Assert.InRange(search.Probe, 1, maximum);
            search.Answer(visible: search.Probe <= actual);
            questions++;
        }

        return (search.Result, questions);
    }

    [Fact]
    public void Search_finds_every_possible_count_within_the_promised_number_of_questions()
    {
        foreach (var maximum in new[] { 1, 7, 120, 300 })
        {
            var promised = new LedCountSearch(maximum).TotalSteps;
            for (var actual = 0; actual <= maximum; actual++)
            {
                var (result, questions) = Simulate(maximum, actual);
                Assert.Equal(actual, result);
                Assert.True(questions <= promised, $"max {maximum}, {actual} LEDs took {questions} questions (promised {promised}).");
            }
        }

        Assert.Equal(9, new LedCountSearch(300).TotalSteps);
    }

    [Fact]
    public void Search_caps_at_the_maximum_when_every_led_lights()
    {
        Assert.Equal(120, Simulate(120, actual: 500).Result);
    }

    [Fact]
    public void Fill_lights_the_count_with_a_white_end_marker_and_colors_each_fan()
    {
        var zone = new RgbColor[40];
        LedCountPattern.Fill(zone, count: 24, ledsPerFan: 8);

        Assert.Equal(LedCountPattern.Marker, zone[23]);
        Assert.All(zone[24..], c => Assert.Equal(RgbColor.Black, c));
        Assert.All(zone[..8], c => Assert.Equal(zone[0], c));
        Assert.NotEqual(zone[0], zone[8]);
        Assert.NotEqual(zone[8], zone[16]);
        Assert.NotEqual(RgbColor.Black, zone[0]);
        Assert.NotEqual(LedCountPattern.Marker, zone[0]);
    }

    [Fact]
    public void Fill_and_probe_never_write_outside_the_zone()
    {
        var zone = new RgbColor[10];
        LedCountPattern.Fill(zone, count: 50, ledsPerFan: 0);
        Assert.All(zone, c => Assert.NotEqual(RgbColor.Black, c));

        LedCountPattern.Probe(zone, 4);
        Assert.Equal(LedCountPattern.Marker, zone[3]);
        Assert.Equal(1, zone.Count(c => c != RgbColor.Black));

        LedCountPattern.Probe(zone, 11);
        Assert.All(zone, c => Assert.Equal(RgbColor.Black, c));
    }

    [Theory]
    [InlineData(24, 8, "3 fans × 8 LEDs")]
    [InlineData(26, 8, "3 fans × 8 + 2 LEDs")]
    [InlineData(8, 8, "1 fan × 8 LEDs")]
    [InlineData(5, 8, "5 LEDs (less than one fan)")]
    [InlineData(26, 0, "26 LEDs")]
    [InlineData(0, 8, "No LEDs")]
    public void Describes_the_count_per_fan(int count, int perFan, string expected) =>
        Assert.Equal(expected, LedCountPattern.Describe(count, perFan));
}

public class LightingEnginePreviewTests
{
    [Fact]
    public async Task Preview_replaces_the_profile_on_that_device_until_it_ends()
    {
        var device = TestData.Device(TestData.Motherboard(argbLeds: 4));
        var adapter = new RecordingAdapter(device);
        var registry = new DeviceRegistry();
        registry.AddAdapter(adapter);
        await registry.RefreshAsync(CancellationToken.None);
        var engine = new LightingEngine(registry) { FrameRate = 60 };
        var red = new RgbColor(255, 0, 0);

        // The profile leaves the device out; a preview still reaches it.
        var profile = new LightingProfile { GlobalEffect = LightingEffect.StaticColor(red) };
        profile.Devices[device.Id] = new DeviceLightingSetting { Enabled = false };
        engine.Apply(profile);

        Assert.True(engine.BeginPreview(device.Id, d =>
        {
            var colors = new RgbColor[d.LedCount];
            LedCountPattern.Probe(colors.AsSpan(3, 4), 2);
            return colors;
        }));

        engine.Start();
        await Task.Delay(250);
        engine.EndPreview(device.Id);
        await Task.Delay(150);
        await engine.StopAsync();

        var frames = adapter.Frames.Where(f => f.Device == device.Id).ToList();
        var first = Assert.Single(frames); // sent once, not re-sent while unchanged, and not touched after the preview ended
        Assert.Equal(LedCountPattern.Marker, first.Colors[4]);
        Assert.Equal(1, first.Colors.Count(c => c != RgbColor.Black));
    }

    [Fact]
    public async Task Preview_is_refused_for_devices_without_direct_mode()
    {
        var device = TestData.Device(TestData.Motherboard(argbLeds: 4, withDirect: false));
        var registry = new DeviceRegistry();
        registry.AddAdapter(new RecordingAdapter(device));
        await registry.RefreshAsync(CancellationToken.None);
        var engine = new LightingEngine(registry);
        Assert.False(engine.BeginPreview(device.Id, d => new RgbColor[d.LedCount]));
    }
}
