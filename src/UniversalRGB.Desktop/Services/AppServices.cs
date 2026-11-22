using System.Reflection;
using UniversalRGB.Adapters.OpenRGB;
using UniversalRGB.Core.Compatibility;
using UniversalRGB.Core.Devices;
using UniversalRGB.Core.Diagnostics;
using UniversalRGB.Core.Lighting;
using UniversalRGB.Core.Profiles;
using UniversalRGB.Core.Storage;
using UniversalRGB.Drivers.Installation;
using UniversalRGB.Hardware.Detection;
using UniversalRGB.Hardware.Sensors;

namespace UniversalRGB.Desktop.Services;

/// <summary>Composition root: builds and owns every non-UI service.</summary>
public sealed class AppServices : IAsyncDisposable
{
    public AppServices(IAppLog log, AppPaths paths)
    {
        Log = log;
        Paths = paths;
        paths.EnsureCreated();

        SettingsStore = new SettingsStore(paths.SettingsFile);
        Settings = SettingsStore.Load();
        Database = new AppDatabase(paths.Database);
        Profiles = new ProfileStore(paths.Profiles);

        Drivers = DriverManifest.Parse(ReadManifest("drivers.json"));
        Compatibility = CompatibilityManifest.Parse(ReadManifest("compatibility.json")).Records;
        Database.ReplaceCompatibility(Compatibility);

        Registry = new DeviceRegistry();
        OpenRgb = new OpenRgbAdapter(() => (Settings.OpenRgbHost, Settings.OpenRgbPort), log);
        Registry.AddAdapter(OpenRgb);

        Inventory = new HardwareInventoryService();
        Temperature = new GpuTemperatureSource(log);
        Audio = new AudioLevelSource(log);
        Engine = new LightingEngine(Registry, Temperature, Audio, log) { FrameRate = Settings.FrameRate };
        Installer = new PackageInstaller(Database, paths.Packages, log);
        Requirements = new RequirementInstaller(Database, paths.Packages, log);
    }

    public RequirementInstaller Requirements { get; }

    public IAppLog Log { get; }
    public AppPaths Paths { get; }
    public SettingsStore SettingsStore { get; }
    public AppSettings Settings { get; }
    public AppDatabase Database { get; }
    public ProfileStore Profiles { get; }
    public DriverManifest Drivers { get; }
    public IReadOnlyList<CompatibilityRecord> Compatibility { get; }
    public DeviceRegistry Registry { get; }
    public OpenRgbAdapter OpenRgb { get; }
    public HardwareInventoryService Inventory { get; }
    public GpuTemperatureSource Temperature { get; }
    public AudioLevelSource Audio { get; }
    public LightingEngine Engine { get; }
    public PackageInstaller Installer { get; }

    public void SaveSettings()
    {
        try
        {
            SettingsStore.Save(Settings);
        }
        catch (IOException ex)
        {
            Log.Error("Could not save settings.", ex);
        }
    }

    /// <summary>Reads a manifest from the folder next to the EXE (updatable), falling back to the embedded copy.</summary>
    private static string ReadManifest(string name)
    {
        var onDisk = Path.Combine(AppContext.BaseDirectory, "manifests", name);
        if (File.Exists(onDisk))
        {
            return File.ReadAllText(onDisk);
        }

        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("manifests." + name)
            ?? throw new FileNotFoundException($"Manifest '{name}' is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        await Engine.DisposeAsync();
        Temperature.Dispose();
        Audio.Dispose();
        Requirements.Dispose();
        await OpenRgb.DisposeAsync();
    }
}
