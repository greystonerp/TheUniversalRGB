using UniversalRGB.Core.Compatibility;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Inventory;
using UniversalRGB.Core.Storage;
using UniversalRGB.Drivers.Detection;
using UniversalRGB.Drivers.Installation;
using UniversalRGB.Drivers.Verification;

namespace UniversalRGB.Tests;

public class DeviceClassifierTests
{
    private static readonly CompatibilityRecord Razer = new() { Id = "razer", Manufacturer = "Razer", VendorId = "1532", Category = "Peripheral" };

    private static CompatibilityRecord? Match(HardwareIdentity id) => id.VendorId == "1532" ? Razer : null;

    private static HardwareInventory Inventory(params InventoryDevice[] devices) => new()
    {
        System = new SystemSummary
        {
            BoardManufacturer = "ASUSTeK COMPUTER INC.",
            BoardProduct = "PRIME B550M-A WIFI II",
            Memory = [new MemoryModule("Crucial", "CT16G4DFRA32A", 16UL << 30, "DIMM1", 3200), new MemoryModule("Crucial", "CT16G4DFRA32A", 16UL << 30, "DIMM2", 3200)],
        },
        Devices = devices,
    };

    private static InventoryDevice Usb(string name, string hwid, int problem = 0, string? busName = null) => new()
    {
        InstanceId = hwid + "\\1",
        Name = name,
        BusReportedName = busName,
        HardwareIds = [hwid],
        ProblemCode = problem,
        Identity = HardwareIdParser.Parse(hwid),
        Category = InventoryCategory.Usb,
    };

    [Fact]
    public void Controllers_are_ready_and_cover_their_components()
    {
        var board = TestData.Device(TestData.Motherboard(argbLeds: 10));
        var env = new EnvironmentState { BackendConnected = true, PawnIoInstalled = true };

        var entries = DeviceClassifier.Classify(Inventory(), [board], env, [], Match);

        var entry = Assert.Single(entries, e => e.Controller is not null);
        Assert.Equal(DeviceStatus.Ready, entry.Status);
        Assert.Contains("Direct", entry.StatusDetail);
        Assert.DoesNotContain(entries, e => e.Category == "Motherboard" && e.Controller is null);
    }

    [Fact]
    public void Running_vendor_software_is_a_conflict()
    {
        var keyboard = TestData.Device(TestData.Keyboard());
        var env = new EnvironmentState { BackendConnected = true, Conflicts = [new RunningConflict("Razer Synapse", "RazerAppEngine", ["1532"])] };

        var entry = DeviceClassifier.Classify(null, [keyboard], env, [], Match).Single();

        Assert.Equal(DeviceStatus.ConflictDetected, entry.Status);
        Assert.Contains("never closes", entry.NextStep);
    }

    [Fact]
    public void Unconfigured_argb_headers_are_flagged()
    {
        var board = TestData.Device(TestData.Motherboard(argbLeds: 0));
        var entry = DeviceClassifier.Classify(null, [board], new EnvironmentState { BackendConnected = true }, [], Match).Single();
        Assert.Equal(DeviceStatus.Ready, entry.Status);
        Assert.Contains("LED count not set", entry.StatusDetail);
        Assert.NotNull(entry.NextStep);
    }

    [Fact]
    public void Memory_without_pawnio_needs_a_driver_and_is_grouped()
    {
        var entries = DeviceClassifier.Classify(Inventory(), [], new EnvironmentState { BackendConnected = true, PawnIoInstalled = false }, [], Match);
        var dram = Assert.Single(entries, e => e.Category == "DRAM");
        Assert.Equal(DeviceStatus.DriverRequired, dram.Status);
        Assert.Contains("2 module(s), 32 GB", dram.StatusDetail);
        Assert.Contains("Only relevant if these modules have RGB", dram.StatusDetail);
    }

    [Fact]
    public void Backend_offline_means_setup_required()
    {
        var entries = DeviceClassifier.Classify(Inventory(Usb("HID Keyboard Device", @"USB\VID_1532&PID_0294", busName: "Razer Ornata V3 X")),
            [], new EnvironmentState { BackendConnected = false }, [], Match);
        var razer = Assert.Single(entries, e => e.Manufacturer == "Razer");
        Assert.Equal(DeviceStatus.SetupRequired, razer.Status);
        Assert.Equal("Razer Ornata V3 X", razer.Name);
        Assert.All(entries, e => Assert.NotEqual(DeviceStatus.Ready, e.Status));
    }

    [Fact]
    public void Windows_driver_problems_are_reported()
    {
        var entries = DeviceClassifier.Classify(Inventory(Usb("Razer device", @"USB\VID_1532&PID_0001", problem: 28)),
            [], new EnvironmentState { BackendConnected = true }, [], Match);
        var razer = Assert.Single(entries, e => e.Manufacturer == "Razer");
        Assert.Equal(DeviceStatus.DriverRequired, razer.Status);
        Assert.Contains("code 28", razer.StatusDetail);
    }

    [Fact]
    public void Unknown_vendors_are_not_listed_and_recognized_ones_are_unsupported_when_not_exposed()
    {
        var entries = DeviceClassifier.Classify(Inventory(Usb("Mouse", @"USB\VID_1234&PID_0001"), Usb("Razer Kraken", @"USB\VID_1532&PID_0527")),
            [], new EnvironmentState { BackendConnected = true, PawnIoInstalled = true }, [], Match);
        Assert.DoesNotContain(entries, e => e.HardwareKey == "1234:0001");
        Assert.Equal(DeviceStatus.Unsupported, entries.Single(e => e.HardwareKey == "1532:0527").Status);
    }

    [Fact]
    public void Previously_seen_devices_are_offline()
    {
        var known = new KnownDevice("openrgb:gone", "Old Strip", "LedStrip", "OpenRGB", null, DateTimeOffset.Now.AddDays(-2), DateTimeOffset.Now.AddDays(-1));
        var entry = DeviceClassifier.Classify(null, [], new EnvironmentState { BackendConnected = true }, [known], Match).Single();
        Assert.Equal(DeviceStatus.Offline, entry.Status);
    }
}

public class AppDatabaseTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "urgb-db-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Fact]
    public void Exact_product_match_beats_vendor_family()
    {
        var db = new AppDatabase(_path);
        db.ReplaceCompatibility(
        [
            new CompatibilityRecord { Id = "family", Manufacturer = "ASUS", VendorId = "0B05" },
            new CompatibilityRecord { Id = "exact", Manufacturer = "ASUS", Model = "Aura", VendorId = "0B05", ProductId = "19AF", Status = "tested", Verified = true },
        ]);

        Assert.Equal("exact", db.Match(new HardwareIdentity("USB", "0B05", "19AF"))!.Id);
        Assert.Equal("family", db.Match(new HardwareIdentity("USB", "0B05", "1111"))!.Id);
        Assert.Null(db.Match(new HardwareIdentity("USB", "FFFF", "0001")));
        Assert.True(db.GetCompatibility().Single(r => r.Id == "exact").Verified);
    }

    [Fact]
    public void Known_devices_and_history_persist()
    {
        var db = new AppDatabase(_path);
        db.RecordSeen([TestData.Device(TestData.Keyboard())]);
        db.RecordSeen([TestData.Device(TestData.Keyboard())]);
        var known = Assert.Single(db.GetKnownDevices());
        Assert.Equal("1532:0294", known.HardwareKey);

        db.AddInstallRecord(new InstallRecord(0, DateTimeOffset.Now, "PawnIO", @"C:\x.exe", "AB", "Signer", true, 0, "Succeeded", "ok"));
        var record = Assert.Single(db.GetInstallHistory());
        Assert.Equal("PawnIO", record.PackageName);
        Assert.True(record.SignatureValid);
    }

    [Fact]
    public void Shipped_manifests_load_and_seed()
    {
        var compat = CompatibilityManifest.Load(Path.Combine(AppContext.BaseDirectory, "manifests", "compatibility.json"));
        Assert.All(compat.Records, r => Assert.False(r.Verified));
        Assert.Equal(compat.Records.Count, compat.Records.Select(r => r.VendorId).Distinct().Count());

        var db = new AppDatabase(_path);
        db.ReplaceCompatibility(compat.Records);
        Assert.Equal("Razer", db.Match(new HardwareIdentity("USB", "1532", "0294"))!.Manufacturer);

        var drivers = DriverManifest.Load(Path.Combine(AppContext.BaseDirectory, "manifests", "drivers.json"));
        var openRgb = drivers.Find("openrgb")!;
        Assert.True(openRgb.CanDownload);
        Assert.True(openRgb.AutoInstall);
        Assert.Equal("zip", openRgb.Format);
        Assert.StartsWith("https://codeberg.org/OpenRGB/", openRgb.DownloadUrl);

        var pawnIo = drivers.Find("pawnio")!;
        Assert.True(pawnIo.RequiresAdministrator);
        Assert.False(pawnIo.AutoInstall);
        Assert.Contains("namazso.eu", pawnIo.ExpectedSigners);
        Assert.StartsWith("https://github.com/namazso/", pawnIo.DownloadUrl);
        Assert.Equal("pawnio", drivers.GuessFromFileName(@"C:\Downloads\PawnIO_setup.exe")!.Id);
    }

    [Fact]
    public void Manifest_rejects_unsafe_packages()
    {
        const string head = """{ "format": "UniversalRGB.Drivers", "packages": [ { "id": "x", "name": "X", "publisher": "p", "purpose": "p", "officialPage": "https://example.org/", """;
        Assert.Throws<InvalidDataException>(() => DriverManifest.Parse(head + """ "downloadUrl": "http://example.org/x.zip", "sha256": ["00"] } ] }"""));
        Assert.Throws<InvalidDataException>(() => DriverManifest.Parse(head + """ "downloadUrl": "https://example.org/x.zip" } ] }"""));
        Assert.Throws<InvalidDataException>(() => DriverManifest.Parse(head + """ "format": "exe", "kind": "kernel-driver", "autoInstall": true } ] }"""));
    }
}

public class RequirementInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "urgb-req-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private sealed class FakeHandler(byte[] body) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(body),
            });
        }
    }

    private static byte[] MakeZip()
    {
        using var memory = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(memory, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var name in new[] { "Tool Windows 64-bit/Tool.exe", "Tool Windows 64-bit/lib.dll" })
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(name);
            }
        }

        return memory.ToArray();
    }

    private DriverPackage Package(byte[] body, string? sha = null) => new()
    {
        Id = "tool",
        Name = "Tool",
        Version = "1.0",
        Publisher = "p",
        Purpose = "p",
        OfficialPage = "https://example.org/",
        DownloadUrl = "https://example.org/Tool.zip",
        Sha256 = [sha ?? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(body))],
        Format = "zip",
        ZipExecutable = "Tool Windows 64-bit/Tool.exe",
        InstallDirectory = Path.Combine(_root, "Programs", "Tool"),
        AutoInstall = true,
    };

    [Fact]
    public async Task Verified_zip_is_downloaded_cached_and_unpacked_flat()
    {
        var body = MakeZip();
        var handler = new FakeHandler(body);
        var db = new AppDatabase(Path.Combine(_root, "db.sqlite"));
        using var installer = new RequirementInstaller(db, Path.Combine(_root, "cache"), handler: handler);
        var package = Package(body);

        var zip = await installer.DownloadVerifiedAsync(package, null, CancellationToken.None);
        await installer.DownloadVerifiedAsync(package, null, CancellationToken.None);
        Assert.Equal(1, handler.Requests); // second call reuses the verified cached copy

        var exe = await installer.InstallPortableAsync(package, zip, CancellationToken.None);
        Assert.Equal(Path.Combine(_root, "Programs", "Tool", "Tool.exe"), exe);
        Assert.True(File.Exists(exe));
        Assert.True(File.Exists(Path.Combine(_root, "Programs", "Tool", "lib.dll")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Programs", "Tool.staging")));
        Assert.Equal("Tool 1.0", db.GetInstallHistory().Single().PackageName);

        // Re-installing replaces the previous copy.
        Assert.Equal(exe, await installer.InstallPortableAsync(package, zip, CancellationToken.None));
    }

    [Fact]
    public async Task Hash_mismatch_is_rejected_and_nothing_is_kept()
    {
        var body = MakeZip();
        var db = new AppDatabase(Path.Combine(_root, "db.sqlite"));
        using var installer = new RequirementInstaller(db, Path.Combine(_root, "cache"), handler: new FakeHandler(body));
        var package = Package(body, sha: new string('0', 64));

        var error = await Assert.ThrowsAsync<InvalidDataException>(() => installer.DownloadVerifiedAsync(package, null, CancellationToken.None));
        Assert.Contains("does not match the pinned SHA-256", error.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, "cache")));
        Assert.False(Directory.Exists(Path.Combine(_root, "Programs")));
    }

    [Fact]
    public async Task Non_https_or_unpinned_downloads_are_refused()
    {
        var body = MakeZip();
        var db = new AppDatabase(Path.Combine(_root, "db.sqlite"));
        using var installer = new RequirementInstaller(db, Path.Combine(_root, "cache"), handler: new FakeHandler(body));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.DownloadVerifiedAsync(Package(body) with { DownloadUrl = "http://example.org/Tool.zip" }, null, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            installer.DownloadVerifiedAsync(Package(body) with { Sha256 = [] }, null, CancellationToken.None));
    }
}

public class DriverSafetyTests
{
    [Fact]
    public async Task Unsigned_files_are_never_installable()
    {
        var path = Path.Combine(Path.GetTempPath(), "urgb-unsigned-" + Guid.NewGuid().ToString("N") + ".exe");
        await File.WriteAllBytesAsync(path, [0x4D, 0x5A, 0, 0, 1, 2, 3]);
        try
        {
            var result = await PackageVerifier.VerifyAsync(path, null);
            Assert.False(result.CanInstall);
            Assert.NotEqual(SignatureState.Valid, result.Signature);
            Assert.Equal(64, result.Sha256.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Signed_file_passes_signature_but_pinned_hash_and_signer_are_enforced()
    {
        var dotnet = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet", "dotnet.exe");
        if (!File.Exists(dotnet))
        {
            return; // Environment without an embedded-signed reference file.
        }

        var open = await PackageVerifier.VerifyAsync(dotnet, null);
        Assert.Equal(SignatureState.Valid, open.Signature);
        Assert.False(string.IsNullOrWhiteSpace(open.SignerSubject));
        Assert.Contains("Microsoft", open.SignerIssuer);

        var pinned = new DriverPackage { Id = "x", Name = "X", Publisher = "p", Purpose = "p", OfficialPage = "https://example.org/", Sha256 = ["00"] };
        Assert.False((await PackageVerifier.VerifyAsync(dotnet, pinned)).CanInstall);

        var wrongSigner = pinned with { Sha256 = [], ExpectedSigners = ["Some Other Vendor"] };
        Assert.False((await PackageVerifier.VerifyAsync(dotnet, wrongSigner)).CanInstall);

        var notInstallable = pinned with { Sha256 = [], Installable = false };
        Assert.False((await PackageVerifier.VerifyAsync(dotnet, notInstallable)).CanInstall);

        var good = pinned with { Sha256 = [open.Sha256], ExpectedSigners = [open.SignerSubject!] };
        var verified = await PackageVerifier.VerifyAsync(dotnet, good);
        Assert.True(verified.CanInstall);
        Assert.True(verified.HashPinnedMatch);
    }

    [Fact]
    public async Task Installer_refuses_without_consent_and_records_history()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "urgb-db-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var db = new AppDatabase(dbPath);
            var installer = new PackageInstaller(db, Path.GetTempPath());
            var verification = new PackageVerification { FilePath = @"C:\nowhere.exe", Sha256 = new string('A', 64), Signature = SignatureState.Valid };

            var outcome = await installer.InstallAsync(verification, userApproved: false, CancellationToken.None);

            Assert.Equal(InstallResult.Cancelled, outcome.Result);
            Assert.Equal("Cancelled", db.GetInstallHistory().Single().Result);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            foreach (var f in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
            {
                File.Delete(f);
            }
        }
    }

    [Fact]
    public void Conflict_matching_is_case_insensitive_and_vendor_scoped()
    {
        var running = new HashSet<string>(["icue", "explorer", "SignalRgb"], StringComparer.OrdinalIgnoreCase);
        var conflicts = ConflictDetector.Match(running);
        Assert.Contains(conflicts, c => c.DisplayName == "Corsair iCUE" && c.Affects("1B1C") && !c.Affects("1532"));
        Assert.Contains(conflicts, c => c.DisplayName == "SignalRGB" && c.Affects("1532"));
    }

    [Fact]
    public void Authenticode_codes_are_interpreted()
    {
        Assert.Equal(SignatureState.NotSigned, AuthenticodeVerifier.Interpret(0x800B0100).Item1);
        Assert.Equal(SignatureState.Invalid, AuthenticodeVerifier.Interpret(0x80096010).Item1);
        Assert.Equal(SignatureState.Valid, AuthenticodeVerifier.Interpret(0).Item1);
    }
}

public class WindowsInventorySmokeTests
{
    [Fact]
    public void Enumerates_this_pc_read_only()
    {
        var inventory = new UniversalRGB.Hardware.Detection.HardwareInventoryService().Scan();
        Assert.NotNull(inventory.System);
        Assert.All(inventory.Devices, d => Assert.NotEqual(InventoryCategory.Other, d.Category));
    }
}
