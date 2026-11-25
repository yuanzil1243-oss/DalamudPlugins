using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;
using MoveSpeedLab;

internal sealed unsafe class SpeedBackendChecks : IDisposable, ISigScanner, IGameInteropProvider
{
    private delegate float Target(WalkController* walk);
    private delegate void Update(WalkController* walk);
    private readonly NativeSpeedBackend backend;
    private readonly WalkController* walk;
    private Target target = null!;
    private Update update = null!;
    private SpeedContext context = new(new(770, 2, 1), true, true, true, true, FloorKind.Normal, "ready");
    private float nativeTarget = 6, increment = .05f;
    private int targetCalls, updateCalls;
    private bool nativeZero, noGetter, throwTarget, changeContextAfterGetter;
    private bool releaseAfterGetter;
    private float forcedSpeed = 3;
    private object targetHook = null!, updaterHook = null!;

    private static void Require(bool value) { if (!value) throw new Exception("Production backend regression"); }
    private SpeedBackendChecks()
    {
        walk = (WalkController*)NativeMemory.AllocZeroed((nuint)sizeof(WalkController));
        *walk = new() { Parent = 123, BaseSpeed = 6, SpeedUpdateActive = 1, HasInput = 1 };
        backend = new(this, this, () => context);
        var type = typeof(NativeSpeedBackend);
        Set("walkAddress", (nint)walk);
        targetHook = Inject("hook", nameof(OriginalTarget)); updaterHook = Inject("updateHook", nameof(OriginalUpdate));
        target = type.GetMethod("Detour", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Target>(backend);
        update = type.GetMethod("UpdateDetour", BindingFlags.NonPublic | BindingFlags.Instance)!.CreateDelegate<Update>(backend);
        Set("entry", type.GetMethod("Detour", BindingFlags.NonPublic | BindingFlags.Instance)!
            .CreateDelegate(type.GetField("entry", BindingFlags.NonPublic | BindingFlags.Instance)!.FieldType, backend));
    }
    private void Set(string field, object value) => typeof(NativeSpeedBackend)
        .GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(backend, value);
    private object Inject(string field, string original)
    {
        var hookField = typeof(NativeSpeedBackend).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var method = GetType().GetMethod(original, BindingFlags.NonPublic | BindingFlags.Instance)!;
        var hook = Activator.CreateInstance(hookField.FieldType, method.CreateDelegate(hookField.FieldType.GenericTypeArguments[0], this))!;
        hook.GetType().GetMethod("Enable")!.Invoke(hook, null); hookField.SetValue(backend, hook); return hook;
    }
    private float OriginalTarget(WalkController* controller)
    {
        targetCalls++; if (throwTarget) throw new InvalidOperationException("injected getter failure");
        return controller->SpeedUpdateActive != 0 ? nativeTarget : 0;
    }
    private void OriginalUpdate(WalkController* controller)
    {
        updateCalls++;
        if (noGetter) { controller->CurrentSpeed = forcedSpeed; return; }
        if (controller->SpeedUpdateActive == 0) return;
        float ceiling = target(controller);
        controller->CurrentSpeed = nativeZero ? 0 : Math.Max(0, Math.Min(controller->CurrentSpeed + increment, ceiling));
        if (changeContextAfterGetter) context = context with { CanWalk = false };
        if (releaseAfterGetter) backend.Release();
    }
    private void Publish(float factor = 2)
    {
        var d = SpeedPolicy.Decide(new(true, true, false, true, factor), context, true);
        backend.Publish(d, context, Environment.TickCount64);
    }
    private void Tick() => update(walk);
    private bool Disposed(object hook) => (bool)hook.GetType().GetProperty("Disposed")!.GetValue(hook)!;
    public void Dispose() { backend.Dispose(); NativeMemory.Free(walk); }
    public IEnumerable<nint> ScanAllText(string signature) => [];
    public nint ScanText(string signature) => throw new NotSupportedException();
    public nint GetStaticAddressFromSig(string signature) => throw new NotSupportedException();
    public Dalamud.Hooking.Hook<T> HookFromAddress<T>(nint address, T detour) where T : Delegate => throw new NotSupportedException();

    internal static void Run(Action<string, Action> check)
    {
        foreach (float factor in new[] { .1f, .5f, 1, 1.2f, 2, 10 })
            check("backend-real-callback-first-update-" + factor, () =>
            {
                using var t = new SpeedBackendChecks(); t.Publish(factor); t.Tick();
                Require(t.walk->CurrentSpeed == 6 * factor && t.updateCalls == 1 && t.targetCalls == 1);
                Require(!t.backend.Released); t.backend.Release();
                Require(t.backend.Released && t.walk->CurrentSpeed == Math.Min(6 * factor, 6));
            });
        check("backend-restores-before-next-native-update-and-releases-to-fresh-slow", () =>
        {
            using var t = new SpeedBackendChecks(); t.Publish(); t.Tick();
            t.nativeTarget = 2.4f; t.Tick(); Require(t.walk->CurrentSpeed == 4.8f);
            t.nativeTarget = 1.2f; t.backend.Release(); Require(t.walk->CurrentSpeed == 1.2f);
            t.Tick(); Require(t.walk->CurrentSpeed == 1.2f);
        });
        check("backend-probe-has-no-field-write-or-natural-call-accounting", () =>
        {
            using var t = new SpeedBackendChecks(); t.Publish(); var o = t.backend.Probe(123);
            Require(o is { OriginalTarget: 6, ReturnedTarget: 12 } && t.walk->CurrentSpeed == 0);
            Require(t.backend.NativeCalls == 0 && t.backend.AdjustedNativeCalls == 0);
            t.Tick(); Require(t.backend.NativeCalls == 1 && t.backend.AdjustedNativeCalls == 1);
        });
        check("backend-native-zero-and-no-getter-forced-branches-preserved", () =>
        {
            using var t = new SpeedBackendChecks(); t.Publish(); t.Tick();
            t.noGetter = true; t.forcedSpeed = 12; t.Tick();
            Require(t.walk->CurrentSpeed == 12); t.backend.Release(); Require(t.walk->CurrentSpeed == 12);
            t.noGetter = false; t.nativeZero = true; t.Publish(); t.Tick(); Require(t.walk->CurrentSpeed == 0);
        });
        check("backend-context-changes-during-native-update-cannot-snap", () =>
        {
            using var t = new SpeedBackendChecks(); t.Publish(); t.changeContextAfterGetter = true; t.Tick();
            Require(t.walk->CurrentSpeed == .05f);
        });
        check("backend-reentrant-release-cannot-be-followed-by-a-late-snap", () =>
        {
            using var t = new SpeedBackendChecks(); t.Publish(); t.releaseAfterGetter = true; t.Tick();
            Require(t.backend.Released && t.walk->CurrentSpeed == .05f && t.updateCalls == 1);
        });
        check("backend-ungranted-callbacks-keep-native-update-once", () =>
        {
            using var t = new SpeedBackendChecks(); t.Tick();
            Require(t.walk->CurrentSpeed == .05f && t.targetCalls == 1 && t.updateCalls == 1);
        });
        check("backend-invalid-native-restoration-faults-without-inventing-speed", () =>
        {
            using var t = new SpeedBackendChecks(); t.Publish(); t.Tick(); t.nativeTarget = float.NaN;
            t.backend.Release(); Require(t.backend.Released && t.backend.RuntimeError != null);
        });
        check("backend-expired-lease-stops-scaling-and-snapping", () =>
        {
            using var t = new SpeedBackendChecks(); t.Publish(); t.Tick();
            t.Set("lease", new SpeedLease(true, 2, t.context.Identity, Environment.TickCount64 - 1, true));
            t.Tick(); Require(t.walk->CurrentSpeed == 6);
        });
        check("backend-release-on-boss-and-rearm-next-group", () =>
        {
            using var t = new SpeedBackendChecks(); t.Publish(); t.Tick();
            t.context = t.context with { Identity = new(770, 2, 10), Kind = FloorKind.Boss };
            t.Publish(); Require(t.backend.Released && t.walk->CurrentSpeed == 6);
            t.context = t.context with { Identity = new(771, 2, 11), Kind = FloorKind.Normal };
            t.Publish(); t.Tick(); Require(t.walk->CurrentSpeed == 12);
        });
        check("backend-getter-fault-latches-and-removes-grant", () =>
        {
            using var t = new SpeedBackendChecks(); t.Publish(); t.throwTarget = true; t.Tick();
            Require(t.backend.Released && t.backend.RuntimeError != null && t.updateCalls == 1);
            t.Publish(); Require(t.backend.Released);
        });
        check("backend-disable-dispose-releases-before-both-hooks-removed", () =>
        {
            using var t = new SpeedBackendChecks(); t.Publish(); t.Tick(); t.nativeTarget = 3;
            t.backend.Dispose(); Require(t.walk->CurrentSpeed == 3 && t.backend.Released && !t.backend.Initialized);
            Require(t.Disposed(t.targetHook) && t.Disposed(t.updaterHook)); t.backend.Dispose();
        });
        check("backend-missing-signatures-fail-before-any-hook-install", () =>
        {
            using var t = new SpeedBackendChecks(); t.backend.Dispose();
            using var fresh = new NativeSpeedBackend(t, t, () => t.context);
            Require(!fresh.TryInitialize(out var error) && error.Contains("签名") && fresh.Released);
        });
    }
}
