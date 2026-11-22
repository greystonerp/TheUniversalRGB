namespace UniversalRGB.Core.Inventory;

/// <summary>
/// Recognizes common RGB memory product lines from their part numbers. Used only to word diagnostics
/// ("these look like RGB modules"); real support is still decided by the backend.
/// </summary>
public static class MemoryRgbHints
{
    private static readonly (string Pattern, bool Prefix, string Line)[] Known =
    [
        ("CMW", true, "Corsair Vengeance RGB PRO"),
        ("CMH", true, "Corsair Vengeance RGB"),
        ("CMG", true, "Corsair Vengeance RGB RS"),
        ("CMR", true, "Corsair Vengeance RGB"),
        ("CMT", true, "Corsair Dominator Platinum RGB"),
        ("GTZR", false, "G.Skill Trident Z RGB"),
        ("GTZN", false, "G.Skill Trident Z Neo"),
        ("GTRS", false, "G.Skill Trident Z Royal"),
        ("GTRG", false, "G.Skill Trident Z Royal"),
        ("TZ5R", false, "G.Skill Trident Z5 RGB"),
        ("TZ5NR", false, "G.Skill Trident Z5 Neo RGB"),
        ("TF3D", true, "T-Force Delta RGB"),
        ("FF3D", true, "T-Force Delta RGB"),
    ];

    /// <summary>Returns a product line name when the part number looks like RGB memory, otherwise null.</summary>
    public static string? Describe(string? partNumber)
    {
        if (string.IsNullOrWhiteSpace(partNumber))
        {
            return null;
        }

        var part = partNumber.Trim().ToUpperInvariant();
        foreach (var (pattern, prefix, line) in Known)
        {
            if (prefix ? part.StartsWith(pattern, StringComparison.Ordinal) : part.Contains(pattern, StringComparison.Ordinal))
            {
                return line;
            }
        }

        // Kingston FURY Beast RGB (…BBA…) and Renegade RGB (…RSA…), e.g. KF436C17BBAK2/16.
        if (part.StartsWith("KF", StringComparison.Ordinal) && (part.Contains("BBA", StringComparison.Ordinal) || part.Contains("RSA", StringComparison.Ordinal)))
        {
            return "Kingston FURY RGB";
        }

        return null;
    }
}
