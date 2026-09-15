namespace CielCraft.Core.Rotations;

/// <summary>
/// The game's HQ chance for a quality percentage (roadmap 7.18). Same
/// 101-entry table the Teamcraft simulator and raphael-data use; indexed by
/// the floored quality percent, so 0% quality is still a 1% chance.
/// </summary>
public static class HqChance
{
    private static readonly byte[] Table =
    [
        1, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 6, 6, 6, 6, 7, 7, 7, 7, 8, 8, 8,
        9, 9, 9, 10, 10, 10, 11, 11, 11, 12, 12, 12, 13, 13, 13, 14, 14, 14, 15, 15, 15, 16, 16, 17,
        17, 17, 18, 18, 18, 19, 19, 20, 20, 21, 22, 23, 24, 26, 28, 31, 34, 38, 42, 47, 52, 58, 64, 68,
        71, 74, 76, 78, 80, 81, 82, 83, 84, 85, 86, 87, 88, 89, 90, 91, 92, 94, 96, 98, 100,
    ];

    /// <summary>HQ chance in percent for a quality percentage (clamped to 0..100).</summary>
    public static int ForQualityPercent(int qualityPercent) =>
        Table[System.Math.Clamp(qualityPercent, 0, 100)];

    /// <summary>HQ chance in percent for a quality against the recipe maximum; 100 when the maximum is 0.</summary>
    public static int For(int quality, int maxQuality)
    {
        if (maxQuality <= 0)
            return 100;

        return ForQualityPercent((int)System.Math.Min(100, (long)System.Math.Max(quality, 0) * 100 / maxQuality));
    }
}
