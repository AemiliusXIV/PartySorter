using System;
using System.Collections.Generic;
using System.Linq;
using FFXIVClientStructs.FFXIV.Client.UI.Info;

namespace PartySorter.Services;

/// <summary>
/// Reorders party members using the native <c>InfoProxyPartyMember.ChangeOrder()</c> API
/// exposed by FFXIVClientStructs.
///
/// --- Architecture decision: why native instead of /partysort text commands ---
///
/// The original v0.1 design dispatched /partysort via RaptureShellModule.ExecuteCommandInner.
/// That approach is safe and stable (text commands are a public, documented game API), but it
/// is throttled by the game's chat input rate (~100 ms minimum between commands), meaning
/// moving a member across 6 slots took ~600 ms and 6 sequential round-trips.
///
/// InfoProxyPartyMember.ChangeOrder(int selectedIndex, int targetIndex, bool doUpdate) is the
/// same function /partysort calls internally. Calling it directly removes all throttling —
/// a 6-slot move resolves in a single frame (~16 ms). The third argument (doUpdate) signals
/// whether the game should broadcast the new order to the server immediately; passing true
/// matches the behaviour of /partysort.
///
/// --- How to revert if ChangeOrder breaks after a patch ---
///
/// If SE changes the InfoProxyPartyMember struct layout and ChangeOrder offsets break,
/// the original /partysort text-command path can be restored from git history:
/// 1. Retrieve Automation/ChatSender.cs from the "Initial public release" commit.
/// 2. In Plugin.cs: add the ChatSender service back and replace <c>new PartyReorderer()</c>
///    with <c>new ChatSender()</c>.
/// 3. In PartySortController.cs: swap the <c>reorderer.DispatchMovesToTarget</c> calls
///    back to the enqueue + chat.Tick() pattern that was in place before 7.5.
///
/// The /partysort fallback adds ~40x latency per swap but is structurally simpler and will
/// work for as long as the text command itself exists.
/// </summary>
public sealed class PartyReorderer : IDisposable
{
    private const double PostDispatchCooldownSec = 0.5;
    private DateTime cooldownUntilUtc = DateTime.MinValue;
    private bool lastAttemptFailed = false;

    public bool CanDispatch => DateTime.UtcNow >= cooldownUntilUtc && !lastAttemptFailed;

    public void DispatchMovesToTarget(List<ulong> current, List<ulong> target)
    {
        if (!CanDispatch) return;
        if (current.Count != target.Count) return;

        lastAttemptFailed = false;
        try
        {
            var moves = ComputeSwapsToTransform(current, target);
            if (moves.Count == 0) return;

            unsafe
            {
                var infoProxy = InfoProxyPartyMember.Instance();
                if (infoProxy == null) return;

                foreach (var (a, b) in moves)
                {
                    if (a == 0 || b == 0) continue;
                    infoProxy->ChangeOrder(a, b, true);
                }
            }

            Plugin.Log.Information("PartySorter: performed {0} native reorder(s) (auto-reapply).", moves.Count);
            cooldownUntilUtc = DateTime.UtcNow.AddSeconds(PostDispatchCooldownSec);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "PartyReorderer.DispatchMovesToTarget failed");
            lastAttemptFailed = true;
        }
    }

    internal static List<(int a, int b)> ComputeSwapsToTransform(List<ulong> current, List<ulong> target)
    {
        var moves = new List<(int a, int b)>();
        if (current.Count != target.Count) return moves;
        var working = new List<ulong>(current);

        for (var i = 1; i < target.Count; i++)
        {
            if (working[i] == target[i]) continue;
            var idx = working.IndexOf(target[i], i);
            if (idx <= 0) continue;
            for (var j = idx; j > i; j--)
            {
                moves.Add((j - 1, j));
                (working[j - 1], working[j]) = (working[j], working[j - 1]);
            }
        }

        return moves;
    }

    public void Dispose()
    {
    }
}
