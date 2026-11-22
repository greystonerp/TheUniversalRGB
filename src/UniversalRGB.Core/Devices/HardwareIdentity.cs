using System.Text.RegularExpressions;

namespace UniversalRGB.Core.Devices;

/// <summary>Bus identity parsed from a Windows hardware ID or an RGB backend location string.</summary>
public sealed record HardwareIdentity(string Bus, string? VendorId, string? ProductId, string? Revision = null, string? SubsystemId = null)
{
    /// <summary>"VID:PID" in upper-case hex, or null when either part is unknown.</summary>
    public string? Key => VendorId is null || ProductId is null ? null : $"{VendorId}:{ProductId}";

    public override string ToString() => Key is null ? Bus : $"{Bus} {Key}";
}

public static partial class HardwareIdParser
{
    [GeneratedRegex(@"VID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbVid();

    [GeneratedRegex(@"PID_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex UsbPid();

    [GeneratedRegex(@"REV_([0-9A-F]{2,4})", RegexOptions.IgnoreCase)]
    private static partial Regex Rev();

    [GeneratedRegex(@"VEN_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PciVen();

    [GeneratedRegex(@"DEV_([0-9A-F]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex PciDev();

    [GeneratedRegex(@"SUBSYS_([0-9A-F]{8})", RegexOptions.IgnoreCase)]
    private static partial Regex PciSubsys();

    /// <summary>
    /// Parses identifiers such as "USB\VID_0B05&amp;PID_19AF&amp;REV_0100", "HID\VID_1532&amp;PID_02A5&amp;MI_00",
    /// "PCI\VEN_10DE&amp;DEV_1C02&amp;SUBSYS_85B11043" or OpenRGB locations like "HID: \\?\hid#vid_0b05&amp;pid_19af...".
    /// </summary>
    public static HardwareIdentity? Parse(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var vid = UsbVid().Match(id);
        var pid = UsbPid().Match(id);
        if (vid.Success)
        {
            var bus = id.StartsWith("HID", StringComparison.OrdinalIgnoreCase) || id.Contains("hid#", StringComparison.OrdinalIgnoreCase)
                ? "HID"
                : "USB";
            var rev = Rev().Match(id);
            return new HardwareIdentity(
                bus,
                vid.Groups[1].Value.ToUpperInvariant(),
                pid.Success ? pid.Groups[1].Value.ToUpperInvariant() : null,
                rev.Success ? rev.Groups[1].Value.ToUpperInvariant() : null);
        }

        var ven = PciVen().Match(id);
        if (ven.Success)
        {
            var dev = PciDev().Match(id);
            var sub = PciSubsys().Match(id);
            var rev = Rev().Match(id);
            return new HardwareIdentity(
                "PCI",
                ven.Groups[1].Value.ToUpperInvariant(),
                dev.Success ? dev.Groups[1].Value.ToUpperInvariant() : null,
                rev.Success ? rev.Groups[1].Value.ToUpperInvariant() : null,
                sub.Success ? sub.Groups[1].Value.ToUpperInvariant() : null);
        }

        return null;
    }

    /// <summary>Returns the most specific identity found in a list of hardware IDs.</summary>
    public static HardwareIdentity? ParseBest(IEnumerable<string> ids)
    {
        HardwareIdentity? best = null;
        foreach (var id in ids)
        {
            var parsed = Parse(id);
            if (parsed is null)
            {
                continue;
            }

            if (best is null || Score(parsed) > Score(best))
            {
                best = parsed;
            }
        }

        return best;

        static int Score(HardwareIdentity h) =>
            (h.VendorId is null ? 0 : 1) + (h.ProductId is null ? 0 : 2) + (h.Revision is null ? 0 : 1) + (h.SubsystemId is null ? 0 : 1);
    }
}
