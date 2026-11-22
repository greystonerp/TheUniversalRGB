using UniversalRGB.Adapters.OpenRGB;
using UniversalRGB.Core.Compatibility;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Inventory;

namespace UniversalRGB.Tests;

public class OpenRgbLogAnalyzerTests
{
    // Excerpt of a real OpenRGB 1.1 log from a run without administrator rights.
    private static readonly string[] RealLog =
    [
        "    OpenRGB v1.1",
        "    Commit: 8e16de27858cf2be6af54a33ce003df74cb9fd3e from 2026-10-16 16:37:19 -0500",
        "    Launched: 20261121_171106",
        "[110       ][Info   ]Start PawnIO: SmbusI801.bin",
        "[111       ][Error  ]Could not open PawnIO, PawnIO initialization aborted",
        "[262       ][Info   ][DetectionManager] Registering RGB controller Razer Ornata V3 X Rev2",
        "[325       ][Info   ][DetectionManager] Registering RGB controller ASUS PRIME B550M-A WIFI II",
        "[845       ][Error  ][NetworkServer] recv_select failed receiving magic, closing listener",
    ];

    [Fact]
    public void Reads_version_smbus_failure_and_registered_controllers()
    {
        var findings = OpenRgbLogAnalyzer.Analyze(RealLog);
        Assert.Equal("1.1", findings.Version);
        Assert.Equal(new DateTime(2026, 11, 21, 17, 11, 6), findings.StartedAt);
        Assert.True(findings.SmbusUnavailable);
        Assert.Equal(["Razer Ornata V3 X Rev2", "ASUS PRIME B550M-A WIFI II"], findings.RegisteredControllers);
        Assert.Equal(BoardControllerIssue.None, findings.MsiMysticLight);
    }

    [Theory]
    [InlineData("[300][Warning][MSIMotherboard185] Controller firmware below minimum supported version, skipping", BoardControllerIssue.FirmwareTooOld)]
    [InlineData("[300][Info   ]No matching driver found for MSI MPG B550 GAMING PLUS (MS-7C56), packet length = 0", BoardControllerIssue.NoResponse)]
    [InlineData("[300][Info   ]Found Board MSI MPG B550 GAMING PLUS (MS-7C56) but does not have valid config", BoardControllerIssue.NoBoardConfig)]
    public void Recognizes_msi_mystic_light_outcomes(string line, BoardControllerIssue expected) =>
        Assert.Equal(expected, OpenRgbLogAnalyzer.Analyze([line]).MsiMysticLight);

    [Fact]
    public void Reads_newest_log_file_even_while_open()
    {
        var dir = Path.Combine(Path.GetTempPath(), "urgb-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllLines(Path.Combine(dir, "OpenRGB_20260801_034251.log"), ["    OpenRGB v1.0rc3"]);
            var current = Path.Combine(dir, "OpenRGB_20261121_171106.log");
            using var writer = new FileStream(current, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            var bytes = System.Text.Encoding.UTF8.GetBytes(string.Join("\n", RealLog));
            writer.Write(bytes);
            writer.Flush();

            var findings = OpenRgbLogAnalyzer.ReadLatest(dir);
            Assert.Equal("1.1", findings!.Version);
            Assert.True(findings.SmbusUnavailable);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class MemoryRgbHintsTests
{
    [Theory]
    [InlineData("CMH32GX4M2D3600C18", "Corsair Vengeance RGB")]
    [InlineData("CMW32GX4M2E3200C16", "Corsair Vengeance RGB PRO")]
    [InlineData("F4-3600C16D-32GTZRC", "G.Skill Trident Z RGB")]
    [InlineData("F5-6000J3038F16GX2-TZ5NR", "G.Skill Trident Z5 Neo RGB")]
    [InlineData("KF436C17BBAK2/16", "Kingston FURY RGB")]
    public void Recognizes_rgb_memory(string part, string line) => Assert.Equal(line, MemoryRgbHints.Describe(part));

    [Theory]
    [InlineData("CT16G4DFRA32A.C8FE")]
    [InlineData("CMK16GX4M2B3200C16")]
    [InlineData("KF432C16BB/16")]
    [InlineData("")]
    public void Does_not_guess_for_plain_memory(string part) => Assert.Null(MemoryRgbHints.Describe(part));
}

/// <summary>Reproduces the MSI MPG B550 GAMING PLUS PC: OpenRGB not elevated, Mystic Light not exposed, RGB RAM hidden.</summary>
public class MsiBoardClassificationTests
{
    private static HardwareInventory MsiPc() => new()
    {
        System = new SystemSummary
        {
            BoardManufacturer = "Micro-Star International Co., Ltd.",
            BoardProduct = "MPG B550 GAMING PLUS (MS-7C56)",
            BoardVersion = "1.0",
            Memory = Enumerable.Range(1, 4).Select(i => new MemoryModule(null, "CMH32GX4M2D3600C18", 16UL << 30, $"DIMM{i}", 3600)).ToList(),
        },
        Devices =
        [
            new InventoryDevice
            {
                InstanceId = @"HID\VID_1462&PID_7C56\1",
                Name = "MYSTIC LIGHT",
                HardwareIds = [@"HID\VID_1462&PID_7C56"],
                Identity = HardwareIdParser.Parse(@"HID\VID_1462&PID_7C56"),
                Category = InventoryCategory.Hid,
            },
        ],
    };

    private static readonly CompatibilityRecord Msi = new() { Id = "msi", Manufacturer = "MSI", VendorId = "1462", Category = "Motherboard / Controller" };

    private static IReadOnlyList<DeviceEntry> Classify(BackendFindings findings, params RunningConflict[] conflicts) =>
        DeviceClassifier.Classify(MsiPc(), [], new EnvironmentState
        {
            BackendConnected = true,
            PawnIoInstalled = true,
            Findings = findings,
            BackendElevated = false,
            Conflicts = conflicts,
        }, [], id => id.VendorId == "1462" ? Msi : null);

    [Fact]
    public void Board_and_its_mystic_light_controller_are_one_entry_with_a_specific_reason()
    {
        var entries = Classify(new BackendFindings { SmbusUnavailable = true, MsiMysticLight = BoardControllerIssue.NoResponse });

        var board = Assert.Single(entries, e => e.Category == "Motherboard");
        Assert.Equal(DeviceStatus.SetupRequired, board.Status);
        Assert.Equal("1462:7C56", board.HardwareKey);
        Assert.Contains("did not answer OpenRGB", board.StatusDetail);
        Assert.Contains("MSI Center", board.NextStep);
        Assert.DoesNotContain(entries, e => e.Name == "MYSTIC LIGHT");
    }

    [Fact]
    public void Rgb_memory_hidden_by_missing_admin_rights_is_explained()
    {
        var dram = Assert.Single(Classify(new BackendFindings { SmbusUnavailable = true }), e => e.Category == "DRAM");
        Assert.Equal(DeviceStatus.SetupRequired, dram.Status);
        Assert.Contains("4 module(s), 64 GB", dram.StatusDetail);
        Assert.Contains("Corsair Vengeance RGB", dram.StatusDetail);
        Assert.Contains("not running as administrator", dram.StatusDetail);
        Assert.Contains("Run OpenRGB as administrator", dram.NextStep);
        Assert.DoesNotContain("may not have RGB", dram.StatusDetail);
    }

    [Fact]
    public void Old_mystic_light_firmware_points_to_msis_led_firmware_update()
    {
        var board = Classify(new BackendFindings { MsiMysticLight = BoardControllerIssue.FirmwareTooOld }).Single(e => e.Category == "Motherboard");
        Assert.Equal(DeviceStatus.SetupRequired, board.Status);
        Assert.Contains("fans", board.StatusDetail);
        Assert.Contains("Support → Live Update", board.NextStep);
        Assert.Contains("LED FW Update", board.NextStep);
        Assert.StartsWith("https://download.msi.com/", board.HelpUrl);
    }

    [Fact]
    public void Old_firmware_is_reported_even_while_an_msi_app_is_running()
    {
        var board = Classify(new BackendFindings { MsiMysticLight = BoardControllerIssue.FirmwareTooOld },
                new RunningConflict("MSI Mystic Light", "LEDKeeper2", ["1462"]))
            .Single(e => e.Category == "Motherboard");
        Assert.Equal(DeviceStatus.SetupRequired, board.Status);
        Assert.Contains("firmware", board.StatusDetail);
    }

    [Fact]
    public void A_device_seen_under_a_new_id_is_not_reported_offline()
    {
        var keyboard = TestData.Device(TestData.Keyboard());
        var ghost = new UniversalRGB.Core.Storage.KnownDevice("openrgb:old-id-with-device-path", keyboard.Name, "Keyboard", "OpenRGB",
            keyboard.Identity!.Key, DateTimeOffset.Now.AddHours(-1), DateTimeOffset.Now.AddHours(-1));
        var entries = DeviceClassifier.Classify(null, [keyboard], new EnvironmentState { BackendConnected = true }, [ghost], _ => null);
        Assert.DoesNotContain(entries, e => e.Status == DeviceStatus.Offline);
    }

    [Fact]
    public void Running_mystic_light_is_reported_as_the_conflict()
    {
        var board = Classify(new BackendFindings(), new RunningConflict("MSI Mystic Light", "LEDKeeper2", ["1462"]))
            .Single(e => e.Category == "Motherboard");
        Assert.Equal(DeviceStatus.ConflictDetected, board.Status);
        Assert.Contains("MSI Mystic Light is running (LEDKeeper2)", board.StatusDetail);
    }

    [Fact]
    public void Exposed_mystic_light_covers_the_board()
    {
        var controller = OpenRgbAdapter.ToDevice("openrgb:msi", TestData.Motherboard() with
        {
            Name = "MSI MPG B550 GAMING PLUS (MS-7C56)",
            Location = @"HID: \\?\hid#vid_1462&pid_7c56#7&abc",
        });
        var entries = DeviceClassifier.Classify(MsiPc(), [controller], new EnvironmentState { BackendConnected = true, PawnIoInstalled = true }, [],
            id => id.VendorId == "1462" ? Msi : null);
        Assert.Single(entries, e => e.Category == "Motherboard");
        Assert.Equal(DeviceStatus.Ready, entries.Single(e => e.Category == "Motherboard").Status);
    }

    [Theory]
    [InlineData("MPG B550 GAMING PLUS (MS-7C56)", "7C56")]
    [InlineData("MAG B650 TOMAHAWK WIFI (MS-7D75)", "7D75")]
    [InlineData("PRIME B550M-A WIFI II", null)]
    public void Reads_msi_board_code(string product, string? code) => Assert.Equal(code, DeviceClassifier.MsiBoardCode(product));
}
