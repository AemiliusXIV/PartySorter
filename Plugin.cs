using System;
using System.Collections.Generic;
using Dalamud.Game.Command;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;
using PartySorter.Automation;
using PartySorter.Services;
using PartySorter.Windows;

namespace PartySorter;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "Party Sorter";

    private const string ConfigCommand = "/psorter";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IPartyList PartyList { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInterop { get; private set; } = null!;
    [PluginService] internal static IToastGui ToastGui { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    internal Configuration Config { get; }
    internal WindowSystem WindowSystem { get; } = new("PartySorter");
    internal ConfigWindow ConfigWindow { get; }
    internal SavedGroupsWindow SavedGroupsWindow { get; }
    internal PartyAddonReader AddonReader { get; }
    internal PartyReorderer Reorderer { get; }
    internal PartySortController SortController { get; }
    internal DragController DragController { get; }

    // Used to delay the instance-enter check until the party list has had time to populate.
    private bool pendingInstanceEnterCheck = false;
    private DateTime instanceCheckAfterUtc = DateTime.MinValue;
    private const double InstanceCheckDelaySeconds = 3.0;

    public Plugin()
    {
        Config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Config.Initialize(PluginInterface);

        AddonReader = new PartyAddonReader();
        Reorderer = new PartyReorderer();
        SortController = new PartySortController(Config, Reorderer, AddonReader);
        DragController = new DragController(Config, SortController);
        ConfigWindow = new ConfigWindow(this);
        SavedGroupsWindow = new SavedGroupsWindow(this);

        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(SavedGroupsWindow);

        PluginInterface.UiBuilder.Draw += OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        Framework.Update += OnFrameworkUpdate;
        ClientState.TerritoryChanged += OnTerritoryChanged;

        CommandManager.AddHandler(ConfigCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Party Sorter. Subcommands: save (save current order to chat), debug.",
            ShowInHelp = true,
        });
    }

    public void Dispose()
    {
        ClientState.TerritoryChanged -= OnTerritoryChanged;
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= OnDraw;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;

        CommandManager.RemoveHandler(ConfigCommand);

        WindowSystem.RemoveAllWindows();

        DragController.Dispose();
        SortController.Dispose();
        Reorderer.Dispose();
        AddonReader.Dispose();
    }

    private void OnTerritoryChanged(uint territory)
    {
        pendingInstanceEnterCheck = true;
        instanceCheckAfterUtc = DateTime.UtcNow.AddSeconds(InstanceCheckDelaySeconds);
    }

    private void HandleInstanceEnter()
    {
        if (!Config.Enabled) return;

        var key      = SortController.CurrentGroupKey;
        var hasSaved = !string.IsNullOrEmpty(key) && Config.SavedOrders.ContainsKey(key);

        // Smart open: only open when there is NO saved order for the current group,
        // so users with statics aren't interrupted every pull.
        // When InstanceEnterHighEndOnly is set, additionally require that the duty
        // is flagged as high-end in FFXIV's own data (Extreme / Savage / Ultimate /
        // Criterion). The flag is maintained by Square Enix and requires no plugin
        // update when new high-end content rotates in.
        if (Config.OpenConfigOnInstanceEnter && !hasSaved)
        {
            var territoryId = (uint)ClientState.TerritoryType;
            if (!Config.InstanceEnterHighEndOnly || IsHighEndDuty(territoryId))
                SavedGroupsWindow.IsOpen = true;
        }

        if (Config.NotifyOnInstanceEnter)
        {
            if (hasSaved && Config.SavedOrders.TryGetValue(key, out var group))
            {
                var label = string.IsNullOrWhiteSpace(group.Label) ? "this group" : group.Label;
                ToastGui.ShowNormal($"PartySorter: Saved order found for {label}.");
            }
            else
            {
                ToastGui.ShowNormal($"PartySorter: No saved order — hold {Config.Modifier} to drag and set one.");
            }
        }
    }

    private void OnDraw()
    {
        WindowSystem.Draw();

        if (Config.Enabled)
        {
            try
            {
                DragController.Draw();
            }
            catch (Exception ex)
            {
                Log.Error(ex, "DragController.Draw threw");
            }
        }
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (pendingInstanceEnterCheck && DateTime.UtcNow >= instanceCheckAfterUtc)
        {
            pendingInstanceEnterCheck = false;
            if (Condition[ConditionFlag.BoundByDuty])
                HandleInstanceEnter();
        }

        if (!Config.Enabled)
            return;

        try
        {
            SortController.Tick();
        }
        catch (Exception ex)
        {
            Log.Error(ex, "PartySortController.Tick threw");
        }
    }

    // ── Job icon lookup ──────────────────────────────────────────────────────
    private static readonly Dictionary<uint, uint> JobIconCache = new();

    /// <summary>
    /// Returns the game icon ID for a ClassJob RowId, or 0 if unknown.
    /// Results are cached so repeated calls during Draw() are cheap.
    /// </summary>
    internal static uint GetJobIconId(uint jobId)
    {
        if (jobId == 0) return 0;
        if (JobIconCache.TryGetValue(jobId, out var cached)) return cached;

        // DoH/DoL jobs have no meaningful combat icon — return 0 so the caller
        // falls back to the [?] role-text badge instead of showing a crafter icon.
        if (JobRoles.GetRole(jobId) == JobRoles.Role.Unknown)
        {
            JobIconCache[jobId] = 0;
            return 0;
        }

        try
        {
            var sheet = DataManager.GetExcelSheet<ClassJob>();
            if (sheet != null && sheet.TryGetRow(jobId, out var row))
            {
                // Job icons are at 62000 + JobIndex (byte field on ClassJob)
                var iconId = 62000u + (uint)row.JobIndex;
                JobIconCache[jobId] = iconId;
                return iconId;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PartySorter: GetJobIconId failed for job {0}", jobId);
        }
        JobIconCache[jobId] = 0;
        return 0;
    }

    /// <summary>
    /// Looks up the ContentFinderCondition name for a territory ID.
    /// Returns null for open-world zones, unknown territories, or if the lookup fails.
    /// </summary>
    internal static string? GetDutyName(uint territoryId)
    {
        if (territoryId == 0) return null;
        try
        {
            var sheet = DataManager.GetExcelSheet<ContentFinderCondition>();
            if (sheet == null) return null;
            foreach (var row in sheet)
            {
                if (row.TerritoryType.RowId == territoryId)
                {
                    var name = row.Name.ToString();
                    return string.IsNullOrEmpty(name) ? null : name;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PartySorter: GetDutyName failed for territory {0}", territoryId);
        }
        return null;
    }

    /// <summary>
    /// Returns true when the territory belongs to a high-end duty (Extreme trial,
    /// Savage raid, Ultimate, or Criterion) according to FFXIV's own
    /// <c>ContentFinderCondition.HighEndDuty</c> flag.  Because this comes directly
    /// from game data, it requires no plugin update when new high-end content is added.
    /// </summary>
    internal static bool IsHighEndDuty(uint territoryId)
    {
        if (territoryId == 0) return false;
        try
        {
            var sheet = DataManager.GetExcelSheet<ContentFinderCondition>();
            if (sheet == null) return false;
            foreach (var row in sheet)
                if (row.TerritoryType.RowId == territoryId)
                    return row.HighEndDuty;
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "PartySorter: IsHighEndDuty failed for territory {0}", territoryId);
        }
        return false;
    }

    internal void OpenMain()
    {
        SavedGroupsWindow.IsOpen = true;
    }

    internal void OpenSettings()
    {
        ConfigWindow.IsOpen = true;
    }

    private void OnCommand(string command, string args)
    {
        var trimmed = (args ?? string.Empty).Trim().ToLowerInvariant();
        switch (trimmed)
        {
            case "save":
                RunSaveCommand();
                return;
            case "debug":
                RunDiagnostic();
                return;
            default:
                OpenMain();
                return;
        }
    }

    /// <summary>
    /// Saves the current live party order and reports the result to chat.
    /// Used by the /pdragsort save command so players can save without opening a window.
    /// </summary>
    private void RunSaveCommand()
    {
        if (SortController.TrySaveCurrentOrder(out var msg))
            ChatGui.Print($"[PartySorter] {msg}");
        else
            ChatGui.PrintError($"[PartySorter] {msg}");
    }

    /// <summary>
    /// Prints the current drag/party-list state to chat so the user can paste it
    /// when reporting "it doesn't engage in this situation".
    /// </summary>
    private void RunDiagnostic()
    {
        try
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== PartySorter diagnostic ===");
            sb.AppendLine($"Plugin enabled: {Config.Enabled}");
            sb.AppendLine($"Modifier configured: {Config.Modifier}");
            sb.AppendLine($"BoundByDuty: {Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty]}");
            sb.AppendLine($"PlayerState.IsLoaded: {PlayerState.IsLoaded}");
            sb.AppendLine($"PlayerState.CharacterName: \"{PlayerState.CharacterName}\"");
            sb.AppendLine($"IPartyList.Length: {PartyList.Length}");

            var ipNames = new System.Collections.Generic.List<string>();
            foreach (var pm in PartyList)
            {
                if (pm == null) continue;
                ipNames.Add($"\"{pm.Name.TextValue}\" (cid={(ulong)pm.ContentId})");
            }
            sb.AppendLine($"IPartyList members: [{string.Join(", ", ipNames)}]");

            var pl = GameGui.GetAddonByName("_PartyList");
            sb.AppendLine($"_PartyList addon: IsNull={pl.IsNull}, IsVisible={(pl.IsNull ? "N/A" : pl.IsVisible.ToString())}");

            var cw = GameGui.GetAddonByName("_CrossWorldPartyList");
            sb.AppendLine($"_CrossWorldPartyList addon: IsNull={cw.IsNull}, IsVisible={(cw.IsNull ? "N/A" : cw.IsVisible.ToString())}");

            var snap = SortController.LastSnapshot;
            if (snap != null)
            {
                sb.AppendLine($"LastSnapshot: addon={snap.AddonName}, isCrossWorld={snap.IsCrossWorld}, slots={snap.Slots.Count}");
                foreach (var s in snap.Slots)
                    sb.AppendLine($"  slot {s.SlotIndex}: \"{s.Name}\" cid={s.ContentId} isLocal={s.IsLocalPlayer}");
            }
            else
            {
                sb.AppendLine("LastSnapshot: (null — TrySnapshot did not yield a snapshot last tick)");
            }

            sb.AppendLine($"CurrentGroupKey: \"{SortController.CurrentGroupKey}\"");

            // Per-slot probe of the addon — shows exactly what TryReadAddon sees
            sb.AppendLine();
            sb.AppendLine(AddonReader.ProbePartyList());

            // Print to both /xllog viewer and chat
            Log.Information(sb.ToString());
            ChatGui.Print(sb.ToString());
        }
        catch (Exception ex)
        {
            Log.Error(ex, "RunDiagnostic threw");
            ChatGui.PrintError($"PartySorter diagnostic failed: {ex.Message}");
        }
    }
}
