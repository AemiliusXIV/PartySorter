using System;
using System.Collections.Generic;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Shell;

namespace PartySorter.Automation;

public sealed class ChatSender : IDisposable
{
    private readonly Queue<string> queue = new();
    private DateTime nextDispatchUtc = DateTime.MinValue;
    private const int MinDispatchIntervalMs = 100;
    private const int MaxMessageLength = 500;

    public int PendingCount => queue.Count;

    public void Enqueue(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        if (message.Length > MaxMessageLength) return;
        if (!message.StartsWith('/')) return;
        foreach (var ch in message)
        {
            if (ch < 0x20) return;
        }
        queue.Enqueue(message);
    }

    public void Tick()
    {
        if (queue.Count == 0) return;
        var now = DateTime.UtcNow;
        if (now < nextDispatchUtc) return;

        var msg = queue.Dequeue();
        try
        {
            SendInternal(msg);
            nextDispatchUtc = now.AddMilliseconds(MinDispatchIntervalMs);
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Failed to dispatch chat message: {0}", msg);
        }
    }

    public void Clear()
    {
        queue.Clear();
    }

    private static unsafe void SendInternal(string message)
    {
        var framework = Framework.Instance();
        if (framework == null) throw new InvalidOperationException("Framework not ready");
        var uiModule = framework->GetUIModule();
        if (uiModule == null) throw new InvalidOperationException("UIModule not ready");
        var shell = RaptureShellModule.Instance();
        if (shell == null) throw new InvalidOperationException("RaptureShellModule not ready");

        var utf = Utf8String.FromString(message);
        try
        {
            shell->ExecuteCommandInner(utf, uiModule);
        }
        finally
        {
            if (utf != null)
            {
                utf->Dtor(true);
            }
        }
    }

    public void Dispose()
    {
        queue.Clear();
    }
}
