using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace PartySorter;

[Serializable]
public class SavedGroup
{
    public string? Label { get; set; }
    public List<ulong> OrderedContentIds { get; set; } = new();

    /// <summary>
    /// Names of the members in the same order as <see cref="OrderedContentIds"/>.
    /// Captured at save time so the saved-groups window can display who is in a
    /// group without having to be in a party with them. Older saves may have an
    /// empty list — in that case the UI falls back to the member count.
    /// </summary>
    public List<string> OrderedMemberNames { get; set; } = new();

    /// <summary>
    /// ClassJob.RowId for each member in the same order as <see cref="OrderedMemberNames"/>.
    /// 0 = unknown. Used to display role badges (T/H/D) in the saved-groups popup.
    /// Older saves will have an empty list; the UI gracefully omits role badges.
    /// </summary>
    public List<uint> OrderedMemberJobIds { get; set; } = new();

    public DateTime LastUsedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// IClientState.TerritoryType at the moment this order was last saved.
    /// 0 means the territory was not recorded (older save).
    /// </summary>
    public uint LastUsedTerritoryId { get; set; } = 0;

    /// <summary>
    /// Human-readable name of the ContentFinderCondition (duty) matching
    /// <see cref="LastUsedTerritoryId"/>, or null for open-world / not recorded.
    /// </summary>
    public string? LastUsedDutyName { get; set; }

    /// <summary>
    /// When true, this saved order cannot be overwritten by auto-save-on-drag or
    /// the "Save current order" button. Prevents accidental overwrites of a
    /// carefully curated static order. Auto-reapply still reads and applies a
    /// locked group normally — it only blocks writing.
    /// </summary>
    public bool Locked { get; set; } = false;

    /// <summary>
    /// How auto-reapply behaves for this specific saved group. Defaults to
    /// <see cref="AutoApplyMode.ByNames"/> so existing behaviour is preserved.
    /// </summary>
    public AutoApplyMode AutoApply { get; set; } = AutoApplyMode.ByNames;
}

/// <summary>Controls when and how the auto-reapply logic fires for a saved group.</summary>
public enum AutoApplyMode
{
    /// <summary>Restore order when the same members (by character ContentId) are present.</summary>
    ByNames = 0,

    /// <summary>Never auto-restore this saved group.</summary>
    Disabled = 1,

    /// <summary>
    /// Restore order by matching specific job — a WHM substitute fills the WHM
    /// position, a DRK substitute fills the DRK position, etc.
    /// Requires job IDs to have been recorded at save time.
    /// </summary>
    ByJob = 2,

    /// <summary>
    /// Strictest mode: same members AND each must be playing the same specific job
    /// as when the order was saved. Skips if anyone has changed jobs.
    /// </summary>
    NameAndJob = 3,
}

public enum DragModifier
{
    Shift,
    Ctrl,
    Alt,
}

/// <summary>Controls what happens when a party card is dropped onto another.</summary>
public enum DropBehavior
{
    /// <summary>The two members swap positions directly. (Default)</summary>
    Swap = 0,

    /// <summary>
    /// The dragged member is inserted at the drop position; every member between
    /// the source and target shifts one slot up or down to fill the gap.
    /// </summary>
    Shift = 1,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    // --- General ---
    public bool Enabled { get; set; } = true;
    public DragModifier Modifier { get; set; } = DragModifier.Ctrl;

    /// <summary>
    /// Whether dropping a card onto another swaps the two members directly or
    /// inserts the dragged member at the target position (shifting others).
    /// </summary>
    public DropBehavior DropBehavior { get; set; } = DropBehavior.Swap;

    // --- Saving ---
    /// <summary>
    /// When true, every successful drag automatically updates the saved order for this
    /// group — but only if a saved entry already exists AND it is not locked. Dragging
    /// with a party that has no saved entry does nothing; use "Save current order" to
    /// create the first entry. Off-by-default so casual reorders are not persisted.
    /// </summary>
    public bool AutoSaveOnDrag { get; set; } = false;

    // --- Auto-reapply ---
    public bool AutoReapplyEnabled { get; set; } = true;

    /// <summary>
    /// When true, auto-reapply only fires while inside a duty (BoundByDuty condition).
    /// Open-world parties and cross-world parties are ignored.
    /// </summary>
    public bool OnlyReapplyInInstance { get; set; } = false;

    /// <summary>
    /// When true, shows a toast notification each time auto-reapply successfully restores
    /// a saved order (e.g. "Order restored: Tuesday Static").
    /// </summary>
    public bool NotifyOnReapply { get; set; } = false;

    // --- Instance enter ---
    /// <summary>
    /// When true, opens the PartyDragSort config window automatically on duty enter.
    /// Useful for quickly checking or adjusting the saved group before pulls.
    /// </summary>
    public bool OpenConfigOnInstanceEnter { get; set; } = false;

    /// <summary>
    /// When true, the automatic window-open on duty enter only fires for high-end
    /// duties (Extreme trials, Savage raids, Ultimates, Criterion).  Uses FFXIV's
    /// own ContentFinderCondition.HighEndDuty flag so it updates automatically with
    /// new content — no plugin patch required.
    /// Has no effect when <see cref="OpenConfigOnInstanceEnter"/> is false.
    /// </summary>
    public bool InstanceEnterHighEndOnly { get; set; } = false;

    /// <summary>
    /// When true, shows a toast on duty enter indicating whether a saved order was found
    /// for the current group, or prompting the player to set one if not.
    /// </summary>
    public bool NotifyOnInstanceEnter { get; set; } = false;

    // --- Group identification ---
    /// <summary>
    /// When true, saved orders are keyed by both the member set AND the current
    /// territory/duty. The same group of people can have separate saved orders for
    /// different duties (e.g. different orderings for Eden vs. Coils).
    /// When false (default), the same order applies regardless of which duty you
    /// are in — matching the original behaviour and preserving older saves.
    ///
    /// NOTE: affects saves, lookups, the current-group highlight, and auto-reapply
    /// equally. Toggling it orphans existing saves until they are re-saved in a duty.
    /// </summary>
    public bool KeyByInstance { get; set; } = false;

    // --- Saved groups ---
    public Dictionary<string, SavedGroup> SavedOrders { get; set; } = new();

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi)
    {
        pluginInterface = pi;
    }

    public void Save()
    {
        pluginInterface?.SavePluginConfig(this);
    }
}
