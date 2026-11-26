using System.Numerics;
using DeepDungeon.Fsd.Core;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace DeepDungeon.Fsd.Dalamud;

/// <summary>Read-only server receipt observation. Does not issue, block, or modify actions or packets.</summary>
internal sealed unsafe class GcdTriggerReceiptObserver : IDisposable
{
    private readonly Hook<ActionEffectHandler.Delegates.Receive> hook;
    private readonly GcdTriggerConfirmation confirmation;
    private readonly Func<bool> eligible;
    private volatile bool disposed;

    internal GcdTriggerReceiptObserver(GcdTriggerConfirmation confirmation, Func<bool> eligible)
    {
        this.confirmation = confirmation;
        this.eligible = eligible;
        hook = Service.GameInteropProvider.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
            (nint)ActionEffectHandler.MemberFunctionPointers.Receive, Receive);
        try { hook.Enable(); }
        catch { hook.Dispose(); throw; }
    }

    private void Receive(uint actor, Character* caster, Vector3* position, ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects, GameObjectId* targets)
    {
        hook.OriginalDisposeSafe(actor, caster, position, header, effects, targets);
        if (disposed) return;
        try
        {
            if (header == null || !eligible() || actor != (Service.LocalPlayer?.EntityId ?? 0)) return;
            long now = Environment.TickCount64;
            bool action = header->ActionType == (byte)ActionType.Action;
            if (confirmation.Observe(now, actor, header->ActionId, header->SourceSequence,
                header->AnimationTargetId.Id, action)) return;
            if (targets == null || header->NumTargets > 32) return;
            for (int i = 0; i < header->NumTargets; i++)
                if (confirmation.Observe(now, actor, header->ActionId, header->SourceSequence, targets[i].Id, action)) return;
        }
        catch { /* Counters never affect the original action-effect chain. */ }
    }
    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        confirmation.Reset();
        try { hook.Disable(); } finally { hook.Dispose(); }
    }
}
