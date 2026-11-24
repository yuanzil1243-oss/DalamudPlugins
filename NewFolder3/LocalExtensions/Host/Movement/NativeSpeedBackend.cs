// SPDX-License-Identifier: GPL-3.0-only
// Native layout/signatures adapted from AtmoOmen/OmenTools (MIT); see THIRD-PARTY-NOTICES.
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace MoveSpeedLab;

/// <summary>
/// Owns only a return-value hook. No native speed/coordinate writes, no game command,
/// no external plugin settings, and no per-frame native patching.
/// Initialize/Publish/Release/Probe/Dispose are framework-thread operations.
/// </summary>
public sealed unsafe class NativeSpeedBackend : IDisposable
{
    internal const string TargetSpeedSignature = "E8 ?? ?? ?? ?? 0F 28 F0 E9 ?? ?? ?? ?? 8B 81";
    internal const string WalkInstanceSignature = "48 8D 0D ?? ?? ?? ?? 45 0F 28 D4";
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float TargetSpeedDelegate(WalkController* controller);
    private readonly ISigScanner scanner;
    private readonly IGameInteropProvider interop;
    private readonly Func<SpeedContext> liveContext;
    private Hook<TargetSpeedDelegate>? hook;
    private TargetSpeedDelegate? entry;
    private nint walkAddress;
    private SpeedLease lease = SpeedLease.Released;
    private long calls, adjustedCalls;
    private bool disposed;
    private string? runtimeError;
    [ThreadStatic] private static bool probing;
    [ThreadStatic] private static bool probeSawOwnHook;
    [ThreadStatic] private static float probeOriginal;

    public NativeSpeedBackend(ISigScanner scanner, IGameInteropProvider interop, Func<SpeedContext> liveContext)
    {
        this.scanner = scanner;
        this.interop = interop;
        this.liveContext = liveContext;
    }
    public bool Initialized => !disposed && hook?.IsEnabled == true;
    public bool Released => !Volatile.Read(ref lease).Apply;
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
                scanner.ScanAllText(WalkInstanceSignature).Take(2).Count() != 1)
                throw new InvalidOperationException("原生签名缺失或存在多重匹配，拒绝安装 Hook。");
            // These are the same ISigScanner operations used by upstream OmenTools.CompSig.
            // ScanText resolves the leading relative CALL; no absolute address is invented.
            nint target = scanner.ScanText(TargetSpeedSignature);
            walkAddress = scanner.GetStaticAddressFromSig(WalkInstanceSignature);
            if (target == 0 || walkAddress == 0) throw new InvalidOperationException("原生签名未解析。");
            if (!ValidController((WalkController*)walkAddress))
                throw new InvalidOperationException("本机行走结构尚未就绪或与上游定义不符；请登录并落地后再试。");
            entry = Marshal.GetDelegateForFunctionPointer<TargetSpeedDelegate>(target);
            hook = interop.HookFromAddress<TargetSpeedDelegate>(target, Detour);
            hook.Enable();
            Volatile.Write(ref runtimeError, null);
            return true;
        }
        catch (Exception ex)
        {
            Release();
            hook?.Dispose(); hook = null; entry = null;
            error = ex.GetBaseException().Message;
            return false;
        }
    }

    private static bool ValidController(WalkController* controller) =>
        WalkControllerValidation.IsValid(controller);

    public void Publish(in SpeedDecision decision, in SpeedContext context, long now)
    {
        if (!Initialized || RuntimeError != null) { Release(); return; }
        Volatile.Write(ref lease, SpeedLease.Create(decision, context, now));
    }
    public void Release() => Volatile.Write(ref lease, SpeedLease.Released);

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
            if (current.Apply && (nint)controller == walkAddress && ValidController(controller) &&
                controller->InputLocked == 0 && controller->Jumping == 0 &&
                current.Allows(liveContext(), Environment.TickCount64))
                result = SpeedPolicy.Adjust(original, current.Multiplier, true);
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
        try { hook?.Disable(); }
        finally
        {
            try { hook?.Dispose(); }
            finally { hook = null; entry = null; disposed = true; }
        }
    }
}
