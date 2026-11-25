// SPDX-License-Identifier: GPL-3.0-only
// Native layout/signatures adapted from AtmoOmen/OmenTools (MIT); see THIRD-PARTY-NOTICES.
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace MoveSpeedLab;

/// <summary>
/// Owns the target getter and a disk-verified linear-speed updater hook.
/// Snaps only the updater's positive derived CurrentSpeed. No manual opcode/data-table patches,
/// input/coordinate writes, or external plugin settings. Release clamps its own value to a fresh native target.
/// Initialize/Publish/Release/Probe/Dispose are framework-thread operations.
/// </summary>
public sealed unsafe class NativeSpeedBackend : IDisposable
{
    internal const string TargetSpeedSignature = "E8 ?? ?? ?? ?? 0F 28 F0 E9 ?? ?? ?? ?? 8B 81";
    internal const string WalkInstanceSignature = "48 8D 0D ?? ?? ?? ?? 45 0F 28 D4";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float TargetSpeedDelegate(WalkController* controller);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void UpdateSpeedDelegate(WalkController* controller);
    private readonly ISigScanner scanner;
    private readonly IGameInteropProvider interop;
    private readonly Func<SpeedContext> liveContext;
    private Hook<TargetSpeedDelegate>? hook;
    private Hook<UpdateSpeedDelegate>? updateHook;
    private TargetSpeedDelegate? entry;
    private nint walkAddress;
    private SpeedLease lease = SpeedLease.Released;
    private long calls, adjustedCalls;
    private bool disposed;
    private string? runtimeError;
    private readonly object nativeGate = new();
    private readonly InstantStartCorrection correction = new();
    [ThreadStatic] private static UpdateFrame activeUpdate;
    [ThreadStatic] private static bool probing;
    [ThreadStatic] private static bool probeSawOwnHook;
    [ThreadStatic] private static float probeOriginal;

    private struct UpdateFrame
    {
        internal NativeSpeedBackend? Owner;
        internal nint Controller;
        internal SpeedLease? Lease;
        internal int GetterCalls;
        internal float NativeTarget, ReturnedTarget;
    }

    public NativeSpeedBackend(ISigScanner scanner, IGameInteropProvider interop, Func<SpeedContext> liveContext)
    {
        this.scanner = scanner;
        this.interop = interop;
        this.liveContext = liveContext;
    }
    public bool Initialized => !disposed && hook?.IsEnabled == true && updateHook?.IsEnabled == true;
    public bool Released => Volatile.Read(ref lease) is { Apply: false, InstantStart: false };
    public long NativeCalls => Interlocked.Read(ref calls);
    public long AdjustedNativeCalls => Interlocked.Read(ref adjustedCalls);
    public string? RuntimeError => Volatile.Read(ref runtimeError);

    public bool TryInitialize(out string error)
    {
        error = "";
        if (disposed) { error = "速度模块已卸载。"; return false; }
        if (Initialized) return true;
        try
        {
            // Fail closed on ambiguous matches instead of accepting the first match.
            if (scanner.ScanAllText(TargetSpeedSignature).Take(2).Count() != 1 ||
                scanner.ScanAllText(WalkInstanceSignature).Take(2).Count() != 1 ||
                scanner.ScanAllText(NativeSpeedContract.UpdateSignature).Take(2).Count() != 1)
                throw new InvalidOperationException("原生签名缺失或存在多重匹配，拒绝安装 Hook。");
            // These are the same ISigScanner operations used by upstream OmenTools.CompSig.
            // ScanText resolves the leading relative CALL; no absolute address is invented.
            nint target = scanner.ScanText(TargetSpeedSignature);
            nint updater = scanner.ScanText(NativeSpeedContract.UpdateSignature);
            walkAddress = scanner.GetStaticAddressFromSig(WalkInstanceSignature);
            if (target == 0 || updater == 0 || walkAddress == 0) throw new InvalidOperationException("原生签名未解析。");
            byte[] body = new byte[NativeSpeedContract.UpdateLength];
            Marshal.Copy(updater, body, 0, body.Length);
            if (!NativeSpeedContract.Matches(body, updater, target))
                throw new InvalidOperationException("本机起步更新函数与已核验原生契约不符；拒绝安装移速 Hook。");
            if (!ValidController((WalkController*)walkAddress))
                throw new InvalidOperationException("本机行走结构尚未就绪或与上游定义不符；请登录并落地后再试。");
            entry = Marshal.GetDelegateForFunctionPointer<TargetSpeedDelegate>(target);
            hook = interop.HookFromAddress<TargetSpeedDelegate>(target, Detour);
            updateHook = interop.HookFromAddress<UpdateSpeedDelegate>(updater, UpdateDetour);
            hook.Enable();
            updateHook.Enable();
            Volatile.Write(ref runtimeError, null);
            return true;
        }
        catch (Exception ex)
        {
            Release();
            try { updateHook?.Dispose(); }
            finally { hook?.Dispose(); updateHook = null; hook = null; entry = null; }
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    private static bool ValidController(WalkController* controller) =>
        WalkControllerValidation.IsValid(controller);

    public void Publish(in SpeedDecision decision, in SpeedContext context, long now)
    {
        lock (nativeGate)
        {
            if (!Initialized || RuntimeError != null) { ReleaseCore(); return; }
            var next = SpeedLease.Create(decision, context, now);
            if (!next.InstantStart || next.Multiplier != Volatile.Read(ref lease).Multiplier) ReleaseCore();
            Volatile.Write(ref lease, next);
        }
    }
    public void Release() { lock (nativeGate) ReleaseCore(); }

    private void ReleaseCore()
    {
        Volatile.Write(ref lease, SpeedLease.Released);
        RestoreOwnedCore();
    }

    private void RestoreOwnedCore()
    {
        try
        {
            var walk = (WalkController*)walkAddress;
            if (hook != null && correction.Owns(walk))
            {
                float nativeTarget = hook.Original(walk);
                if (!float.IsFinite(nativeTarget) || nativeTarget < 0)
                    throw new InvalidOperationException("原生目标无效，不能猜测恢复速度。");
                correction.Restore(walk, nativeTarget);
            }
        }
        catch (Exception ex)
        {
            Volatile.Write(ref lease, SpeedLease.Released);
            Volatile.Write(ref runtimeError, "原生起步撤销观察失败：" + ex.GetType().Name);
        }
        finally { correction.Forget(); }
    }

    private void UpdateDetour(WalkController* controller)
    {
        lock (nativeGate)
        {
            UpdateFrame previous = activeUpdate;
            activeUpdate = new() { Owner = this, Controller = (nint)controller, Lease = Volatile.Read(ref lease) };
            try
            {
                // Remove the previous update's owned excess BEFORE native logic sees CurrentSpeed.
                // A forced/no-getter native branch then remains the sole owner of its output.
                RestoreOwnedCore();
                // Keep all native activation, acceleration, forced-movement, and zero-result gates.
                // The getter must have been observed in THIS update; never call it to manufacture movement.
                updateHook!.Original(controller);
                var frame = activeUpdate;
                var current = Volatile.Read(ref lease);
                if (frame.GetterCalls == 1 && ReferenceEquals(frame.Lease, current) && current.InstantStart &&
                    (nint)controller == walkAddress && ValidController(controller) &&
                    current.Allows(liveContext(), Environment.TickCount64) &&
                    correction.Apply(controller, frame.NativeTarget, frame.ReturnedTarget))
                    return;
                correction.Forget();
            }
            catch (Exception ex)
            {
                ReleaseCore();
                Volatile.Write(ref runtimeError, "原生起步回调失败，修正已撤销：" + ex.GetType().Name);
            }
            finally { activeUpdate = previous; }
        }
    }

    private float Detour(WalkController* controller)
    {
        // Call the original exactly once, even when disabled or the live observation fails.
        float original = hook!.Original(controller);
        if (probing) { probeOriginal = original; probeSawOwnHook = true; }
        else Interlocked.Increment(ref calls);
        float result = original;
        try
        {
            SpeedLease current = Volatile.Read(ref lease);
            if ((current.Apply || current.InstantStart) && (nint)controller == walkAddress && ValidController(controller) &&
                controller->InputLocked == 0 && controller->Jumping == 0 &&
                current.Allows(liveContext(), Environment.TickCount64))
            {
                result = SpeedPolicy.Adjust(original, current.Multiplier, current.Apply);
                if (!probing && activeUpdate.Owner == this && activeUpdate.Controller == (nint)controller &&
                    ReferenceEquals(activeUpdate.Lease, current))
                {
                    activeUpdate.GetterCalls++;
                    activeUpdate.NativeTarget = original;
                    activeUpdate.ReturnedTarget = result;
                }
            }
        }
        catch (Exception ex)
        {
            Release();
            Volatile.Write(ref runtimeError, "原生回调观察失败，修正已撤销：" + ex.GetType().Name);
            result = original;
        }
        if (float.IsFinite(original) && float.IsFinite(result) && result != original)
        {
            if (!probing) Interlocked.Increment(ref adjustedCalls);
        }
        return result;
    }

    /// <summary>Read-only getter probe, kept separate from naturally occurring game calls.</summary>
    public SpeedObservation? Probe(long now)
    {
        if (!Initialized || entry == null || !ValidController((WalkController*)walkAddress)) return null;
        if (probing) return null;
        try
        {
            probing = true; probeSawOwnHook = false;
            var walk = (WalkController*)walkAddress;
            float returned = entry(walk);
            if (!probeSawOwnHook) return null;
            return new(now, probeOriginal, returned, walk->CurrentSpeed, walk->BaseSpeed, walk->HasInput != 0);
        }
        catch (Exception ex)
        {
            Release();
            Volatile.Write(ref runtimeError, "原生速度观测失败：" + ex.GetType().Name);
            return null;
        }
        finally { probing = false; }
    }

    public void Dispose()
    {
        if (disposed) return;
        Release();
        // Keep the original delegate reachable until the hook is removed.
        try { updateHook?.Disable(); }
        finally
        {
            try { updateHook?.Dispose(); }
            finally
            {
                try { hook?.Disable(); }
                finally
                {
                    try { hook?.Dispose(); }
                    finally { updateHook = null; hook = null; entry = null; disposed = true; }
                }
            }
        }
    }
}
