using System.Text.Json;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Runtime;

namespace NewFolder3;

internal static class LocalFeatureTests
{
    public static int Run()
    {
        int count = 0;
        void Check(string name, Action test) { test(); count++; Console.WriteLine("  ok  local-" + name); }
        var selected = new SaveSlotSnapshot(1, false, 51, "51", "identity", true) { RowFingerprint = new('A', 64) };
        var other = selected with { Index = 0, StartFloor = 21, Progress = "21", Identity = "other" };
        Check("two-occupied-slots-explicit-slot-2", () =>
            Assert(ExternalSavePolicy.TryValidate(selected, [other, selected], 1, out _)));
        Check("slot-1-is-zero-based", () =>
            Assert(ExternalSavePolicy.TryValidate(other, [other, selected], 1, out _)));
        foreach (var changed in new[] { selected with { Empty = true }, selected with { Enterable = false },
            selected with { Identity = "replaced" }, selected with { StartFloor = 61 },
            selected with { Progress = "changed" }, selected with { RowFingerprint = new('B', 64) } })
            Check("changed-source-rejected-" + count, () =>
                Assert(!ExternalSavePolicy.TryValidate(selected, [other, changed], 1, out _)));
        Check("does-not-switch-to-other-save", () =>
            Assert(!ExternalSavePolicy.TryValidate(selected, [other], 1, out _)));
        Check("duplicate-slot-rejected", () =>
            Assert(!ExternalSavePolicy.TryValidate(selected, [selected, selected], 1, out _)));
        foreach (var invalid in new[] { selected with { Index = -1 }, selected with { Index = 2 },
            selected with { StartFloor = 200 }, selected with { StartFloor = 52 },
            selected with { RowFingerprint = null }, selected with { Identity = "" } })
            Check("invalid-selected-save-" + count, () =>
                Assert(!ExternalSavePolicy.TryValidate(invalid, [invalid], 1, out _)));

        foreach (uint dungeon in new uint[] { 1, 2, 4 })
        {
            Assert(FarmingPlan.TryCreate(FarmingMode.DeepProgression, SaveUse.Create, 1, 1, false,
                false, true, true, false, out var original, out _, dungeon));
            var plan = original! with { ExternalSave = true, ExternalSaveCharacter = 123, ExternalSaveJob = 31 };
            Check("external-never-delete-or-restart-" + dungeon, () =>
            {
                for (int floor = 1; floor <= plan.TerminalEntryFloor; floor += 10)
                    for (int flags = 0; flags < 16; flags++)
                    {
                        var decision = FarmingSessionPolicy.Decide(plan,
                            new(floor, (flags & 1) != 0, (flags & 2) != 0, (flags & 4) != 0, (flags & 8) != 0));
                        Assert(!decision.DeleteOwnedSave && !decision.RecoverFailure);
                        if ((flags & 10) != 0) Assert(decision.Error.Length > 0);
                    }
            });
            Check("upstream-created-failure-unchanged-" + dungeon, () =>
            {
                var decision = FarmingSessionPolicy.Decide(original!, new(1, false, true, false, false));
                Assert(decision.DeleteOwnedSave && decision.RecoverFailure);
            });
            Check("external-next-group-and-terminal-" + dungeon, () =>
            {
                var next = FarmingSessionPolicy.Decide(plan, new(1, true, false, false, false));
                Assert(next.NextFloor == 11 && !next.CountCycle && next.Error == "");
                var terminal = FarmingSessionPolicy.Decide(plan, new(plan.TerminalEntryFloor, true, false, false, false));
                Assert(terminal.CountCycle && !terminal.DeleteOwnedSave && terminal.Error == "");
            });
            Check("identity-and-checkpoint-roundtrip-" + dungeon, () =>
            {
                Assert(ExternalSavePolicy.MatchesCharacter(plan, 123, 31));
                Assert(!ExternalSavePolicy.MatchesCharacter(plan, 124, 31));
                Assert(!ExternalSavePolicy.MatchesCharacter(plan, 123, 38));
                Assert(!ExternalSavePolicy.MatchesCharacter(plan, 0, 31));
                Assert(JsonSerializer.Deserialize<FarmingPlan>(JsonSerializer.Serialize(plan)) == plan);
            });
        }
        Check("source-binding-pins-full-initial-row", () =>
        {
            var binding = new PreparedSaveBinding(false, selected.StartFloor, selected.Index, 200);
            binding.PinCheckpoint(selected);
            Assert(binding.Observe([other, selected], out var source, out _) && source == selected);
            Assert(!binding.Observe([other, selected with { RowFingerprint = new('B', 64) }], out _, out _));
            binding.ReleaseCheckpointPin();
            binding.ConfigureForEntry(false, 61, 1, 200);
            Assert(binding.Observe([other, selected with { StartFloor = 61, Progress = "61" }], out _, out _));
            Assert(!binding.Observe([other, selected with { StartFloor = 61, Progress = "61", Identity = "other" }], out _, out _));
        });
        Check("unknown-history-never-candidate", () =>
        {
            foreach (var a in Enum.GetValues<SaveHistoryFact>())
                foreach (var b in Enum.GetValues<SaveHistoryFact>())
                    foreach (var c in Enum.GetValues<SaveHistoryFact>())
                    {
                        var history = new SaveHistoryEvidence(a, b, c);
                        Assert(history.IsCandidate(1) == (a == SaveHistoryFact.Confirmed &&
                            b == SaveHistoryFact.Confirmed && c == SaveHistoryFact.Confirmed));
                        Assert(!history.IsCandidate(2));
                    }
        });
        Check("unrecognized-native-risk-prompt-never-confirmed", () =>
        {
            foreach (bool creating in new[] { true, false })
            {
                var facts = new EntryFacts(false, false, 0, 0, EntryUiKind.Unknown, true, false, false, true);
                Assert(EntryUiPolicy.Decide(facts, 1, 51, creating).Action == EntryAction.Wait);
            }
        });
        foreach (bool initialRunning in new[] { false, true })
            foreach (bool initialPull in new[] { false, true })
                Check($"ae-start-suppress-resume-restore-{initialRunning}-{initialPull}", () =>
                {
                    bool running = initialRunning, pull = initialPull;
                    var ae = new AeOutputLease(() => running, () => pull, v => running = v, v => pull = v);
                    Assert(ae.TrySet(true) && running && pull);
                    Assert(ae.TrySet(false) && !running && !pull);
                    Assert(ae.TrySet(true) && running && pull);
                    Assert(ae.Restore() && running == initialRunning && pull == initialPull && !ae.IsOwned);
                    Assert(ae.Restore());
                });
        Check("ae-manual-change-wins", () =>
        {
            bool running = true, pull = true;
            var ae = new AeOutputLease(() => running, () => pull, v => running = v, v => pull = v);
            Assert(ae.TrySet(false));
            running = true;
            Assert(!ae.TrySet(true) && running && pull && !ae.IsOwned);
        });
        Check("ae-read-failure-retains-restore-retry", () =>
        {
            bool running = false, pull = false, unavailable = false;
            var ae = new AeOutputLease(() => unavailable ? throw new IOException() : running,
                () => unavailable ? throw new IOException() : pull, v => running = v, v => pull = v);
            Assert(ae.TrySet(true));
            unavailable = true;
            Assert(!ae.Restore() && ae.IsOwned);
            unavailable = false;
            Assert(ae.Restore() && !running && !pull && !ae.IsOwned);
        });
        Check("ae-partial-write-failure-rolls-back", () =>
        {
            bool running = false, pull = false;
            var ae = new AeOutputLease(() => running, () => pull, v => running = v,
                v => { if (v) throw new IOException(); pull = v; });
            Assert(!ae.TrySet(true) && !running && !pull);
        });
        Check("ae-rejected-write-detected", () =>
        {
            bool running = false, pull = false;
            var ae = new AeOutputLease(() => running, () => pull, _ => { }, v => pull = v);
            Assert(!ae.TrySet(true) && !running && !pull);
        });
        Check("ae-provider-values-compatible", () =>
            Assert((int)FsdRotationProvider.Custom == 4 && (int)FsdRotationProvider.InsertNameHere3 == 5 &&
                (int)FsdRotationProvider.AEAssist == 6));
        return count;
    }

    private static void Assert(bool success)
    {
        if (!success) throw new InvalidOperationException("Local feature regression failed.");
    }
}
