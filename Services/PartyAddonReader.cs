using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Text;
using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace PartySorter.Services;

public sealed class PartySlotInfo
{
    public required int SlotIndex { get; init; }
    public required ulong ContentId { get; init; }
    public required string Name { get; init; }
    public required string WorldName { get; init; }
    public required Vector2 ScreenPos { get; init; }
    public required Vector2 ScreenSize { get; init; }
    public bool IsLocalPlayer { get; init; }
}

public sealed class PartySnapshot
{
    public required string AddonName { get; init; }
    public required bool IsCrossWorld { get; init; }
    public required IReadOnlyList<PartySlotInfo> Slots { get; init; }
    public required ulong LocalPlayerContentId { get; init; }
    public required string LocalPlayerName { get; init; }
}

public sealed class PartyAddonReader : IDisposable
{
    private const string PartyListAddon = "_PartyList";
    private const string CrossWorldAddon = "_CrossWorldPartyList";
    private const int MaxSlots = 8;

    public bool TrySnapshot(out PartySnapshot? snapshot)
    {
        snapshot = null;

        var pState = Plugin.PlayerState;
        if (!pState.IsLoaded)
            return false;

        var localCid = pState.ContentId;
        var localName = pState.CharacterName;

        var partyList = Plugin.PartyList;
        if (partyList.Length < 1)
            return false;

        var members = new Dictionary<string, (ulong cid, string world)>(StringComparer.Ordinal);
        foreach (var pm in partyList)
        {
            if (pm == null) continue;
            var worldName = pm.World.ValueNullable?.Name.ToString() ?? string.Empty;
            members[pm.Name.TextValue] = ((ulong)pm.ContentId, worldName);
        }

        if (TryReadAddon(PartyListAddon, members, localName, localCid, isCrossWorld: false, out var standard))
        {
            snapshot = standard;
            return true;
        }

        if (TryReadAddon(CrossWorldAddon, members, localName, localCid, isCrossWorld: true, out var cross))
        {
            snapshot = cross;
            return true;
        }

        return false;
    }

    private static unsafe bool TryReadAddon(
        string addonName,
        Dictionary<string, (ulong cid, string world)> members,
        string localName,
        ulong localCid,
        bool isCrossWorld,
        out PartySnapshot? snapshot)
    {
        snapshot = null;

        var addonWrap = Plugin.GameGui.GetAddonByName(addonName);
        if (addonWrap.IsNull)
            return false;
        if (!addonWrap.IsVisible)
            return false;

        var addon = (AtkUnitBase*)addonWrap.Address;
        // Typed view — reliable for reading the name text node. Only valid for _PartyList,
        // not _CrossWorldPartyList (different addon struct).
        var typedAddon = isCrossWorld ? null : (AddonPartyList*)addon;

        var slots = new List<PartySlotInfo>(MaxSlots);
        var unmatched = new List<string>();

        for (var i = 0; i < MaxSlots; i++)
        {
            // Raw node = source of truth for visibility + screen rect
            var node = addon->GetNodeById((uint)(10 + i));
            if (node == null)
                continue;
            if (!node->IsVisible())
                continue;

            // Name — try typed AddonPartyList first (proven to work in duties / overworld
            // trust parties where the abbreviated-name display lives in this node), then
            // fall back to the raw component name search.
            string name = string.Empty;
            if (typedAddon != null)
            {
                var nameNode = typedAddon->PartyMembers[i].Name;
                if (nameNode != null)
                {
                    try
                    {
                        name = nameNode->NodeText.ExtractText().Trim();
                    }
                    catch { /* fall through */ }
                }
            }
            if (string.IsNullOrEmpty(name))
                name = TryReadSlotName(addon, i);

            if (string.IsNullOrEmpty(name))
            {
                unmatched.Add($"slot {i}: (empty)");
                continue;
            }

            if (!TryMatchMember(name, members, out var entry, out var matchedName))
            {
                unmatched.Add($"slot {i}: \"{name}\"");
                continue;
            }

            // The party-member component's *local* bounding box reserves space for elements
            // that may not currently be drawn (MP bar, buff icons, cast bar, etc.). To get
            // the outline to track the *actually visible* card regardless of HUD config,
            // resolution, or scale, we walk the component's visible children and compute
            // the union of their screen-space rectangles.
            var (screenPos, screenSize) = GetVisibleCardBounds(node, addon);

            slots.Add(new PartySlotInfo
            {
                SlotIndex = i,
                ContentId = entry.cid,
                // Use the IPartyList key (full clean name) rather than the raw addon string
                // so stored names don't contain level prefixes ("Lv 100") or abbreviations.
                Name = matchedName,
                WorldName = entry.world,
                ScreenPos = screenPos,
                ScreenSize = screenSize,
                IsLocalPlayer = entry.cid == localCid || string.Equals(matchedName, localName, StringComparison.Ordinal),
            });
        }

        if (slots.Count < 2)
        {
            // Diagnostic: log once when we found visible slots but couldn't match them to IPartyList.
            // Helps diagnose duty-specific name decoration issues.
            if (unmatched.Count > 0)
            {
                Plugin.Log.Debug(
                    "PartyDragSort: {0} addon found {1} visible slot(s) but only {2} matched IPartyList. " +
                    "Unmatched addon names: [{3}]. IPartyList names: [{4}].",
                    addonName,
                    unmatched.Count + slots.Count,
                    slots.Count,
                    string.Join(", ", unmatched.Select(n => $"\"{n}\"")),
                    string.Join(", ", members.Keys.Select(k => $"\"{k}\"")));
            }
            return false;
        }

        snapshot = new PartySnapshot
        {
            AddonName = addonName,
            IsCrossWorld = isCrossWorld,
            Slots = slots,
            LocalPlayerContentId = localCid,
            LocalPlayerName = localName,
        };
        return true;
    }

    /// <summary>
    /// Attempts to match a name read from the party-list addon against the IPartyList dictionary.
    /// In duties the addon often decorates names with payloads (level prefix, role icon, world tag),
    /// so we fall back from exact match → trimmed prefix/suffix containment match.
    /// </summary>
    private static bool TryMatchMember(
        string addonName,
        Dictionary<string, (ulong cid, string world)> members,
        out (ulong cid, string world) entry,
        out string matchedName)
    {
        // 1. Exact match (open-world parties usually hit here).
        if (members.TryGetValue(addonName, out entry))
        {
            matchedName = addonName;
            return true;
        }

        // 2. Substring fallback — handles duty decorations like
        //    "Lv90 Aemilius Tjard", "Aemilius Tjard" (job icon),
        //    or world suffix "Aemilius TjardTonberry".
        var trimmedAddon = addonName.Trim();
        foreach (var kvp in members)
        {
            var memberName = kvp.Key;
            if (string.IsNullOrEmpty(memberName)) continue;

            // The addon text contains the member name as a substring, OR
            // the member name contains the addon text as a substring (rare, but covers truncation).
            if (trimmedAddon.Contains(memberName, StringComparison.Ordinal) ||
                memberName.Contains(trimmedAddon, StringComparison.Ordinal))
            {
                entry = kvp.Value;
                matchedName = kvp.Key; // full IPartyList name, not the decorated addon string
                return true;
            }
        }

        // 3. Abbreviated / token-prefix match — for "Aemilius H." style display.
        //    Strategy: take the trailing word-pair of the addon name (skipping any
        //    leading level/junk like "Lv 100", "???", spaces) and check whether
        //    they're prefixes of a member's first/last name.
        var tokens = trimmedAddon
            .Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.TrimEnd('.'))
            .Where(t => t.Length > 0 && IsLikelyNameToken(t))
            .ToList();

        if (tokens.Count >= 2)
        {
            // Use the LAST two filtered tokens as (first, last) candidate.
            var addonFirst = tokens[tokens.Count - 2];
            var addonLast = tokens[tokens.Count - 1];

            foreach (var kvp in members)
            {
                var memberTokens = kvp.Key.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (memberTokens.Length < 2) continue;

                var memberFirst = memberTokens[0];
                var memberLast = memberTokens[memberTokens.Length - 1];

                if (memberFirst.StartsWith(addonFirst, StringComparison.Ordinal) &&
                    memberLast.StartsWith(addonLast, StringComparison.Ordinal))
                {
                    entry = kvp.Value;
                    matchedName = kvp.Key; // full IPartyList name
                    return true;
                }
            }
        }

        entry = default;
        matchedName = addonName; // fallback (only reached when returning false)
        return false;
    }

    /// <summary>
    /// Heuristic: a token is a "likely name token" if it starts with a letter and isn't a
    /// common level marker like "Lv". Filters out level fragments ("100", "???") so they
    /// don't interfere with name-token matching.
    /// </summary>
    private static bool IsLikelyNameToken(string token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        if (!char.IsLetter(token[0])) return false;
        if (token.Equals("Lv", StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static unsafe string TryReadSlotName(AtkUnitBase* addon, int slotIndex)
    {
        var memberNode = addon->GetNodeById((uint)(10 + slotIndex));
        if (memberNode == null || !memberNode->IsVisible() || (int)memberNode->Type < 1000)
            return string.Empty;

        var component = ((AtkComponentNode*)memberNode)->Component;
        if (component == null)
            return string.Empty;

        var nameNode = component->UldManager.SearchNodeById(15);
        if (nameNode == null || (int)nameNode->Type != 3)
            return string.Empty;

        var textNode = (AtkTextNode*)nameNode;

        // Parse via SeString so that formatting payloads the duty UI adds
        // (level prefix, role/job icons, world tag) get stripped to plain text
        // matching IPartyList.Name.TextValue. The raw ToString() preserves payload
        // bytes which causes the dictionary lookup to silently fail in instances.
        try
        {
            var clean = textNode->NodeText.ExtractText();
            return string.IsNullOrWhiteSpace(clean) ? textNode->NodeText.ToString() : clean.Trim();
        }
        catch
        {
            return textNode->NodeText.ToString();
        }
    }

    /// <summary>
    /// Returns the screen-space rectangle of a party-member slot — i.e. the area
    /// the FFXIV native UI considers "this party member card" for click and hover.
    ///
    /// Why this, not a child-walking union?
    ///   • The slot node is the same node the game itself uses for hit-testing,
    ///     so anywhere the player sees the native hover highlight is anywhere our
    ///     overlay will also register a click. No surprising gaps.
    ///   • <c>ScreenX</c>/<c>ScreenY</c> are absolute coordinates the game updates
    ///     every frame, so this is automatically resolution-, position- and
    ///     HUD-scale-independent.
    ///   • <c>Width</c>/<c>Height</c> are in node-local units; we convert to
    ///     screen-space pixels with the slot's own ScaleX/Y times the addon's
    ///     global Scale (the player's HUD scale setting).
    ///   • No child-tree walking means no fragile assumptions about which nodes
    ///     are nested where, no phantom-visible placeholder pollution, and no
    ///     breakage when other plugins reshape the slot's internals.
    /// </summary>
    private static unsafe (Vector2 pos, Vector2 size) GetVisibleCardBounds(
        AtkResNode* slotNode,
        AtkUnitBase* addon)
    {
        var addonScale = addon->Scale;
        var pos = new Vector2(slotNode->ScreenX, slotNode->ScreenY);
        var size = new Vector2(
            slotNode->Width  * slotNode->ScaleX * addonScale,
            slotNode->Height * slotNode->ScaleY * addonScale);
        return (pos, size);
    }

    /// <summary>
    /// Diagnostic: probes the _PartyList addon directly and reports per-slot what it
    /// finds. Used by /pdragsort debug to figure out why TrySnapshot is failing.
    /// </summary>
    public unsafe string ProbePartyList()
    {
        var sb = new StringBuilder();

        var pState = Plugin.PlayerState;
        sb.AppendLine($"PlayerState.IsLoaded: {pState.IsLoaded}");
        if (pState.IsLoaded)
            sb.AppendLine($"PlayerState.CharacterName: \"{pState.CharacterName}\"");

        // Build the same dictionary TryReadAddon uses
        var partyList = Plugin.PartyList;
        var members = new Dictionary<string, (ulong cid, string world)>(StringComparer.Ordinal);
        foreach (var pm in partyList)
        {
            if (pm == null) continue;
            var worldName = pm.World.ValueNullable?.Name.ToString() ?? string.Empty;
            members[pm.Name.TextValue] = ((ulong)pm.ContentId, worldName);
        }
        sb.AppendLine($"IPartyList keys: [{string.Join(", ", members.Keys.Select(k => $"\"{k}\""))}]");

        var addonWrap = Plugin.GameGui.GetAddonByName(PartyListAddon);
        if (addonWrap.IsNull)
        {
            sb.AppendLine("_PartyList: NOT FOUND");
            return sb.ToString();
        }
        if (!addonWrap.IsVisible)
        {
            sb.AppendLine("_PartyList: not visible");
            return sb.ToString();
        }

        var addon = (AtkUnitBase*)addonWrap.Address;
        sb.AppendLine($"_PartyList ptr: 0x{(ulong)addon:X}, RootNode: {(addon->RootNode == null ? "null" : "ok")}");

        // --- Approach 1: typed AddonPartyList struct (more reliable) ---
        sb.AppendLine();
        sb.AppendLine("--- AddonPartyList typed probe ---");
        try
        {
            var typedAddon = (AddonPartyList*)addon;
            for (var i = 0; i < MaxSlots; i++)
            {
                var member = typedAddon->PartyMembers[i];
                var compNode = member.PartyMemberComponent;
                var nameNode = member.Name;

                if (compNode == null && nameNode == null)
                {
                    sb.AppendLine($"  [{i}] both null");
                    continue;
                }

                var visible = compNode != null && ((AtkResNode*)compNode)->IsVisible();
                var nameText = "(null)";
                if (nameNode != null)
                {
                    try
                    {
                        nameText = $"\"{nameNode->NodeText.ExtractText()}\"";
                    }
                    catch { nameText = $"\"{nameNode->NodeText}\" (raw)"; }
                }

                var matched = nameText.Length > 2
                    ? TryMatchMember(nameText.Trim('"'), members, out _, out _) ? "MATCH" : "no match"
                    : "n/a";
                sb.AppendLine($"  [{i}] comp={(compNode == null ? "null" : "ok")} visible={visible} name={nameText} {matched}");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"  typed probe threw: {ex.Message}");
        }

        // --- Approach 2: raw node ID 10..17 (current production path) ---
        sb.AppendLine();
        sb.AppendLine("--- Raw node ID 10..17 probe (current) ---");
        for (var i = 0; i < MaxSlots; i++)
        {
            var node = addon->GetNodeById((uint)(10 + i));
            if (node == null)
            {
                sb.AppendLine($"  [{i}] GetNodeById({10 + i}) = null");
                continue;
            }
            var visible = node->IsVisible();
            var type = (int)node->Type;
            var name = TryReadSlotName(addon, i);
            var matched = !string.IsNullOrEmpty(name) && TryMatchMember(name, members, out _, out _) ? "MATCH" : "no match";
            sb.AppendLine($"  [{i}] node ok type={type} visible={visible} name=\"{name}\" {matched}");
        }

        return sb.ToString();
    }

    public void Dispose()
    {
    }
}
