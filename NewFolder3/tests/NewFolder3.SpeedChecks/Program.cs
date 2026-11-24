using MoveSpeedLab;

int passed = 0;
void Check(string name, Action test)
{
    test(); passed++; Console.WriteLine("PASS " + name);
}
static void Assert(bool result, string message = "Assertion failed")
{
    if (!result) throw new InvalidOperationException(message);
}

SpeedContext Normal(int floor = 1) => new(new(floor switch
{
    <= 10 => 770u, <= 20 => 771u, <= 30 => 772u, <= 40 => 782u, <= 50 => 773u,
    <= 60 => 783u, <= 70 => 774u, <= 80 => 784u, <= 90 => 775u, _ => 785u
}, 2, floor), true, true, true, true, FloorKind.Normal, "ready");
SpeedContext Outside() => new(new(129, 0, 0), true, false, false, true, FloorKind.Unknown, "outside");
SpeedRequest Request() => new(true, true, false, true, 1.2f);
SpeedDecision Decide(SpeedRequest? request = null, SpeedContext? context = null, bool stable = true) =>
    SpeedPolicy.Decide(request ?? Request(), context ?? Normal(), stable);

Check("default-disabled", () => Assert(!Decide(Request() with { Enabled = false }).Apply));
Check("task-stopped-revokes", () => Assert(!Decide(Request() with { RunActive = false }).Apply));
Check("task-paused-revokes", () => Assert(!Decide(Request() with { Paused = true }).Apply));
Check("external-modifiers-not-acknowledged", () => Assert(!Decide(Request() with { ExternalModifiersAcknowledged = false }).Apply));
Check("confirmed-normal-floor", () => Assert(Decide().Apply));
Check("boss-floor-revokes-before-combat", () => Assert(!Decide(context: Normal(10) with { Kind = FloorKind.Boss }).Apply));
Check("boss-killed-but-same-floor-stays-released", () =>
{
    for (int i = 0; i < 100; i++) Assert(!Decide(context: Normal(10) with { Kind = FloorKind.Boss }).Apply);
});
Check("result-floor-revokes", () => Assert(!Decide(context: Normal(100) with { Kind = FloorKind.Result }).Apply));
Check("unknown-floor-revokes", () => Assert(!Decide(context: Normal() with { Kind = FloorKind.Unknown }).Apply));
Check("invalid-observation-revokes", () => Assert(!Decide(context: Normal() with { Ready = false }).Apply));
Check("cutscene-or-transition-revokes", () => Assert(!Decide(context: Normal() with { Ready = false, CanWalk = false }).Apply));
Check("movement-forbidden-revokes", () => Assert(!Decide(context: Normal() with { CanWalk = false }).Apply));
Check("duty-exit-revokes-auto", () => Assert(!Decide(context: Outside()).Apply));
Check("dungeon-duty-flag-required", () => Assert(!Decide(context: Normal() with { InDuty = false }).Apply));
Check("context-not-stable-cannot-enable", () => Assert(!Decide(stable: false).Apply));
Check("one-point-zero-is-identity", () =>
{
    Assert(!Decide(Request() with { Multiplier = 1 }).Apply);
    Assert(SpeedPolicy.Adjust(7.8f, 1, true) == 7.8f);
});
Check("lower-boundary-inclusive", () => Assert(Decide(Request() with { Multiplier = .1f }).Apply));
Check("upper-boundary-inclusive", () => Assert(Decide(Request() with { Multiplier = 10f }).Apply));
Check("slowdown-authorized", () => Assert(Decide(Request() with { Multiplier = .5f }).Apply));
Check("old-ceiling-no-longer-clamps", () => Assert(Decide(Request() with { Multiplier = 2f }).Apply));
foreach (float invalid in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0, -1, .0999f, 10.0001f, 99 })
{
    float v = invalid;
    Check("invalid-multiplier-" + v, () => Assert(!Decide(Request() with { Multiplier = v }).Apply));
}
Check("native-sprint-value-not-overwritten-with-six", () => Assert(Math.Abs(SpeedPolicy.Adjust(7.8f, 1.2f, true) - 9.36f) < .0001f));
Check("native-slow-value-not-overwritten", () => Assert(Math.Abs(SpeedPolicy.Adjust(2.4f, 1.2f, true) - 2.88f) < .0001f));
Check("one-tenth-slowdown", () => Assert(Math.Abs(SpeedPolicy.Adjust(6, .1f, true) - .6f) < .0001f));
Check("half-speed-slowdown", () => Assert(SpeedPolicy.Adjust(6, .5f, true) == 3));
Check("tenfold-scaling", () => Assert(SpeedPolicy.Adjust(6, 10, true) == 60));
Check("below-range-no-adjustment", () => Assert(SpeedPolicy.Adjust(6, .0999f, true) == 6));
Check("above-range-no-adjustment", () => Assert(SpeedPolicy.Adjust(6, 10.0001f, true) == 6));
Check("native-zero-stays-zero", () => Assert(SpeedPolicy.Adjust(0, 1.5f, true) == 0));
Check("native-negative-preserved", () => Assert(SpeedPolicy.Adjust(-1, 1.5f, true) == -1));
Check("native-nan-preserved", () => Assert(float.IsNaN(SpeedPolicy.Adjust(float.NaN, 1.5f, true))));
Check("native-infinity-preserved", () => Assert(float.IsPositiveInfinity(SpeedPolicy.Adjust(float.PositiveInfinity, 1.5f, true))));
Check("overflow-does-not-create-infinity", () => Assert(SpeedPolicy.Adjust(float.MaxValue, 1.5f, true) == float.MaxValue));
Check("authorization-required-for-adjustment", () => Assert(SpeedPolicy.Adjust(6, 1.5f, false) == 6));
Check("invalid-factor-never-adjusts", () => Assert(SpeedPolicy.Adjust(6, float.NaN, true) == 6));
Check("non-deep-dungeon-duty-denied", () => Assert(!Decide(context: Outside() with { InDuty = true }).Apply));
Check("normal-label-with-boss-identity-denied", () => Assert(!Decide(context: Normal(10)).Apply));
Check("wrong-territory-floor-pair-denied", () => Assert(!Decide(context: Normal(11) with { Identity = new(770, 2, 11) }).Apply));
Check("wrong-dungeon-for-territory-denied", () => Assert(!Decide(context: Normal() with { Identity = new(770, 1, 1) }).Apply));
Check("unknown-territory-denied", () => Assert(!Decide(context: Normal() with { Identity = new(129, 2, 1) }).Apply));

Check("context-stability-initial-delay", () =>
{
    var s = new ContextStability(); Assert(!s.Observe(Normal(), 0));
    Assert(!s.Observe(Normal(), 499)); Assert(s.Observe(Normal(), 500));
});
Check("next-normal-floor-needs-new-delay", () =>
{
    var s = new ContextStability(); s.Observe(Normal(), 0); Assert(s.Observe(Normal(), 1000));
    Assert(!s.Observe(Normal(11), 1001)); Assert(!s.Observe(Normal(11), 1500)); Assert(s.Observe(Normal(11), 1501));
});
Check("transition-resets-stability", () =>
{
    var s = new ContextStability(); s.Observe(Normal(), 0); s.Observe(Normal(), 500);
    Assert(!s.Observe(Normal() with { Ready = false }, 600));
    Assert(!s.Observe(Normal(), 1000)); Assert(s.Observe(Normal(), 1500));
});
Check("pause-reset-needs-stability", () =>
{
    var s = new ContextStability(); s.Observe(Normal(), 0); s.Observe(Normal(), 500); s.Reset();
    Assert(!s.Observe(Normal(), 1000)); Assert(s.Observe(Normal(), 1500));
});

SpeedLease Lease() => new(true, 1.2f, Normal().Identity, 1150);
Check("lease-allows-matching-live-floor", () => Assert(Lease().Allows(Normal(), 1149)));
Check("lease-expires-at-boundary", () => Assert(!Lease().Allows(Normal(), 1150)));
Check("stalled-framework-cannot-extend-grant", () => Assert(!Lease().Allows(Normal(), 10000)));
Check("live-boss-change-cancels-before-next-frame", () => Assert(!Lease().Allows(Normal(10) with { Kind = FloorKind.Boss }, 1000)));
Check("live-unknown-state-cancels-before-next-frame", () => Assert(!Lease().Allows(Normal() with { Ready = false }, 1000)));
Check("live-duty-exit-cancels", () => Assert(!Lease().Allows(Outside(), 1000)));
Check("live-controller-input-forbidden-cancels", () => Assert(!Lease().Allows(Normal() with { CanWalk = false }, 1000)));
Check("released-lease-never-allows", () => Assert(!SpeedLease.Released.Allows(Normal(), 0)));
Check("invalid-lease-multiplier-cannot-enable", () => Assert(!(Lease() with { Multiplier = float.NaN }).Allows(Normal(), 1000)));
Check("lease-factory-refuses-outside", () => Assert(!SpeedLease.Create(new(true, 2, "test"), Outside(), 0).Apply));
Check("lease-factory-refuses-boss", () => Assert(!SpeedLease.Create(new(true, 2, "test"), Normal(10), 0).Apply));
Check("lease-factory-refuses-identity-factor", () => Assert(!SpeedLease.Create(new(true, 1, "test"), Normal(), 0).Apply));
Check("lease-factory-exact-duration", () => Assert(SpeedLease.Create(new(true, 2, "test"), Normal(), 1000).ExpiresAt == 1150));

Check("floor-classifier-boss-tens-and-special-99", () =>
{
    for (uint dungeon = 1; dungeon <= 4; dungeon++)
    {
        Assert(DeepDungeonFloors.Classify(dungeon, 10) == FloorKind.Boss);
        Assert(DeepDungeonFloors.Classify(dungeon, 11) == FloorKind.Normal);
    }
    Assert(DeepDungeonFloors.Classify(3, 99) == FloorKind.Boss);
    Assert(DeepDungeonFloors.Classify(4, 99) == FloorKind.Boss);
    Assert(DeepDungeonFloors.Classify(2, 99) == FloorKind.Normal);
});
Check("floor-classifier-result-floors", () =>
{
    Assert(DeepDungeonFloors.Classify(1, 200) == FloorKind.Result);
    Assert(DeepDungeonFloors.Classify(1, 100) == FloorKind.Boss);
    Assert(DeepDungeonFloors.Classify(2, 100) == FloorKind.Result);
});
Check("invalid-dungeon-floors-stay-unknown", () =>
{
    foreach (var (d, f) in new[] { (0u, 1), (5u, 1), (1u, 0), (1u, 201), (2u, 101), (4u, -1) })
        Assert(DeepDungeonFloors.Classify(d, f) == FloorKind.Unknown);
});
Check("territory-ranges-validated", () =>
{
    Assert(DeepDungeonFloors.TryRange(782, out var d, out var f, out var t) && d == 2 && f == 31 && t == 40);
    Assert(DeepDungeonFloors.TryRange(607, out d, out f, out t) && d == 1 && f == 191 && t == 200);
    Assert(DeepDungeonFloors.TryRange(1290, out d, out f, out t) && d == 4 && f == 91 && t == 100);
    Assert(!DeepDungeonFloors.TryRange(1333, out _, out _, out _));
    Assert(!DeepDungeonFloors.TryRange(129, out _, out _, out _));
});

// The production Plugin.Update calls this exact SpeedSession.Evaluate + SpeedLease.Create path.
SpeedContext Boss() => Normal(10) with { Kind = FloorKind.Boss };
SpeedContext Unknown() => Normal() with { Ready = false, Kind = FloorKind.Unknown, CanWalk = false };
SpeedSession Armed()
{
    var session = new SpeedSession(); Assert(session.Enable());
    Assert(!session.Evaluate(Normal(), 1.5f, true, 0).Apply);
    Assert(session.Evaluate(Normal(), 1.5f, true, 500).Apply);
    return session;
}
SpeedLease Step(SpeedSession session, SpeedContext context, long now, float factor = 1.5f) =>
    SpeedLease.Create(session.Evaluate(context, factor, true, now), context, now);
void AssertArmed(SpeedSession session) => Assert(session.Enabled && !session.FaultLatched && session.Error == null);

Check("production-session-default-off", () => Assert(!new SpeedSession().Evaluate(Normal(), 2, true, 9999).Apply));
Check("normal-boss-stationary-beyond-old-timeout-outside-next-group", () =>
{
    var session = Armed();
    for (long now = 600; now <= 12000; now += 100)
    {
        // No movement observation is supplied. Stationarity cannot create a session fault.
        Assert(!Step(session, Boss(), now).Apply); AssertArmed(session);
    }
    Assert(!Step(session, Outside(), 12500).Apply); AssertArmed(session);
    Assert(!Step(session, Outside(), 30000).Apply); AssertArmed(session);
    Assert(!Step(session, Normal(11), 31000).Apply);
    Assert(!Step(session, Normal(11), 31499).Apply);
    Assert(Step(session, Normal(11), 31500).Allows(Normal(11), 31501)); AssertArmed(session);
});
Check("boss-directly-to-next-group-without-outside", () =>
{
    var session = Armed(); Assert(!Step(session, Boss(), 600).Apply);
    Assert(!Step(session, Boss(), 600000).Apply); AssertArmed(session);
    Assert(!Step(session, Normal(11), 600001).Apply);
    Assert(Step(session, Normal(11), 600501).Apply); AssertArmed(session);
});
Check("unknown-transition-never-disarms", () =>
{
    var session = Armed(); Assert(!Step(session, Unknown(), 600).Apply);
    Assert(!Step(session, Unknown(), 60000).Apply); AssertArmed(session);
    Assert(!Step(session, Normal(11), 61000).Apply);
    Assert(Step(session, Normal(11), 61500).Apply);
});
Check("result-outside-reenter-normal", () =>
{
    var session = Armed(); Assert(!Step(session, Normal(100) with { Kind = FloorKind.Result }, 600).Apply);
    Assert(!Step(session, Outside(), 6000).Apply); AssertArmed(session);
    Assert(!Step(session, Normal(), 7000).Apply); Assert(Step(session, Normal(), 7500).Apply);
});
Check("arm-outside-waits-for-next-normal", () =>
{
    var session = new SpeedSession(); Assert(session.Enable());
    Assert(!Step(session, Outside(), 0).Apply); Assert(!Step(session, Outside(), 60000).Apply); AssertArmed(session);
    Assert(!Step(session, Normal(), 60001).Apply); Assert(Step(session, Normal(), 60501).Apply);
});
Check("arm-in-boss-waits-without-timeout", () =>
{
    var session = new SpeedSession(); Assert(session.Enable());
    Assert(!Step(session, Boss(), 0).Apply); Assert(!Step(session, Boss(), 60000).Apply); AssertArmed(session);
    Assert(!Step(session, Normal(11), 60001).Apply); Assert(Step(session, Normal(11), 60501).Apply);
});
Check("master-off-does-not-resume-next-group", () =>
{
    var session = Armed(); Step(session, Boss(), 600); session.Disable();
    Step(session, Outside(), 9000); Step(session, Normal(11), 10000);
    Assert(!Step(session, Normal(11), 20000).Apply); Assert(!session.Enabled && !session.FaultLatched);
});
Check("master-off-on-normal-revokes-next-evaluation", () =>
{
    var session = Armed(); session.Disable(); Assert(!Step(session, Normal(), 501).Apply);
});
Check("pause-survives-boss-and-next-group-until-resume", () =>
{
    var session = Armed(); session.Pause(true);
    Assert(!Step(session, Normal(), 501).Apply); Step(session, Boss(), 600); Step(session, Outside(), 6000);
    Step(session, Normal(11), 7000); Assert(!Step(session, Normal(11), 8000).Apply);
    Assert(session.Enabled && session.Paused && !session.FaultLatched);
    session.Pause(false); Assert(!Step(session, Normal(11), 9000).Apply);
    Assert(!Step(session, Normal(11), 9499).Apply); Assert(Step(session, Normal(11), 9500).Apply);
});
Check("actual-hook-fault-blocks-cross-group-resume", () =>
{
    var session = Armed(); session.Fault("native callback failure");
    Step(session, Boss(), 600); Step(session, Outside(), 6000); Step(session, Normal(11), 7000);
    Assert(!Step(session, Normal(11), 60000).Apply);
    Assert(!session.Enabled && session.FaultLatched && session.Error == "native callback failure");
    Assert(!session.Enable());
});
Check("actual-init-fault-while-armed-outside-stays-disabled", () =>
{
    var session = new SpeedSession(); session.Enable(); Step(session, Outside(), 0);
    session.Fault("signature missing"); Step(session, Normal(), 1000);
    Assert(!Step(session, Normal(), 1500).Apply && session.FaultLatched && !session.Enabled);
});
Check("clearing-real-fault-does-not-auto-enable", () =>
{
    var session = Armed(); session.Fault("test fault"); session.ClearFault();
    Assert(!Step(session, Normal(), 1000).Apply && !session.FaultLatched && !session.Enabled && session.Error == null);
    Assert(session.Enable()); Assert(!Step(session, Normal(), 1500).Apply);
    Assert(Step(session, Normal(), 2000).Apply);
});
Check("death-cast-jump-or-other-cannot-walk-is-temporary", () =>
{
    var session = Armed(); var blocked = Normal() with { CanWalk = false };
    Assert(!Step(session, blocked, 600).Apply); Assert(!Step(session, blocked, 60000).Apply); AssertArmed(session);
    Assert(!Step(session, Normal(), 61000).Apply); Assert(Step(session, Normal(), 61500).Apply);
});
Check("territory-event-resets-lease-stability-with-master-on", () =>
{
    var session = Armed(); session.ResetContext();
    Assert(!Step(session, Normal(), 1000).Apply); Assert(!Step(session, Normal(), 1499).Apply);
    Assert(Step(session, Normal(), 1500).Apply); AssertArmed(session);
});
Check("invalid-factor-does-not-disable-master-or-authorize", () =>
{
    var session = Armed(); Assert(!Step(session, Normal(), 600, float.NaN).Apply); AssertArmed(session);
    Assert(Step(session, Normal(), 700, 2).Apply);
});
Check("acknowledgment-required-by-production-session", () =>
{
    var session = Armed(); Assert(!session.Evaluate(Normal(), 2, false, 600).Apply);
});
Check("future-task-stopped-blocks-production-session", () =>
{
    var session = Armed(); Assert(!session.Evaluate(Normal(), 2, true, 600, runActive: false).Apply);
});
Check("new-group-rejects-previous-floor-lease", () =>
{
    var session = Armed(); var lease = Step(session, Normal(), 600);
    Assert(lease.Allows(Normal(), 601)); Assert(!lease.Allows(Normal(11), 601));
    Assert(!lease.Allows(Boss(), 601)); Assert(!lease.Allows(Outside(), 601));
});
Check("slowdown-resumes-in-next-group-too", () =>
{
    var session = Armed(); Step(session, Boss(), 600, .1f); Step(session, Normal(11), 700, .1f);
    var lease = Step(session, Normal(11), 1200, .1f);
    Assert(lease.Apply && lease.Multiplier == .1f && Math.Abs(SpeedPolicy.Adjust(6, lease.Multiplier, true) - .6f) < .0001f);
});
Check("one-times-never-requires-confirmation-or-disarms", () =>
{
    var session = Armed(); Assert(!Step(session, Normal(), 600, 1).Apply); AssertArmed(session);
    Step(session, Boss(), 700, 1); Step(session, Normal(11), 800, 1);
    Assert(!Step(session, Normal(11), 1300, 1).Apply); Assert(Step(session, Normal(11), 1400, 2).Apply);
});

unsafe
{
    Check("separate-parent-regression", () =>
    {
        WalkController walk = new() { Parent = (nint)0x12345000, CurrentSpeed = 6, BaseSpeed = 6 };
        Assert(walk.Parent + 16 != (nint)(&walk));
        Assert(WalkControllerValidation.IsValid(&walk));
    });
    Check("null-walk-rejected", () => Assert(!WalkControllerValidation.IsValid(null)));
    Check("missing-parent-rejected", () =>
    {
        WalkController walk = new() { CurrentSpeed = 6, BaseSpeed = 6 };
        Assert(!WalkControllerValidation.IsValid(&walk));
    });
    Check("negative-current-speed-rejected", () =>
    {
        WalkController walk = new() { Parent = 1, CurrentSpeed = -1, BaseSpeed = 6 };
        Assert(!WalkControllerValidation.IsValid(&walk));
    });
    Check("stationary-walk-valid", () =>
    {
        WalkController walk = new() { Parent = 1, CurrentSpeed = 0, BaseSpeed = 6 };
        Assert(WalkControllerValidation.IsValid(&walk));
    });
    Check("nonfinite-controller-values-rejected", () =>
    {
        WalkController walk = new() { Parent = 1, CurrentSpeed = float.NaN, BaseSpeed = 6 };
        Assert(!WalkControllerValidation.IsValid(&walk));
        walk.CurrentSpeed = 6; walk.BaseSpeed = float.PositiveInfinity;
        Assert(!WalkControllerValidation.IsValid(&walk));
    });
    Check("nonpositive-base-speed-rejected", () =>
    {
        WalkController walk = new() { Parent = 1, CurrentSpeed = 0, BaseSpeed = 0 };
        Assert(!WalkControllerValidation.IsValid(&walk));
        walk.BaseSpeed = -6; Assert(!WalkControllerValidation.IsValid(&walk));
    });
}

Console.WriteLine($"MoveSpeedLab offline checks: {passed} passed. Native/game execution NOT tested.");
