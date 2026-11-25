using System.Runtime.InteropServices;
using System.Reflection.PortableExecutable;
using Dalamud.Hooking.Internal;
using Dalamud.Plugin.Services;
using MoveSpeedLab;

internal static unsafe class RegisteredHookChecks
{
    private static void Require(bool value) { if (!value) throw new Exception("Registered-hook compatibility regression"); }

    internal static void Run(Action<string, Action> check)
    {
        foreach (int size in new[] { -1, 0, 4, 51 })
            check("registered-hook-invalid-prefix-size-" + size, () =>
                Require(!NativeSpeedContract.HasSupportedEntryJump(new byte[60], size)));
        foreach (int kind in new[] { 5, 6, 7 })
            check("registered-hook-known-entry-layout-" + kind, () =>
            {
                byte[] body = Enumerable.Repeat((byte)0x90, 10).ToArray();
                if (kind == 5) body[0] = 0xe9;
                if (kind == 6) { body[0] = 0xff; body[1] = 0x25; }
                if (kind == 7) { body[0] = 0xff; body[1] = 0x24; body[2] = 0x25; }
                Require(NativeSpeedContract.HasSupportedEntryJump(body, 10));
                body[9] = 0xcc; Require(!NativeSpeedContract.HasSupportedEntryJump(body, 10));
            });
        check("registered-hook-unknown-instruction-is-rejected", () =>
            Require(!NativeSpeedContract.HasSupportedEntryJump(Enumerable.Repeat((byte)0x90,10).ToArray(),10)));
        check("registered-hook-missing-or-wrong-host-registry-fails-closed", () =>
        {
            Require(!RegisteredHookSnapshot.TryReadPrefix(typeof(string).Assembly, (nint)123, out _));
            Require(!RegisteredHookSnapshot.TryReadPrefix(typeof(IGameInteropProvider).Assembly, 0, out _));
            Require(!RegisteredHookSnapshot.TryReadPrefix(typeof(IGameInteropProvider).Assembly, (nint)123, out _));
        });

        string? executable = Environment.GetEnvironmentVariable("SPEED_START_AUDIT_EXE");
        string? auditPath = Environment.GetEnvironmentVariable("SPEED_START_AUDIT_JSON");
        if (executable == null || auditPath == null) return;
        using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(auditPath));
        int updaterRva = Convert.ToInt32(json.RootElement.GetProperty("updaterRva").GetString(),16);
        int getterRva = Convert.ToInt32(json.RootElement.GetProperty("getterRva").GetString(),16);
        using var file = File.OpenRead(executable);
        using var pe = new PEReader(file);
        byte[] original = pe.GetSectionData(updaterRva).GetContent(0,NativeSpeedContract.UpdateLength).ToArray();

        check("registered-hook-production-initialization-pristine", () =>
        {
            using var f = new Fixture(original, getterRva-updaterRva);
            using var backend = f.Create();
            Require(backend.TryInitialize(out _) && backend.Initialized && f.Created == 2);
        });
        check("registered-hook-production-unload-reload-preserves-host-entry", () =>
        {
            using var f = new Fixture(original, getterRva-updaterRva);
            using (var first = f.Create()) Require(first.TryInitialize(out _));
            byte[] hooked = f.ReadBody();
            Require(!NativeSpeedContract.Matches(hooked,f.Updater,f.Target));
            using var second = f.Create();
            Require(second.TryInitialize(out _) && second.Initialized && f.Created == 4);
            Require(f.ReadBody().SequenceEqual(hooked));
        });
        foreach (string failure in new[] { "missing", "untrimmed", "wrong-address", "bad-prefix", "short-prefix", "long-prefix", "unknown-jump", "tail-drift", "first-call", "second-call", "wrong-getter" })
            check("registered-hook-production-rejects-" + failure, () =>
            {
                using var f = new Fixture(original, getterRva-updaterRva);
                f.RegisterAndPatch();
                byte[] prefix = original[..10];
                if (failure == "missing") HookManager.Unhookers.TryRemove(f.Updater,out _);
                if (failure == "untrimmed") HookManager.Unhookers[f.Updater] = new(f.Updater,prefix,false);
                if (failure == "wrong-address") HookManager.Unhookers[f.Updater] = new(f.Updater+1,prefix);
                if (failure == "bad-prefix") { prefix[0] ^= 1; HookManager.Unhookers[f.Updater] = new(f.Updater,prefix); }
                if (failure == "short-prefix") HookManager.Unhookers[f.Updater] = new(f.Updater,original[..4]);
                if (failure == "long-prefix") HookManager.Unhookers[f.Updater] = new(f.Updater,original[..51]);
                if (failure == "unknown-jump") Marshal.WriteByte(f.Updater,0,0xcc);
                if (failure == "tail-drift") Marshal.WriteByte(f.Updater,100,(byte)(original[100]^1));
                if (failure == "first-call") Marshal.WriteByte(f.Updater,0x85,(byte)(original[0x85]^1));
                if (failure == "second-call") Marshal.WriteByte(f.Updater,0xfd,(byte)(original[0xfd]^1));
                if (failure == "wrong-getter") f.WrongGetter = true;
                using var backend = f.Create();
                Require(!backend.TryInitialize(out _) && !backend.Initialized && backend.Released && f.Created == 0);
            });
        check("registered-hook-record-is-cloned-and-never-mutated", () =>
        {
            using var f = new Fixture(original,getterRva-updaterRva); f.RegisterAndPatch();
            Require(RegisteredHookSnapshot.TryReadPrefix(typeof(IGameInteropProvider).Assembly,f.Updater,out var first));
            first[0] ^= 1;
            Require(RegisteredHookSnapshot.TryReadPrefix(typeof(IGameInteropProvider).Assembly,f.Updater,out var second));
            Require(second.SequenceEqual(original[..10]));
        });
        check("registered-hook-full-body-copy-preserves-live-bytes", () =>
        {
            using var f = new Fixture(original,getterRva-updaterRva); f.RegisterAndPatch();
            byte[] live = f.ReadBody(), before = (byte[])live.Clone();
            Require(NativeSpeedContract.MatchesRegisteredHook(live,f.Updater,f.Target,original[..10]));
            Require(live.SequenceEqual(before) && f.ReadBody().SequenceEqual(before));
            Require(!NativeSpeedContract.MatchesRegisteredHook(live.AsSpan(0,30),f.Updater,f.Target,original[..10]));
        });
    }

    private sealed class Fixture : IDisposable, ISigScanner, IGameInteropProvider
    {
        private readonly nint storage;
        private readonly WalkController* walk;
        private readonly byte[] original;
        internal nint Updater { get; }
        internal nint Target { get; }
        internal int Created;
        internal bool WrongGetter;
        internal Fixture(byte[] body,int getterDelta)
        {
            original = body;
            storage = (nint)NativeMemory.AllocZeroed(4096);
            Updater = storage+1024; Target = Updater+getterDelta;
            Require(Target >= storage && Target < storage+4096);
            Marshal.Copy(body,0,Updater,body.Length);
            walk = (WalkController*)NativeMemory.AllocZeroed((nuint)sizeof(WalkController));
            *walk = new() { Parent = 123, BaseSpeed = 6, SpeedUpdateActive = 1, HasInput = 1 };
        }
        internal NativeSpeedBackend Create() => new(this,this,() => default);
        internal byte[] ReadBody()
        {
            byte[] bytes = new byte[original.Length];Marshal.Copy(Updater,bytes,0,bytes.Length);return bytes;
        }
        internal void RegisterAndPatch()
        {
            HookManager.Unhookers.TryAdd(Updater,new(Updater,original[..10]));
            byte[] jump = [0xff,0x24,0x25,0x00,0x10,0x00,0x40,0x90,0x90,0x90];
            Marshal.Copy(jump,0,Updater,jump.Length);
        }
        public IEnumerable<nint> ScanAllText(string signature) => [signature == NativeSpeedContract.UpdateSignature ? Updater : Target];
        public nint ScanText(string signature) => signature == NativeSpeedContract.UpdateSignature ? Updater : Target+(WrongGetter ? 1 : 0);
        public nint GetStaticAddressFromSig(string signature) => (nint)walk;
        public Dalamud.Hooking.Hook<T> HookFromAddress<T>(nint address,T detour) where T : Delegate
        {
            Created++; if (address == Updater) RegisterAndPatch();
            return new(detour);
        }
        public void Dispose()
        {
            HookManager.Unhookers.TryRemove(Updater,out _);NativeMemory.Free((void*)storage);NativeMemory.Free(walk);
        }
    }
}
