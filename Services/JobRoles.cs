namespace PartySorter.Services;

/// <summary>
/// Maps FFXIV ClassJob row IDs to broad combat roles.
/// Shared between <see cref="PartySortController"/> (role-based matching) and
/// <see cref="PartyDragSort.Windows.SavedGroupsWindow"/> (role badge display).
/// </summary>
internal static class JobRoles
{
    public enum Role { Unknown, Tank, Healer, Dps }

    /// <summary>Returns the broad combat role for a ClassJob.RowId (0 = unknown/none).</summary>
    public static Role GetRole(uint jobId) => jobId switch
    {
        // Tanks: GLA(1), MRD(3), PLD(19), WAR(21), DRK(32), GNB(37)
        1 or 3 or 19 or 21 or 32 or 37 => Role.Tank,
        // Healers: CNJ(6), WHM(24), SCH(28), AST(33), SGE(40)
        6 or 24 or 28 or 33 or 40       => Role.Healer,
        // Adventurer, crafters(8–15), gatherers(16–18), or unset
        0 or (>= 8 and <= 18)           => Role.Unknown,
        // All other valid job IDs are DPS (melee, ranged physical, caster)
        _                               => Role.Dps,
    };
}
