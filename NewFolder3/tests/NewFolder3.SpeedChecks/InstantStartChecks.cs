using MoveSpeedLab;
using System.Buffers.Binary;

internal static unsafe class InstantStartChecks
{
    private static void Require(bool value) { if (!value) throw new Exception("Instant-start regression"); }
    private static SpeedContext Normal => new(new(770, 2, 1), true, true, true, true, FloorKind.Normal, "ready");
    private static WalkController Walk => new() { Parent = 123, BaseSpeed = 6, CurrentSpeed = .05f, SpeedUpdateActive = 1, HasInput = 1 };

    internal static void Run(Action<string, Action> check)
    {
        foreach (float multiplier in new[] { .1f, .5f, 1, 1.2f, 2, 10 })
            check("instant-first-native-update-" + multiplier, () =>
            {
                var request = new SpeedRequest(true, true, false, true, multiplier);
                var decision = SpeedPolicy.Decide(request, Normal, true);
                var lease = SpeedLease.Create(decision, Normal, 1000);
                Require(decision.InstantStart && lease.InstantStart && lease.Allows(Normal, 1001));
                WalkController walk = Walk;
                var correction = new InstantStartCorrection();
                float target = SpeedPolicy.Adjust(6, multiplier, lease.Apply);
                Require(correction.Apply(&walk, 6, target) && walk.CurrentSpeed == 6 * multiplier);
                correction.Restore(&walk, 6);
                Require(walk.CurrentSpeed == Math.Min(target, 6));
            });

        check("instant-one-times-keeps-native-target-and-own-lease", () =>
        {
            var s = new SpeedSession(); s.Enable(); s.Evaluate(Normal, 1, true, 0);
            Require(!s.Evaluate(Normal, 1, true, 499).InstantStart);
            var d = s.Evaluate(Normal, 1, true, 500);
            var l = SpeedLease.Create(d, Normal, 500);
            Require(!d.Apply && d.InstantStart && !l.Apply && l.InstantStart);
            Require(l.Allows(Normal, 649) && !l.Allows(Normal, 650));
            s.Pause(true); Require(!s.Evaluate(Normal, 1, true, 1000).InstantStart);
            s.Pause(false); Require(!s.Evaluate(Normal, 1, true, 1001).InstantStart);
            Require(s.Evaluate(Normal, 1, true, 1501).InstantStart);
            s.Fault("injected"); Require(!s.Evaluate(Normal, 1, true, 2001).InstantStart);
        });
        foreach (var request in new[]
        {
            new SpeedRequest(false, true, false, true, 2), new(true, false, false, true, 2),
            new(true, true, true, true, 2), new(true, true, false, false, 2), new(true, true, false, true, float.NaN)
        })
            check("instant-request-gate-" + request, () => Require(!SpeedPolicy.Decide(request, Normal, true).InstantStart));
        foreach (var context in new[]
        {
            Normal with { Ready = false }, Normal with { CanWalk = false }, Normal with { InDuty = false },
            Normal with { InDeepDungeon = false }, Normal with { Identity = new(770, 2, 10), Kind = FloorKind.Boss },
            Normal with { Identity = new(129, 0, 0), Kind = FloorKind.Unknown }
        })
            check("instant-context-gate-" + context, () =>
            {
                var d = SpeedPolicy.Decide(new(true, true, false, true, 2), context, true);
                Require(!d.InstantStart && !SpeedLease.Create(d, context, 1000).InstantStart);
            });
        check("instant-final-reward-remains-eligible", () =>
        {
            var c = Normal with { Identity = new(785, 2, 100), Kind = FloorKind.Result };
            Require(SpeedPolicy.Decide(new(true, true, false, true, 1), c, true).InstantStart);
        });
        foreach (float native in new[] { 0f, -1, float.NaN, float.PositiveInfinity })
            check("instant-invalid-native-target-" + native, () =>
            {
                WalkController walk = Walk; Require(!new InstantStartCorrection().Apply(&walk, native, 12));
                Require(walk.CurrentSpeed == .05f);
            });
        foreach (float returned in new[] { 0f, -1, float.NaN, float.PositiveInfinity })
            check("instant-invalid-returned-target-" + returned, () =>
            {
                WalkController walk = Walk; Require(!new InstantStartCorrection().Apply(&walk, 6, returned));
                Require(walk.CurrentSpeed == .05f);
            });
        check("instant-native-zero-output-is-never-resurrected", () =>
        {
            WalkController walk = Walk; walk.CurrentSpeed = 0;
            Require(!new InstantStartCorrection().Apply(&walk, 6, 60) && walk.CurrentSpeed == 0);
        });
        check("instant-native-activation-lock-and-jump-gates", () =>
        {
            WalkController walk = Walk; var c = new InstantStartCorrection();
            walk.SpeedUpdateActive = 0; Require(!c.Apply(&walk, 6, 12));
            walk.SpeedUpdateActive = 1; walk.InputLocked = 1; Require(!c.Apply(&walk, 6, 12));
            walk.InputLocked = 0; walk.Jumping = 1; Require(!c.Apply(&walk, 6, 12));
            Require(walk.CurrentSpeed == .05f);
        });
        check("instant-release-requeries-current-native-slow-and-zero", () =>
        {
            WalkController walk = Walk; var c = new InstantStartCorrection();
            Require(c.Apply(&walk, 7.8f, 15.6f)); c.Restore(&walk, 2.4f); Require(walk.CurrentSpeed == 2.4f);
            Require(c.Apply(&walk, 6, 60)); c.Restore(&walk, 0); Require(walk.CurrentSpeed == 0);
        });
        check("instant-release-preserves-another-writer-and-replaced-parent", () =>
        {
            WalkController walk = Walk; var c = new InstantStartCorrection(); c.Apply(&walk, 6, 12);
            walk.CurrentSpeed = 3; c.Restore(&walk, 2); Require(walk.CurrentSpeed == 3);
            c.Apply(&walk, 6, 12); walk.Parent++; c.Restore(&walk, 2); Require(walk.CurrentSpeed == 12);
        });
        check("instant-only-derived-linear-speed-bytes-change", () =>
        {
            byte* memory = stackalloc byte[320]; new Span<byte>(memory, 320).Fill(0x5a);
            WalkController* walk = (WalkController*)memory; *walk = Walk;
            byte[] before = new ReadOnlySpan<byte>(memory, 320).ToArray();
            Require(new InstantStartCorrection().Apply(walk, 6, 12));
            for (int i = 0; i < 320; i++) if (i < 68 || i >= 72) Require(memory[i] == before[i]);
        });
        check("instant-native-call-contract-rejects-truncation-mutation-and-wrong-target", () =>
        {
            byte[] code = new byte[NativeSpeedContract.UpdateLength]; code[0x84] = 0xe8;
            BinaryPrimitives.WriteInt32LittleEndian(code.AsSpan(0x85, 4), -0x399);
            Require(NativeSpeedContract.CallsTarget(code, 0x84, 0x1000, 0xcf0));
            Require(!NativeSpeedContract.CallsTarget(code, 0x84, 0x1000, 0xcf1));
            Require(!NativeSpeedContract.CallsTarget(code, -1, 0x1000, 0xcf0));
            Require(!NativeSpeedContract.CallsTarget(code, code.Length - 4, 0x1000, 0xcf0));
            Require(!NativeSpeedContract.Matches(code, 0x1000, 0xcf0));
            Require(!NativeSpeedContract.Matches(code.AsSpan(0, 20), 0x1000, 0xcf0));
        });
        string? executable = Environment.GetEnvironmentVariable("SPEED_START_AUDIT_EXE");
        string? auditPath = Environment.GetEnvironmentVariable("SPEED_START_AUDIT_JSON");
        if (executable != null && auditPath != null)
            check("instant-production-contract-matches-disk-body-and-rejects-one-byte-drift", () =>
            {
                using var json = System.Text.Json.JsonDocument.Parse(File.ReadAllText(auditPath));
                int updater = Convert.ToInt32(json.RootElement.GetProperty("updaterRva").GetString(), 16);
                int getter = Convert.ToInt32(json.RootElement.GetProperty("getterRva").GetString(), 16);
                using var stream = File.OpenRead(executable);
                using var pe = new System.Reflection.PortableExecutable.PEReader(stream);
                byte[] code = pe.GetSectionData(updater).GetContent(0, NativeSpeedContract.UpdateLength).ToArray();
                Require(NativeSpeedContract.Matches(code, updater, getter));
                Require(!NativeSpeedContract.Matches(code, updater, getter + 1));
                code[100] ^= 1; Require(!NativeSpeedContract.Matches(code, updater, getter));
            });
    }
}
