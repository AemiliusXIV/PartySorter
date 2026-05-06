using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

namespace PartySorter.Services;

public sealed class DragController : IDisposable
{
    private const string DragPayloadType = "PS_SLOT";
    private const string OverlayWindowId = "##ps_overlay";

    // Pre-allocated button IDs — no per-frame string allocations on the hot path.
    private static readonly string[] ButtonIds;
    static DragController()
    {
        ButtonIds = new string[8];
        for (var i = 0; i < 8; i++)
            ButtonIds[i] = $"##ps_btn_{i}";
    }

    // Window flags that NEVER change between frames.
    //
    // Previous approaches toggled NoInputs on/off every time the modifier key was
    // pressed or released.  Toggling a window flag causes ImGui to recalculate its
    // global input-routing state; it also transitions io.WantCaptureMouse between
    // true and false, which makes Dalamud change how mouse events are routed to
    // FFXIV.  That route-change is expensive enough to produce a visible freeze.
    //
    // Instead, we control "is the overlay receiving input" purely through
    // position and size: the window is parked far off-screen when inactive
    // (can't be hovered → no input capture), and moved on-screen when active.
    // Flags stay identical on every frame, so ImGui never recalculates anything.
    private static readonly ImGuiWindowFlags OverlayFlags =
        ImGuiWindowFlags.NoTitleBar
        | ImGuiWindowFlags.NoResize
        | ImGuiWindowFlags.NoMove
        | ImGuiWindowFlags.NoSavedSettings
        | ImGuiWindowFlags.NoNav
        | ImGuiWindowFlags.NoScrollbar
        | ImGuiWindowFlags.NoScrollWithMouse
        | ImGuiWindowFlags.NoBackground
        | ImGuiWindowFlags.NoFocusOnAppearing
        | ImGuiWindowFlags.NoBringToFrontOnFocus;

    // Park position: safely off every monitor so the window has no hit area and
    // cannot interfere with anything while the modifier is not held.
    private static readonly Vector2 ParkPos  = new(-10_000f, -10_000f);
    private static readonly Vector2 ParkSize = new(1f, 1f);

    private readonly Configuration config;
    private readonly PartySortController controller;

    // One-frame activation delay.
    //
    // On the modifier key-down frame, FFXIV itself does extra work: it swaps every
    // hotbar to its Ctrl/Shift/Alt variant, evaluates keybindings, etc.  If we
    // also transition our overlay to interactive on that same frame, the combined
    // cost can push the frame over budget, causing a stutter.
    //
    // Deferring activation by exactly one frame (≈16 ms, imperceptible to the user)
    // puts our transition on a frame where FFXIV is back to its normal budget.
    // Deactivation is immediate so input is released the moment the key is lifted.
    private bool prevWantsInteractive;

    public DragController(Configuration config, PartySortController controller)
    {
        this.config     = config;
        this.controller = controller;
    }

    public void Draw()
    {
        var snapshot    = controller.LastSnapshot;
        var hasParty    = snapshot is { IsCrossWorld: false } && snapshot.Slots.Count >= 2;

        var wantsInteractive = hasParty && ModifierHeld() && !IsGameTextInputActive();
        var interactive      = prevWantsInteractive && wantsInteractive; // 1-frame delay
        prevWantsInteractive = wantsInteractive;

        // Always call DrawOverlay so the window is kept alive in ImGui state even
        // when there is no party — this avoids a one-time window-creation cost the
        // first time the user presses the modifier in a new session/zone.
        DrawOverlay(snapshot, interactive);
    }

    private void DrawOverlay(PartySnapshot? snapshot, bool interactive)
    {
        var winPos  = ParkPos;
        var winSize = ParkSize;

        if (interactive && snapshot != null)
        {
            // Compute the union bounding rect of all visible party-slot screen rects.
            var minX = float.MaxValue; var minY = float.MaxValue;
            var maxX = float.MinValue; var maxY = float.MinValue;

            foreach (var slot in snapshot.Slots)
            {
                if (slot.ScreenSize.X < 4 || slot.ScreenSize.Y < 4) continue;
                if (slot.ScreenPos.X < minX) minX = slot.ScreenPos.X;
                if (slot.ScreenPos.Y < minY) minY = slot.ScreenPos.Y;
                var r = slot.ScreenPos.X + slot.ScreenSize.X;
                var b = slot.ScreenPos.Y + slot.ScreenSize.Y;
                if (r > maxX) maxX = r;
                if (b > maxY) maxY = b;
            }

            if (minX < maxX && minY < maxY)
            {
                winPos  = new Vector2(minX, minY);
                winSize = new Vector2(maxX - minX, maxY - minY);
            }
            else
            {
                interactive = false; // no valid slots — fall back to parked
            }
        }

        ImGui.SetNextWindowPos(winPos);
        ImGui.SetNextWindowSize(winSize);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, Vector2.Zero);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 0f);

        if (ImGui.Begin(OverlayWindowId, OverlayFlags) && interactive && snapshot != null)
        {
            // Fully transparent buttons — no hover/active fill, no border.
            ImGui.PushStyleColor(ImGuiCol.Button,        Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, Vector4.Zero);
            ImGui.PushStyleColor(ImGuiCol.ButtonActive,  Vector4.Zero);

            foreach (var slot in snapshot.Slots)
            {
                if (slot.ScreenSize.X < 4 || slot.ScreenSize.Y < 4) continue;

                // Jump cursor to this slot's position within the overlay window.
                // WindowPadding is zero so content origin == window top-left.
                ImGui.SetCursorPos(slot.ScreenPos - winPos);

                var isDraggable = slot.SlotIndex != 0 && !slot.IsLocalPlayer;
                ImGui.InvisibleButton(ButtonIds[slot.SlotIndex], slot.ScreenSize);

                if (isDraggable && ImGui.BeginDragDropSource())
                {
                    ImGui.SetDragDropPayload(DragPayloadType,
                        BitConverter.GetBytes(slot.SlotIndex), ImGuiCond.Always);
                    ImGui.TextUnformatted($"Move {slot.Name}");
                    ImGui.EndDragDropSource();
                }

                if (ImGui.BeginDragDropTarget())
                {
                    var payload = ImGui.AcceptDragDropPayload(DragPayloadType,
                        ImGuiDragDropFlags.None);
                    if (!payload.IsNull && payload.IsDelivery() && payload.DataSize >= sizeof(int))
                    {
                        unsafe { controller.HandleDrop(*(int*)payload.Data, slot.SlotIndex); }
                    }
                    ImGui.EndDragDropTarget();
                }
            }

            ImGui.PopStyleColor(3);
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
    }

    /// <summary>
    /// Returns true when a native FFXIV text-input addon is on screen, so the
    /// modifier-key overlay should not activate.  WantCaptureKeyboard only covers
    /// ImGui (Dalamud) windows; this catches the game-side dialogs.
    ///
    /// _ChatLog is intentionally not checked: the addon is always visible while
    /// the UI is shown, and reliably detecting "player is typing in chat" requires
    /// knowing the exact internal node ID that becomes visible on Enter, which
    /// varies across patches — checking the wrong node silently kills all
    /// dragging.  The Ctrl default already prevents accidental activation while
    /// typing because Ctrl is rarely held continuously during chat entry.
    /// </summary>
    private static bool IsGameTextInputActive()
    {
        ReadOnlySpan<string> textInputAddons = [
            "InputNumeric",    // numeric entry dialogs
            "VirtualKeyboard", // on-screen keyboard (controller / accessibility)
            "Rename",          // rename retainer, FC, chocobo, etc.
        ];
        foreach (var name in textInputAddons)
        {
            var wrap = Plugin.GameGui.GetAddonByName(name);
            if (!wrap.IsNull && wrap.IsVisible)
                return true;
        }
        return false;
    }

    private bool ModifierHeld()
    {
        var io = ImGui.GetIO();
        return config.Modifier switch
        {
            DragModifier.Shift => io.KeyShift,
            DragModifier.Ctrl  => io.KeyCtrl,
            DragModifier.Alt   => io.KeyAlt,
            _                  => io.KeyCtrl,
        };
    }

    public void Dispose() { }
}
