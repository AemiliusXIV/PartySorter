using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace PartySorter.Services;

public sealed class PartySortController : IDisposable
{
    private const double ReapplyCheckIntervalSec = 2.0;
    private const double PostDispatchCooldownSec = 0.5;

    private readonly Configuration config;
    private readonly PartyReorderer reorderer;
    private readonly PartyAddonReader reader;

    private DateTime lastReapplyCheckUtc = DateTime.MinValue;
    private DateTime cooldownUntilUtc    = DateTime.MinValue;

    public PartySortController(Configuration config, PartyReorderer reorderer, PartyAddonReader reader)
    {
        this.config    = config;
        this.reorderer = reorderer;
        this.reader    = reader;
    }

    public string CurrentGroupKey { get; private set; } = string.Empty;
    public PartySnapshot? LastSnapshot { get; private set; }

    public void Tick()
    {
        if (!reader.TrySnapshot(out var snapshot) || snapshot is null)
        {
            LastSnapshot    = null;
            CurrentGroupKey = string.Empty;
            return;
        }

        LastSnapshot = snapshot;
        var territoryId = (uint)Plugin.ClientState.TerritoryType;
        CurrentGroupKey = ComputeGroupKey(snapshot, territoryId, config.KeyByInstance);

        if (!reorderer.CanDispatch)
        {
            cooldownUntilUtc = DateTime.UtcNow.AddSeconds(PostDispatchCooldownSec);
            return;
        }

        if (!config.AutoReapplyEnabled) return;
        if (snapshot.IsCrossWorld) return;
        if (snapshot.Slots.Count < 2) return;

        var now = DateTime.UtcNow;
        if (now < cooldownUntilUtc) return;
        if ((now - lastReapplyCheckUtc).TotalSeconds < ReapplyCheckIntervalSec) return;
        lastReapplyCheckUtc = now;

        if (!TryGetSavedGroup(out var saved)) return;

        // Per-group override — Disabled skips auto-apply entirely.
        if (saved.AutoApply == AutoApplyMode.Disabled) return;

        var label = string.IsNullOrWhiteSpace(saved.Label) ? "saved order" : saved.Label;

        // ── ByJob mode — match by specific job ID ────────────────────────────
        if (saved.AutoApply == AutoApplyMode.ByJob)
        {
            if (saved.OrderedContentIds.Count != snapshot.Slots.Count) return;
            if (saved.OrderedMemberJobIds.Count != saved.OrderedContentIds.Count)
            {
                Plugin.Log.Debug("PartySorter: ByJob skipped — no job IDs recorded for this group.");
                return;
            }
            if (!TryMatchByJob(snapshot, saved, out var jobOrder)) return;
            var curCids = snapshot.Slots.Select(s => s.ContentId).ToList();
            if (curCids.SequenceEqual(jobOrder)) return;
            reorderer.DispatchMovesToTarget(curCids, jobOrder);
            if (config.NotifyOnReapply)
                Plugin.ToastGui.ShowNormal($"PartySorter: Order restored — {label}.");
            return;
        }

        // ── ByNames / NameAndJob — require the exact same member set ─────────
        if (saved.OrderedContentIds.Count != snapshot.Slots.Count) return;

        var savedSet   = new HashSet<ulong>(saved.OrderedContentIds);
        var currentSet = new HashSet<ulong>(snapshot.Slots.Select(s => s.ContentId));
        if (!savedSet.SetEquals(currentSet)) return;

        // NameAndJob: additionally verify each member is playing the same specific job.
        if (saved.AutoApply == AutoApplyMode.NameAndJob &&
            saved.OrderedMemberJobIds.Count == saved.OrderedContentIds.Count)
        {
            var cidToJob = BuildCidToJobMap();
            for (var i = 0; i < saved.OrderedContentIds.Count; i++)
            {
                if (!cidToJob.TryGetValue(saved.OrderedContentIds[i], out var currentJob)) continue;
                if (currentJob != saved.OrderedMemberJobIds[i])
                    return; // A member swapped jobs since this order was saved — skip.
            }
        }

        var currentOrder = snapshot.Slots.Select(s => s.ContentId).ToList();
        if (currentOrder.SequenceEqual(saved.OrderedContentIds)) return;

        reorderer.DispatchMovesToTarget(currentOrder, saved.OrderedContentIds);

        if (config.NotifyOnReapply)
            Plugin.ToastGui.ShowNormal($"PartySorter: Order restored — {label}.");
    }

    public void HandleDrop(int sourceSlot, int targetSlot)
    {
        var snapshot = LastSnapshot;
        if (snapshot is null) return;
        if (snapshot.IsCrossWorld) return;
        if (sourceSlot == targetSlot) return;
        if (sourceSlot < 0 || targetSlot < 0) return;
        if (sourceSlot >= snapshot.Slots.Count || targetSlot >= snapshot.Slots.Count) return;

        if (sourceSlot == 0 || targetSlot == 0)
        {
            Plugin.ToastGui.ShowError("PartySorter: Slot 1 is locked to your character.");
            return;
        }

        var src = snapshot.Slots[sourceSlot];
        var dst = snapshot.Slots[targetSlot];
        if (src.IsLocalPlayer || dst.IsLocalPlayer)
        {
            Plugin.ToastGui.ShowError("PartySorter: Cannot move yourself.");
            return;
        }

        var currentCids  = snapshot.Slots.Select(s => s.ContentId).ToList();
        var desiredCids  = new List<ulong>(currentCids);
        var desiredNames = snapshot.Slots.Select(s => s.Name).ToList();
        var desiredJobs  = GetJobIdsForSlots(snapshot.Slots);

        if (config.DropBehavior == DropBehavior.Shift)
        {
            var item     = desiredCids[sourceSlot];
            var itemName = desiredNames[sourceSlot];
            var itemJob  = desiredJobs[sourceSlot];
            desiredCids.RemoveAt(sourceSlot);
            desiredNames.RemoveAt(sourceSlot);
            desiredJobs.RemoveAt(sourceSlot);
            desiredCids.Insert(targetSlot, item);
            desiredNames.Insert(targetSlot, itemName);
            desiredJobs.Insert(targetSlot, itemJob);
        }
        else // Swap (default)
        {
            (desiredCids[sourceSlot],  desiredCids[targetSlot])  = (desiredCids[targetSlot],  desiredCids[sourceSlot]);
            (desiredNames[sourceSlot], desiredNames[targetSlot]) = (desiredNames[targetSlot], desiredNames[sourceSlot]);
            (desiredJobs[sourceSlot],  desiredJobs[targetSlot])  = (desiredJobs[targetSlot],  desiredJobs[sourceSlot]);
        }

        reorderer.DispatchMovesToTarget(currentCids, desiredCids);

        // AutoSaveOnDrag: only update an existing, unlocked entry — never create a new one.
        if (config.AutoSaveOnDrag &&
            config.SavedOrders.TryGetValue(CurrentGroupKey, out var existingForDrag) &&
            !existingForDrag.Locked)
        {
            var territoryId = (uint)Plugin.ClientState.TerritoryType;
            existingForDrag.OrderedContentIds   = desiredCids;
            existingForDrag.OrderedMemberNames  = desiredNames;
            existingForDrag.OrderedMemberJobIds = desiredJobs;
            existingForDrag.LastUsedUtc         = DateTime.UtcNow;
            existingForDrag.LastUsedTerritoryId = territoryId;
            existingForDrag.LastUsedDutyName    = Plugin.GetDutyName(territoryId);
            config.Save();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Save / apply — public surface used by the UI and /save command
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Core save logic. Returns true and a concise result message on success,
    /// false and a reason on failure. Callers decide how to surface the message
    /// (toast for button clicks, chat for the /save command).
    /// </summary>
    public bool TrySaveCurrentOrder(out string message)
    {
        var snapshot = LastSnapshot;
        if (snapshot is null || string.IsNullOrEmpty(CurrentGroupKey))
        {
            message = "No live party detected.";
            return false;
        }

        if (config.SavedOrders.TryGetValue(CurrentGroupKey, out var check) && check.Locked)
        {
            message = "This group is locked — unlock it to save a new order.";
            return false;
        }

        var currentCids  = snapshot.Slots.Select(s => s.ContentId).ToList();
        var currentNames = snapshot.Slots.Select(s => s.Name).ToList();
        var currentJobs  = GetJobIdsForSlots(snapshot.Slots);
        var territoryId  = (uint)Plugin.ClientState.TerritoryType;

        var existingEntry = config.SavedOrders.TryGetValue(CurrentGroupKey, out var ex) ? ex : null;
        var isNew         = existingEntry is null;

        config.SavedOrders[CurrentGroupKey] = new SavedGroup
        {
            Label               = existingEntry?.Label,
            Locked              = existingEntry?.Locked ?? false,
            AutoApply           = existingEntry?.AutoApply ?? AutoApplyMode.ByNames,
            OrderedContentIds   = currentCids,
            OrderedMemberNames  = currentNames,
            OrderedMemberJobIds = currentJobs,
            LastUsedUtc         = DateTime.UtcNow,
            LastUsedTerritoryId = territoryId,
            LastUsedDutyName    = Plugin.GetDutyName(territoryId),
        };
        config.Save();

        var groupDesc = string.IsNullOrWhiteSpace(config.SavedOrders[CurrentGroupKey].Label)
            ? $"{currentNames.Count} members"
            : $"\"{config.SavedOrders[CurrentGroupKey].Label}\"";
        message = isNew
            ? $"New group saved ({groupDesc})."
            : $"Order updated ({groupDesc}).";
        return true;
    }

    /// <summary>Saves the current live order. Shows a toast on completion. Used by the UI button.</summary>
    public void SaveCurrentOrder()
    {
        if (TrySaveCurrentOrder(out var msg))
            Plugin.ToastGui.ShowNormal($"PartySorter: {msg}");
        else
            Plugin.ToastGui.ShowError($"PartySorter: {msg}");
    }

    /// <summary>
    /// Immediately applies the saved order for the current group, bypassing the
    /// 2-second auto-reapply timer. Does nothing if the order already matches or
    /// there is no saved order for the current group.
    /// </summary>
    public void ApplySavedOrderNow()
    {
        var snapshot = LastSnapshot;
        if (snapshot is null || string.IsNullOrEmpty(CurrentGroupKey)) return;
        if (snapshot.IsCrossWorld) return; // ChangeOrder has no effect in cross-world parties
        if (!TryGetSavedGroup(out var saved)) return;
        if (saved.OrderedContentIds.Count != snapshot.Slots.Count) return;

        var currentOrder = snapshot.Slots.Select(s => s.ContentId).ToList();
        if (currentOrder.SequenceEqual(saved.OrderedContentIds)) return;

        reorderer.DispatchMovesToTarget(currentOrder, saved.OrderedContentIds);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Job-based matching
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Attempts to produce a target ContentId order that satisfies the specific-job
    /// template stored in <paramref name="saved"/>. For each saved position:
    /// <list type="number">
    ///   <item>Prefers the exact same person still playing that job.</item>
    ///   <item>Falls back to any unmatched current member playing that specific job.</item>
    /// </list>
    /// Returns false if any saved position cannot be filled.
    /// </summary>
    private static bool TryMatchByJob(PartySnapshot snapshot, SavedGroup saved, out List<ulong> targetOrder)
    {
        targetOrder = new List<ulong>(saved.OrderedContentIds.Count);

        var cidToJob = BuildCidToJobMap();
        var available = snapshot.Slots
            .Select(s => (ContentId: s.ContentId,
                          JobId: cidToJob.TryGetValue(s.ContentId, out var j) ? j : 0u))
            .ToList();

        var usedIndices = new HashSet<int>();

        for (var i = 0; i < saved.OrderedContentIds.Count; i++)
        {
            var savedCid   = saved.OrderedContentIds[i];
            var savedJobId = saved.OrderedMemberJobIds[i];
            var idx        = -1;

            // Pass 1: same person AND same job.
            for (var j = 0; j < available.Count; j++)
            {
                if (usedIndices.Contains(j)) continue;
                if (available[j].ContentId == savedCid && available[j].JobId == savedJobId)
                { idx = j; break; }
            }

            // Pass 2: any unmatched member playing the same specific job.
            if (idx < 0 && savedJobId != 0)
            {
                for (var j = 0; j < available.Count; j++)
                {
                    if (usedIndices.Contains(j)) continue;
                    if (available[j].JobId == savedJobId)
                    { idx = j; break; }
                }
            }

            if (idx < 0) return false; // No member can fill this position.

            targetOrder.Add(available[idx].ContentId);
            usedIndices.Add(idx);
        }

        return targetOrder.Count == saved.OrderedContentIds.Count;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Looks up the saved group for the current group key. When KeyByInstance is
    /// enabled but no territory-specific save exists, falls back to the base
    /// (members-only) key so existing saves are not orphaned after enabling the setting.
    /// </summary>
    private bool TryGetSavedGroup(out SavedGroup saved)
    {
        if (config.SavedOrders.TryGetValue(CurrentGroupKey, out var s))
        { saved = s; return true; }

        // Fallback: if keying by instance but no territory-specific save is found,
        // try the base (members-only) key so saves from before the setting was enabled
        // remain usable without having to re-save every group.
        if (config.KeyByInstance && LastSnapshot != null)
        {
            var baseKey = ComputeGroupKey(LastSnapshot);
            if (config.SavedOrders.TryGetValue(baseKey, out s))
            { saved = s; return true; }
        }

        saved = null!;
        return false;
    }

    internal static string ComputeGroupKey(PartySnapshot snapshot, uint territoryId = 0, bool includeTerritory = false)
    {
        var ids = snapshot.Slots.Select(s => s.ContentId).Where(c => c != 0).OrderBy(c => c).ToList();
        if (ids.Count == 0) return string.Empty;
        var joined = string.Join(",", ids);
        if (includeTerritory && territoryId > 0)
            joined += $"@{territoryId}";
        var bytes = SHA1.HashData(Encoding.UTF8.GetBytes(joined));
        return Convert.ToHexString(bytes);
    }

    /// <summary>Builds a ContentId → ClassJob.RowId map from the live IPartyList.</summary>
    private static Dictionary<ulong, uint> BuildCidToJobMap()
    {
        var map = new Dictionary<ulong, uint>();
        foreach (var pm in Plugin.PartyList)
        {
            if (pm == null) continue;
            map[(ulong)pm.ContentId] = pm.ClassJob.RowId;
        }
        return map;
    }

    /// <summary>Returns job IDs for each slot in order. 0 when ContentId not found in IPartyList
    /// or when the member is currently on a DoH/DoL job (can happen if saved before job swap).</summary>
    private static List<uint> GetJobIdsForSlots(IReadOnlyList<PartySlotInfo> slots)
    {
        var cidToJob = BuildCidToJobMap();
        return slots.Select(s =>
        {
            if (!cidToJob.TryGetValue(s.ContentId, out var j)) return 0u;
            // Discard DoH/DoL job IDs — they are not meaningful for party ordering and can
            // appear if the save fires before members switch to their combat jobs.
            return JobRoles.GetRole(j) == JobRoles.Role.Unknown ? 0u : j;
        }).ToList();
    }

    public void Dispose() { }
}
