using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using PartySorter.Services;

namespace PartySorter.Windows;

public sealed class SavedGroupsWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Configuration Config => plugin.Config;

    // ── Delete confirmation ─────────────────────────────────────────────────
    private string pendingDeleteKey = string.Empty;

    // ── Clean-up confirmation ───────────────────────────────────────────────
    private bool pendingCleanup = false;

    // ── Label inline edit ───────────────────────────────────────────────────
    private string? editingKey    = null;
    private string  editBuffer    = string.Empty;
    private bool    focusEditNext = false;

    // ── Layout constants ────────────────────────────────────────────────────
    private const float CellPadY = 4f;

    // ── Role badge colours (fallback when icon unavailable) ─────────────────
    private static readonly Vector4 TankColor    = new(0.40f, 0.72f, 1.00f, 1.00f);
    private static readonly Vector4 HealColor    = new(0.40f, 1.00f, 0.50f, 1.00f);
    private static readonly Vector4 DpsColor     = new(1.00f, 0.50f, 0.30f, 1.00f);
    private static readonly Vector4 UnknownColor = new(0.55f, 0.55f, 0.55f, 1.00f);

    private static (string tag, Vector4 color) RoleBadgeFallback(uint jobId) =>
        JobRoles.GetRole(jobId) switch
        {
            JobRoles.Role.Tank   => ("[T]", TankColor),
            JobRoles.Role.Healer => ("[H]", HealColor),
            JobRoles.Role.Dps    => ("[D]", DpsColor),
            _                    => ("[?]", UnknownColor),
        };

    public SavedGroupsWindow(Plugin plugin) : base("Party Sorter##ps_main")
    {
        this.plugin = plugin;
        Size = new Vector2(700, 480);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        var snapshot = plugin.SortController.LastSnapshot;
        var key      = plugin.SortController.CurrentGroupKey;

        // ── Detect unsaved changes for the current group ──────────────────────
        var isCrossWorld       = snapshot?.IsCrossWorld == true;
        var hasLiveParty       = snapshot != null && !string.IsNullOrEmpty(key);
        var currentGroupLocked = hasLiveParty &&
                                 Config.SavedOrders.TryGetValue(key, out var lockedCheck) &&
                                 lockedCheck.Locked;

        var hasUnsavedChanges = false;
        if (hasLiveParty && Config.SavedOrders.TryGetValue(key, out var savedEntry))
        {
            var liveCids  = snapshot!.Slots.Select(s => s.ContentId).ToList();
            var savedCids = savedEntry.OrderedContentIds;
            hasUnsavedChanges = savedCids.Count == liveCids.Count &&
                                !liveCids.SequenceEqual(savedCids);
        }

        // ── Clean-up candidate count ──────────────────────────────────────────
        var cleanupCandidates = Config.SavedOrders
            .Where(kv => string.IsNullOrWhiteSpace(kv.Value.Label) && !kv.Value.Locked)
            .Select(kv => kv.Key)
            .ToList();

        // ── Toolbar ───────────────────────────────────────────────────────────
        if (!hasLiveParty || currentGroupLocked) ImGui.BeginDisabled();

        if (ImGui.Button("Save current order"))
            plugin.SortController.SaveCurrentOrder();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            if (!hasLiveParty)
                ImGui.SetTooltip("Only available while in a party.");
            else if (currentGroupLocked)
                ImGui.SetTooltip("This group is locked. Unlock it to save a new order.");
            else
                ImGui.SetTooltip(
                    "Save the current live in-game party order for this group.\n" +
                    "If this group already had a saved order it will be overwritten.");
        }

        if (!hasLiveParty || currentGroupLocked) ImGui.EndDisabled();
        ImGui.SameLine();

        if (!hasLiveParty || !hasUnsavedChanges || isCrossWorld) ImGui.BeginDisabled();

        if (ImGui.Button("Apply saved order"))
            plugin.SortController.ApplySavedOrderNow();
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            if (!hasLiveParty)
                ImGui.SetTooltip("Only available while in a party.");
            else if (isCrossWorld)
                ImGui.SetTooltip("Not available in cross-world parties.\nZone into a duty first.");
            else if (!hasUnsavedChanges)
                ImGui.SetTooltip("Live order already matches the saved order.");
            else
                ImGui.SetTooltip("Immediately restore the saved party order.");
        }

        if (!hasLiveParty || !hasUnsavedChanges || isCrossWorld) ImGui.EndDisabled();
        ImGui.SameLine();

        if (cleanupCandidates.Count == 0) ImGui.BeginDisabled();

        if (ImGui.Button($"Clean up ({cleanupCandidates.Count})"))
            pendingCleanup = true;
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            if (cleanupCandidates.Count == 0)
                ImGui.SetTooltip("No unlabelled, unlocked groups to remove.");
            else
                ImGui.SetTooltip(
                    $"Delete {cleanupCandidates.Count} unlabelled, unlocked group(s).\n" +
                    "Labelled groups and locked groups are never removed.");
        }

        if (cleanupCandidates.Count == 0) ImGui.EndDisabled();

        // Right-align the group count and Settings button together so the
        // status text isn't awkwardly sandwiched between action buttons.
        var statusText  = $"{Config.SavedOrders.Count} group(s) saved";
        var style       = ImGui.GetStyle();
        var statusW     = ImGui.CalcTextSize(statusText).X;
        var settingsW   = ImGui.CalcTextSize("Settings").X + style.FramePadding.X * 2;
        var rightGroupW = statusW + style.ItemSpacing.X + settingsW;
        ImGui.SameLine(ImGui.GetCursorPosX() + ImGui.GetContentRegionAvail().X - rightGroupW);
        ImGui.TextDisabled(statusText);
        ImGui.SameLine();
        if (ImGui.Button("Settings"))
            plugin.OpenSettings();

        ImGui.Spacing();

        // ── Group list ────────────────────────────────────────────────────────
        if (Config.SavedOrders.Count == 0)
        {
            ImGui.TextDisabled("No saved groups yet.");
            ImGui.TextDisabled("Drag a party-list card while in a party to record one.");
            DrawCleanupModal(cleanupCandidates);
            DrawDeleteModal();
            return;
        }

        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(6, CellPadY));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(4, 4));

        // 5 columns: Label | Instance | Last used | Edit | Delete
        // Edit and Delete are separate fixed-width columns so buttons align cleanly.
        if (ImGui.BeginTable("##saved_groups_w", 5,
            ImGuiTableFlags.RowBg | ImGuiTableFlags.BordersInnerH |
            ImGuiTableFlags.BordersOuterH | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupScrollFreeze(0, 1);
            ImGui.TableSetupColumn("Label",     ImGuiTableColumnFlags.WidthFixed,   185);
            ImGui.TableSetupColumn("Instance",  ImGuiTableColumnFlags.WidthStretch);
            ImGui.TableSetupColumn("Last used", ImGuiTableColumnFlags.WidthFixed,   100);
            ImGui.TableSetupColumn("##edit",    ImGuiTableColumnFlags.WidthFixed,    52);
            ImGui.TableSetupColumn("##delete",  ImGuiTableColumnFlags.WidthFixed,    68);
            ImGui.TableHeadersRow();

            var keys = Config.SavedOrders
                .OrderByDescending(kv => kv.Value.LastUsedUtc)
                .Select(kv => kv.Key)
                .ToList();

            foreach (var k in keys)
            {
                var entry         = Config.SavedOrders[k];
                var isCurrent     = string.Equals(k, key, StringComparison.Ordinal);
                var rowHasUnsaved = isCurrent && hasUnsavedChanges;

                ImGui.TableNextRow();

                // Current-group tint — drawn in the first column but spans the
                // full row width via the window draw list (unclipped by cell).
                if (isCurrent)
                {
                    ImGui.TableNextColumn();
                    var cursor   = ImGui.GetCursorScreenPos();
                    var rowWidth = ImGui.GetContentRegionAvail().X;
                    var rowH     = ImGui.GetFrameHeight() + CellPadY * 2;
                    ImGui.GetWindowDrawList().AddRectFilled(
                        cursor with { X = cursor.X - 6, Y = cursor.Y - CellPadY },
                        cursor + new Vector2(rowWidth + 12, rowH),
                        ImGui.GetColorU32(new Vector4(0.4f, 0.85f, 1f, 0.08f)));
                }
                else
                {
                    ImGui.TableNextColumn();
                }

                // Column 1: [Lock] [Label text / edit input] [●/◆ indicator]
                DrawLabelCell(k, entry, isCurrent, rowHasUnsaved);

                // Column 2: [▾ expand] Instance / duty name
                ImGui.TableNextColumn();
                DrawInstanceCell(k, entry);

                // Column 3: Relative time
                ImGui.TableNextColumn();
                ImGui.TextDisabled(FormatLastUsed(entry.LastUsedUtc));

                // Column 4: Edit button (disabled while this row is being edited)
                ImGui.TableNextColumn();
                var isEditing = editingKey == k;
                if (isEditing) ImGui.BeginDisabled();
                if (ImGui.Button($"Edit##{k}", new Vector2(-1, 0)))
                {
                    editingKey    = k;
                    editBuffer    = entry.Label ?? string.Empty;
                    focusEditNext = true;
                }
                if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                    ImGui.SetTooltip(isEditing ? "Currently editing — press Enter or click away to confirm." : "Edit group label");
                if (isEditing) ImGui.EndDisabled();

                // Column 5: Delete button
                ImGui.TableNextColumn();
                if (ImGui.Button($"Delete##{k}", new Vector2(-1, 0)))
                    pendingDeleteKey = k;
            }

            ImGui.EndTable();
        }

        ImGui.PopStyleVar(2);

        DrawCleanupModal(cleanupCandidates);
        DrawDeleteModal();
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private static string FormatLastUsed(DateTime utc)
    {
        var diff = DateTime.UtcNow - utc;
        if (diff.TotalMinutes < 1)  return "Just now";
        if (diff.TotalHours   < 1)  return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalDays    < 1)  return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays    < 2)  return "Yesterday";
        if (diff.TotalDays    < 7)  return $"{(int)diff.TotalDays}d ago";
        return utc.ToLocalTime().ToString("yyyy-MM-dd");
    }

    private static string BuildInstanceText(SavedGroup entry)
    {
        var count  = entry.OrderedMemberNames?.Count ?? 0;
        var suffix = count > 0 ? $"  ({count})" : string.Empty;

        if (!string.IsNullOrEmpty(entry.LastUsedDutyName))
            return entry.LastUsedDutyName + suffix;
        if (entry.LastUsedTerritoryId > 0)
            return "Open world" + suffix;
        return "—";
    }

    // ────────────────────────────────────────────────────────────────────────
    // Label cell — [Lock] [Label text / edit input] [●/◆ indicator]
    //
    // The Edit button has been moved to its own table column so the first
    // thing the eye sees in a row is the label itself, not controls.
    // ────────────────────────────────────────────────────────────────────────

    private void DrawLabelCell(string k, SavedGroup entry, bool isCurrent, bool hasUnsaved)
    {
        // Lock / unlock icon — small, tight to the left edge
        var lockIcon = (entry.Locked ? FontAwesomeIcon.Lock : FontAwesomeIcon.LockOpen).ToIconString();
        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            if (ImGui.Button($"{lockIcon}##{k}_lock", new Vector2(26, 0)))
            {
                entry.Locked = !entry.Locked;
                Config.Save();
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(entry.Locked
                ? "Locked — cannot be overwritten by save or auto-save.\nClick to unlock."
                : "Unlocked — drag or save will update this group.\nClick to lock.");
        ImGui.SameLine();

        if (editingKey == k)
        {
            // Inline edit field — fills the remaining column width
            if (focusEditNext) { ImGui.SetKeyboardFocusHere(); focusEditNext = false; }
            ImGui.SetNextItemWidth(-1);
            var confirmed   = ImGui.InputText($"##label_edit_{k}", ref editBuffer, 64,
                                              ImGuiInputTextFlags.EnterReturnsTrue);
            var deactivated = ImGui.IsItemDeactivated();
            if (confirmed || deactivated)
                CommitLabelEdit(k);
        }
        else
        {
            // Label text — primary identifier, now first after the lock icon
            if (string.IsNullOrEmpty(entry.Label))
                ImGui.TextDisabled("(unlabelled)");
            else
                ImGui.TextUnformatted(entry.Label);

            // Current-group indicator sits immediately after the label
            if (isCurrent)
            {
                ImGui.SameLine(0, 6);
                if (hasUnsaved)
                {
                    ImGui.TextColored(new Vector4(1f, 0.85f, 0.2f, 1f), "●");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip(
                            "Live order differs from saved order.\n" +
                            "Use \"Apply saved order\" to restore,\n" +
                            "or \"Save current order\" to update.");
                }
                else
                {
                    ImGui.TextColored(new Vector4(0.4f, 0.85f, 1f, 0.8f), "◆");
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("This is your current party group.");
                }
            }
        }
    }

    private void CommitLabelEdit(string k)
    {
        if (Config.SavedOrders.TryGetValue(k, out var entry))
        {
            entry.Label = string.IsNullOrWhiteSpace(editBuffer) ? null : editBuffer.Trim();
            Config.Save();
        }
        editingKey = null;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Job icon rendering
    // ────────────────────────────────────────────────────────────────────────

    private static void DrawJobIcon(uint jobId, Vector2 size)
    {
        var iconId = Plugin.GetJobIconId(jobId);
        if (iconId != 0)
        {
            try
            {
                var tex  = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId));
                var wrap = tex.GetWrapOrDefault();
                if (wrap != null)
                {
                    ImGui.Image(wrap.Handle, size);
                    return;
                }
            }
            catch { /* fall through to text badge */ }
        }

        var (tag, col) = RoleBadgeFallback(jobId);
        ImGui.TextColored(col, tag);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Instance cell — FontAwesome icon + instance / duty name text
    // ────────────────────────────────────────────────────────────────────────

    private void DrawInstanceCell(string k, SavedGroup entry)
    {
        var names = entry.OrderedMemberNames;
        if (names == null || names.Count == 0)
        {
            ImGui.TextDisabled(BuildInstanceText(entry));
            return;
        }

        var popupId     = $"##details_{k}";
        var expandIcon  = FontAwesomeIcon.Users.ToIconString();

        using (Plugin.PluginInterface.UiBuilder.IconFontHandle.Push())
        {
            if (ImGui.Button($"{expandIcon}##{k}_expand", new Vector2(26, 0)))
                ImGui.OpenPopup(popupId);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Show members and settings");
        ImGui.SameLine();
        ImGui.TextUnformatted(BuildInstanceText(entry));

        if (ImGui.BeginPopup(popupId))
        {
            // ── Member roster ──────────────────────────────────────────────
            ImGui.TextUnformatted($"Saved order — {names.Count} member(s):");
            ImGui.Separator();

            var hasJobIds = entry.OrderedMemberJobIds.Count == names.Count;
            var iconSize  = new Vector2(ImGui.GetTextLineHeight(), ImGui.GetTextLineHeight());

            for (var idx = 0; idx < names.Count; idx++)
            {
                var jobId = hasJobIds ? entry.OrderedMemberJobIds[idx] : 0u;
                ImGui.Text($"  {idx + 1}.");
                ImGui.SameLine(0, 4);
                DrawJobIcon(jobId, iconSize);
                ImGui.SameLine(0, 4);
                ImGui.TextUnformatted(names[idx]);
            }

            // ── Auto-apply setting ─────────────────────────────────────────
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();

            ImGui.TextUnformatted("Auto-apply:");
            ImGui.SameLine();

            var modeIdx = (int)entry.AutoApply;
            ImGui.SetNextItemWidth(160);
            if (ImGui.Combo($"##autoapply_{k}", ref modeIdx,
                            "By names\0Disabled\0By job\0Names + job\0\0"))
            {
                entry.AutoApply = (AutoApplyMode)modeIdx;
                Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "By names:     restore order when the same members are present.\n" +
                    "Disabled:     never auto-restore this group.\n" +
                    "By job:       restore order by specific job — e.g. the WHM fills\n" +
                    "              the WHM slot even if the player has changed.\n" +
                    "              (requires job IDs recorded at save time)\n" +
                    "Names + job:  restore only when the same members are present AND\n" +
                    "              each is still playing the same specific job as when saved.");

            ImGui.Spacing();
            var locked = entry.Locked;
            if (ImGui.Checkbox($"Lock this group##{k}_lock_popup", ref locked))
            {
                entry.Locked = locked;
                Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "When locked, this group cannot be overwritten by\n" +
                    "\"Save current order\" or AutoSaveOnDrag.\n" +
                    "Auto-reapply still reads and applies it normally.");

            ImGui.EndPopup();
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Modals
    // ────────────────────────────────────────────────────────────────────────

    private void DrawCleanupModal(List<string> candidates)
    {
        if (pendingCleanup)
        {
            ImGui.OpenPopup("Confirm clean up##pds_cleanup");
            pendingCleanup = false;
        }

        var open = true;
        if (ImGui.BeginPopupModal("Confirm clean up##pds_cleanup", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextUnformatted($"Delete {candidates.Count} unlabelled, unlocked group(s)?");
            ImGui.TextDisabled("Labelled groups and locked groups are never removed.");
            ImGui.TextDisabled("This cannot be undone.");
            ImGui.Spacing();

            if (ImGui.Button("Delete all"))
            {
                foreach (var c in candidates)
                    Config.SavedOrders.Remove(c);
                Config.Save();
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
                ImGui.CloseCurrentPopup();

            ImGui.EndPopup();
        }
    }

    private void DrawDeleteModal()
    {
        if (!string.IsNullOrEmpty(pendingDeleteKey))
            ImGui.OpenPopup("Confirm delete##pds_sg");

        var open = !string.IsNullOrEmpty(pendingDeleteKey);
        if (ImGui.BeginPopupModal("Confirm delete##pds_sg", ref open, ImGuiWindowFlags.AlwaysAutoResize))
        {
            var entry = Config.SavedOrders.TryGetValue(pendingDeleteKey, out var e) ? e : null;
            var name  = string.IsNullOrWhiteSpace(entry?.Label) ? "this group" : entry!.Label;
            ImGui.TextUnformatted($"Delete saved order for \"{name}\"?");
            ImGui.TextDisabled("This cannot be undone.");
            ImGui.Spacing();

            if (ImGui.Button("Delete"))
            {
                Config.SavedOrders.Remove(pendingDeleteKey);
                Config.Save();
                pendingDeleteKey = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel"))
            {
                pendingDeleteKey = string.Empty;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }
        if (!open) pendingDeleteKey = string.Empty;
    }

    public void Dispose() { }
}
