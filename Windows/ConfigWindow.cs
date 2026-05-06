using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace PartySorter.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private Configuration Config => plugin.Config;

    // Accent colour used for section headers throughout
    private static readonly Vector4 HeaderColor = new(0.65f, 0.88f, 1f, 0.95f);

    public ConfigWindow(Plugin plugin) : base("Party Sorter Settings##ps_cfg")
    {
        this.plugin = plugin;
        Size = new Vector2(480, 560);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        // ── Live status bar ──────────────────────────────────────────────────
        DrawStatusBar();

        ImGui.Spacing();

        // Quick jump to the main / saved-groups window.
        if (ImGui.Button("Open Saved Groups window"))
            plugin.OpenMain();
        ImGui.SameLine();
        ImGui.TextDisabled($"({Config.SavedOrders.Count} group(s) saved)");

        // ── General ─────────────────────────────────────────────────────────
        DrawSectionHeader("General");

        var enabled = Config.Enabled;
        if (ImGui.Checkbox("Plugin enabled", ref enabled))
        {
            Config.Enabled = enabled;
            Config.Save();
        }

        var modIdx = (int)Config.Modifier;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("##modifier", ref modIdx, "Shift\0Ctrl\0Alt\0\0"))
        {
            Config.Modifier = (DragModifier)modIdx;
            Config.Save();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("Drag modifier key");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Hold this key while dragging a party-list card onto another to reorder.\n" +
                "Slot 1 is locked to your character and cannot be moved.");

        var dropIdx = (int)Config.DropBehavior;
        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("##drop_behaviour", ref dropIdx, "Swap two members\0Shift others\0\0"))
        {
            Config.DropBehavior = (DropBehavior)dropIdx;
            Config.Save();
        }
        ImGui.SameLine();
        ImGui.TextUnformatted("Drop behaviour");
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Swap (default): the two members exchange positions directly.\n" +
                "Shift: the dragged member is inserted at the drop position;\n" +
                "       every member between the two slots slides one step to fill the gap.");

        // ── Saving ───────────────────────────────────────────────────────────
        DrawSectionHeader("Saving");

        var autoSave = Config.AutoSaveOnDrag;
        if (ImGui.Checkbox("Auto-save order on drag", ref autoSave))
        {
            Config.AutoSaveOnDrag = autoSave;
            Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "When enabled, every drag automatically updates the saved order for this group —\n" +
                "but only if a saved entry already exists AND it is not locked.\n" +
                "Dragging with an unsaved group does nothing; use \"Save current order\" to\n" +
                "create the first entry, then future drags will update it automatically.");

        ImGui.Indent();
        if (Config.AutoSaveOnDrag)
            ImGui.TextDisabled("Updates existing entries on drag. Does not create new ones.");
        else
            ImGui.TextDisabled("Off — use \"Save current order\" or /pdragsort save.");
        ImGui.Unindent();

        // ── Auto-Reapply ────────────────────────────────────────────────────
        DrawSectionHeader("Auto-Reapply");

        var autoReapply = Config.AutoReapplyEnabled;
        if (ImGui.Checkbox("Auto-reapply saved order", ref autoReapply))
        {
            Config.AutoReapplyEnabled = autoReapply;
            Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "When the same group of party members is detected,\n" +
                "restore their saved order automatically. Works across\n" +
                "the cross-world ↔ in-instance transition for statics.");

        if (Config.AutoReapplyEnabled)
        {
            ImGui.Indent();

            var instanceOnly = Config.OnlyReapplyInInstance;
            if (ImGui.Checkbox("Only reapply inside duties", ref instanceOnly))
            {
                Config.OnlyReapplyInInstance = instanceOnly;
                Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "When enabled, auto-reapply only fires while inside a duty.\n" +
                    "Open-world and cross-world parties are left alone.");

            var notifyReapply = Config.NotifyOnReapply;
            if (ImGui.Checkbox("Notify when order is restored", ref notifyReapply))
            {
                Config.NotifyOnReapply = notifyReapply;
                Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Shows a toast each time auto-reapply successfully restores a saved order.");

            ImGui.Unindent();
        }

        // ── Group Identification ─────────────────────────────────────────────
        DrawSectionHeader("Group Identification");

        var keyByInstance = Config.KeyByInstance;
        if (ImGui.Checkbox("Separate saved order per duty", ref keyByInstance))
        {
            Config.KeyByInstance = keyByInstance;
            Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "When enabled, saved orders are keyed by both the member set AND the current\n" +
                "duty/territory. The same group of people can have a different saved order in\n" +
                "Eden vs. Coils, for example.\n\n" +
                "Note: existing saves (keyed members-only) will no longer be matched\n" +
                "after enabling this — re-save each group inside a duty to restore them.");

        if (Config.KeyByInstance)
        {
            ImGui.Indent();
            ImGui.TextDisabled("Active — each duty gets its own saved order for the same group.");
            ImGui.Unindent();
        }

        // ── On Duty Enter ────────────────────────────────────────────────────
        DrawSectionHeader("On Duty Enter");

        var openOnEnter = Config.OpenConfigOnInstanceEnter;
        if (ImGui.Checkbox("Open main window on duty enter", ref openOnEnter))
        {
            Config.OpenConfigOnInstanceEnter = openOnEnter;
            Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Opens the PartySorter window a few seconds after entering a duty,\n" +
                "but only when no saved order exists for the current group.\n" +
                "Groups that already have a saved order are left alone.");

        ImGui.Indent();
        if (Config.OpenConfigOnInstanceEnter)
        {
            var highEndOnly = Config.InstanceEnterHighEndOnly;
            if (ImGui.Checkbox("Only for high-end duties", ref highEndOnly))
            {
                Config.InstanceEnterHighEndOnly = highEndOnly;
                Config.Save();
            }
            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "Only opens the window when entering Extreme, Savage, Ultimate,\n" +
                    "or Criterion content. Normal duties are ignored.\n\n" +
                    "Uses FFXIV's own high-end duty flag — updates automatically\n" +
                    "when new high-end content is released, no plugin update needed.");

            ImGui.TextDisabled(Config.InstanceEnterHighEndOnly
                ? "Active — high-end duties only (Extreme / Savage / Ultimate / Criterion)."
                : "Active — all duties.");
        }
        else
        {
            ImGui.TextDisabled("Window will not open automatically.");
        }
        ImGui.Unindent();

        var notifyEnter = Config.NotifyOnInstanceEnter;
        if (ImGui.Checkbox("Show toast on duty enter", ref notifyEnter))
        {
            Config.NotifyOnInstanceEnter = notifyEnter;
            Config.Save();
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Shows a brief toast when entering a duty:\n" +
                "• Saved order found — confirms the order will be restored.\n" +
                "• No saved order — prompts you to drag and set one.");

        // ── Footer ───────────────────────────────────────────────────────────
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextDisabled("Hold the modifier key and drag a party-list card to reorder.");
        ImGui.TextDisabled("/psorter save  ·  /psorter debug");
    }

    // ────────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────────

    private void DrawStatusBar()
    {
        var snap = plugin.SortController.LastSnapshot;
        var grpKey = plugin.SortController.CurrentGroupKey;

        if (snap != null && snap.Slots.Count > 0)
        {
            var memberCount = snap.Slots.Count;
            string groupDesc;
            if (!string.IsNullOrEmpty(grpKey) && Config.SavedOrders.TryGetValue(grpKey, out var grp))
            {
                groupDesc = string.IsNullOrWhiteSpace(grp.Label)
                    ? "order saved"
                    : $"order saved — \"{grp.Label}\"";
            }
            else
            {
                groupDesc = "no saved order";
            }

            ImGui.TextColored(new Vector4(0.4f, 1f, 0.55f, 0.9f), "●");
            ImGui.SameLine(0, 6);
            ImGui.TextUnformatted($"{memberCount} members in party  ·  {groupDesc}");
        }
        else
        {
            ImGui.TextDisabled("○  No party detected");
        }
    }

    private static void DrawSectionHeader(string title)
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
        ImGui.TextColored(HeaderColor, title);
        ImGui.Spacing();
    }

    public void Dispose() { }
}
