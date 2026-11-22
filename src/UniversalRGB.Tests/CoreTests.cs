using UniversalRGB.Adapters.OpenRGB;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Lighting;
using UniversalRGB.Core.Profiles;

namespace UniversalRGB.Tests;

public class RgbColorTests
{
    [Theory]
    [InlineData("#22D3EE", 0x22, 0xD3, 0xEE)]
    [InlineData("22d3ee", 0x22, 0xD3, 0xEE)]
    [InlineData("#fff", 255, 255, 255)]
    public void Parses_hex(string text, byte r, byte g, byte b)
    {
        Assert.True(RgbColor.TryParseHex(text, out var color));
        Assert.Equal(new RgbColor(r, g, b), color);
    }

    [Theory]
    [InlineData("")]
    [InlineData("#12345")]
    [InlineData("zzzzzz")]
    public void Rejects_bad_hex(string text) => Assert.False(RgbColor.TryParseHex(text, out _));

    [Fact]
    public void OpenRgb_packing_is_0x00BBGGRR()
    {
        var color = new RgbColor(0x11, 0x22, 0x33);
        Assert.Equal(0x00332211u, color.ToOpenRgb());
        Assert.Equal(color, RgbColor.FromOpenRgb(0x00332211u));
    }

    [Fact]
    public void Hsv_round_trip_is_close()
    {
        var original = new RgbColor(34, 211, 238);
        var (h, s, v) = original.ToHsv();
        var back = RgbColor.FromHsv(h, s, v);
        Assert.InRange(Math.Abs(back.R - original.R), 0, 1);
        Assert.InRange(Math.Abs(back.G - original.G), 0, 1);
        Assert.InRange(Math.Abs(back.B - original.B), 0, 1);
    }

    [Fact]
    public void Scale_and_lerp()
    {
        Assert.Equal(new RgbColor(50, 100, 0), new RgbColor(100, 200, 0).Scale(0.5));
        Assert.Equal(new RgbColor(128, 128, 128), RgbColor.Lerp(RgbColor.Black, RgbColor.White, 0.5));
    }
}

public class HardwareIdParserTests
{
    [Fact]
    public void Parses_usb_ids()
    {
        var id = HardwareIdParser.Parse(@"USB\VID_0B05&PID_19AF&REV_0100");
        Assert.Equal(new HardwareIdentity("USB", "0B05", "19AF", "0100"), id);
        Assert.Equal("0B05:19AF", id!.Key);
    }

    [Fact]
    public void Parses_hid_and_openrgb_locations()
    {
        Assert.Equal("HID", HardwareIdParser.Parse(@"HID\VID_1532&PID_0294&MI_00")!.Bus);
        var location = HardwareIdParser.Parse(@"HID: \\?\hid#vid_0b05&pid_19af&col01#7&1234");
        Assert.Equal("0B05:19AF", location!.Key);
        Assert.Equal("HID", location.Bus);
    }

    [Fact]
    public void Parses_pci_ids()
    {
        var id = HardwareIdParser.Parse(@"PCI\VEN_10DE&DEV_1C02&SUBSYS_85B11043&REV_A1");
        Assert.Equal("PCI", id!.Bus);
        Assert.Equal("10DE", id.VendorId);
        Assert.Equal("1C02", id.ProductId);
        Assert.Equal("85B11043", id.SubsystemId);
    }

    [Fact]
    public void Best_identity_prefers_most_specific()
    {
        var best = HardwareIdParser.ParseBest([@"USB\VID_1532", @"USB\VID_1532&PID_0294&REV_0200", @"USB\Class_03"]);
        Assert.Equal("0294", best!.ProductId);
        Assert.Equal("0200", best.Revision);
    }

    [Fact]
    public void Unknown_ids_return_null() => Assert.Null(HardwareIdParser.Parse(@"ACPI\PNP0A08"));
}

public class CapabilitiesTests
{
    [Fact]
    public void Direct_mode_means_streaming()
    {
        var caps = DeviceCapabilities.From(TestData.Device(TestData.Motherboard(argbLeds: 10)));
        Assert.Equal(ControlStrategy.Streaming, caps.Strategy);
        Assert.True(caps.HasResizableZones);
        Assert.False(caps.HasUnconfiguredZones);
        Assert.Contains("Breathing", caps.HardwareEffects);
    }

    [Fact]
    public void Per_led_without_direct_is_static_only()
    {
        var caps = DeviceCapabilities.From(TestData.Device(TestData.Motherboard(withDirect: false)));
        Assert.Equal(ControlStrategy.StaticOnly, caps.Strategy);
        Assert.True(caps.HasUnconfiguredZones);
    }
}

public class EffectRendererTests
{
    [Fact]
    public void Static_fills_every_led_with_brightness()
    {
        var colors = EffectRenderer.Render(LightingEffect.StaticColor(new RgbColor(200, 100, 0), brightness: 50), 0, 5, RenderSignals.None);
        Assert.All(colors, c => Assert.Equal(new RgbColor(100, 50, 0), c));
    }

    [Fact]
    public void Off_is_black() =>
        Assert.All(EffectRenderer.Render(LightingEffect.Off, 3.2, 8, RenderSignals.None), c => Assert.Equal(RgbColor.Black, c));

    [Fact]
    public void Rainbow_wave_spreads_hues_and_moves()
    {
        var effect = new LightingEffect { Kind = EffectKind.RainbowWave, Speed = 50 };
        var a = EffectRenderer.Render(effect, 0, 12, RenderSignals.None);
        var b = EffectRenderer.Render(effect, 1, 12, RenderSignals.None);
        Assert.True(a.Distinct().Count() > 6);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void Breathing_varies_over_time()
    {
        var effect = new LightingEffect { Kind = EffectKind.Breathing, Speed = 100, Colors = [RgbColor.White] };
        var levels = Enumerable.Range(0, 20).Select(i => EffectRenderer.Render(effect, i * 0.05, 1, RenderSignals.None)[0].R).ToList();
        Assert.True(levels.Max() - levels.Min() > 150);
    }

    [Fact]
    public void Temperature_maps_low_and_high_to_gradient_ends()
    {
        var effect = new LightingEffect
        {
            Kind = EffectKind.Temperature,
            Colors = [new RgbColor(0, 0, 255), new RgbColor(255, 0, 0)],
            TemperatureLowC = 40,
            TemperatureHighC = 80,
        };
        Assert.Equal(new RgbColor(0, 0, 255), EffectRenderer.Render(effect, 0, 1, new RenderSignals(30, 0))[0]);
        Assert.Equal(new RgbColor(255, 0, 0), EffectRenderer.Render(effect, 0, 1, new RenderSignals(95, 0))[0]);
        Assert.Equal(new RgbColor(128, 0, 128), EffectRenderer.Render(effect, 0, 1, new RenderSignals(60, 0))[0]);
    }

    [Fact]
    public void Temperature_without_reading_is_dim_first_color()
    {
        var effect = new LightingEffect { Kind = EffectKind.Temperature, Colors = [new RgbColor(0, 0, 200), RgbColor.White] };
        Assert.Equal(new RgbColor(0, 0, 70), EffectRenderer.Render(effect, 0, 1, RenderSignals.None)[0]);
    }

    [Fact]
    public void Music_lights_more_leds_when_louder()
    {
        var effect = new LightingEffect { Kind = EffectKind.MusicReactive, AudioSensitivity = 18 };
        int Lit(double level) => EffectRenderer.Render(effect, 0, 20, new RenderSignals(null, level))
            .Count(c => c.R + c.G + c.B > 100);
        Assert.True(Lit(0.8) > Lit(0.2));
        Assert.Equal(0, Lit(0));
    }

    [Fact]
    public void Gradient_sample_hits_stops()
    {
        IReadOnlyList<RgbColor> stops = [RgbColor.Black, RgbColor.White];
        Assert.Equal(RgbColor.Black, EffectRenderer.Sample(stops, 0));
        Assert.Equal(RgbColor.White, EffectRenderer.Sample(stops, 1));
    }
}

public class HardwareEffectMapperTests
{
    [Fact]
    public void Interpolates_inverted_ranges() =>
        Assert.Equal(1u, HardwareEffectMapper.Interpolate(4, 0, 0.75));

    [Fact]
    public void Rainbow_prefers_random_color_mode()
    {
        var modes = TestData.Motherboard().Modes;
        var choice = HardwareEffectMapper.Choose(modes, new LightingEffect { Kind = EffectKind.RainbowWave, Speed = 100, Direction = EffectDirection.Reverse });
        Assert.NotNull(choice);
        Assert.Equal("Rainbow", choice.Mode.Name);
        Assert.Equal(ModeColorMode.Random, choice.Mode.ColorMode);
        Assert.Equal(10u, choice.Mode.Speed);
        Assert.Equal(ModeDirection.Left, choice.Mode.Direction);
    }

    [Fact]
    public void Off_uses_off_mode_or_black_static()
    {
        var withOff = HardwareEffectMapper.Choose(TestData.Motherboard().Modes, LightingEffect.Off);
        Assert.Equal("Off", withOff!.Mode.Name);

        var noOff = TestData.Motherboard().Modes.Where(m => m.Name != "Off").ToList();
        var fallback = HardwareEffectMapper.Choose(noOff, LightingEffect.Off);
        Assert.Equal("Static", fallback!.Mode.Name);
        Assert.Equal(RgbColor.Black, fallback.Mode.Colors[0]);
    }

    [Fact]
    public void Never_chooses_direct_or_unknown_effects()
    {
        var modes = TestData.Keyboard().Modes;
        Assert.Null(HardwareEffectMapper.Choose(modes, new LightingEffect { Kind = EffectKind.Comet }));
        Assert.Null(HardwareEffectMapper.Choose(modes, new LightingEffect { Kind = EffectKind.Temperature }));
    }
}

public class ProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "urgb-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, true);
        }
    }

    [Fact]
    public void Creates_default_profiles_once()
    {
        var store = new ProfileStore(_dir);
        var first = store.EnsureDefaults();
        Assert.Equal(new[] { "Desktop", "Gaming", "Nighttime" }, first.Select(p => p.Name));
        Assert.Equal(3, store.EnsureDefaults().Count);
    }

    [Fact]
    public void Save_load_round_trip_keeps_effects_and_colors()
    {
        var store = new ProfileStore(_dir);
        var profile = new LightingProfile
        {
            Name = "Custom",
            SyncAll = false,
            GlobalEffect = new LightingEffect { Kind = EffectKind.ColorCycle, Colors = [RgbColor.White, new RgbColor(1, 2, 3)], Speed = 70 },
        };
        profile.Devices["dev"] = new DeviceLightingSetting { Effect = LightingEffect.StaticColor(new RgbColor(9, 8, 7)) };
        store.Save(profile);

        var loaded = store.LoadAll().Single();
        Assert.Equal(EffectKind.ColorCycle, loaded.GlobalEffect.Kind);
        Assert.Equal(new RgbColor(1, 2, 3), loaded.GlobalEffect.Colors[1]);
        Assert.Equal(new RgbColor(9, 8, 7), loaded.Devices["dev"].Effect!.PrimaryColor);
        Assert.Contains("\"#090807\"", File.ReadAllText(Directory.GetFiles(_dir).Single()));
    }

    [Fact]
    public void Import_assigns_new_ids_and_rejects_foreign_files()
    {
        var store = new ProfileStore(_dir);
        var defaults = store.EnsureDefaults();
        var export = Path.Combine(_dir, "..", Guid.NewGuid().ToString("N") + ".json");
        try
        {
            store.Export(defaults, export);
            var imported = store.Import(export);
            Assert.Equal(3, imported.Count);
            Assert.DoesNotContain(imported, p => defaults.Any(d => d.Id == p.Id));
            Assert.Equal(6, store.LoadAll().Count);

            File.WriteAllText(export, """{ "format": "Something.Else", "profiles": [] }""");
            Assert.Throws<InvalidDataException>(() => store.Import(export));
        }
        finally
        {
            File.Delete(export);
        }
    }

    [Fact]
    public void Resolve_effect_respects_sync_device_and_zone()
    {
        var global = LightingEffect.StaticColor(RgbColor.White);
        var device = LightingEffect.StaticColor(new RgbColor(1, 0, 0));
        var zone = LightingEffect.StaticColor(new RgbColor(0, 1, 0));
        var profile = new LightingProfile { SyncAll = true, GlobalEffect = global };
        profile.Devices["d"] = new DeviceLightingSetting { Effect = device, Zones = { ["Strip"] = zone } };

        Assert.Same(global, profile.ResolveEffect("d", "Strip"));
        profile.SyncAll = false;
        Assert.Same(zone, profile.ResolveEffect("d", "Strip"));
        Assert.Same(device, profile.ResolveEffect("d", "Other"));
        Assert.Same(global, profile.ResolveEffect("unknown", null));
    }

    [Theory]
    [InlineData(23, 0, 7, 0, 2, 0, true)]
    [InlineData(23, 0, 7, 0, 12, 0, false)]
    [InlineData(9, 0, 17, 0, 9, 0, true)]
    [InlineData(9, 0, 17, 0, 17, 0, false)]
    public void Schedule_windows_handle_midnight(int sh, int sm, int eh, int em, int nh, int nm, bool expected)
    {
        var schedule = new ProfileSchedule { Enabled = true, Start = new TimeOnly(sh, sm), End = new TimeOnly(eh, em) };
        Assert.Equal(expected, schedule.IsActiveAt(new TimeOnly(nh, nm)));
    }
}
