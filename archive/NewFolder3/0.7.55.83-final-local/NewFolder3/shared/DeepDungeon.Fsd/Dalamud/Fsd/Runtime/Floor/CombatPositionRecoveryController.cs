using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using global::Dalamud.Game.ClientState.Objects.SubKinds;
using global::Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Floor;

/// <summary>One bounded position recovery. Floor accounting survives objective and target changes.</summary>
internal sealed class CombatPositionRecoveryController : IDisposable
{
    private enum Phase { Idle, Searching, Querying, Moving, Verifying }
    private readonly RunContext _ctx;
    private readonly NavigationHelper _navigation;
    private readonly EnemyChaseHelper _chase;
    private readonly NormalFloorGraphSnapshot? _graph;
    private readonly CombatRecoveryBudget _budget;
    private readonly RunFloorTelemetryTrace? _telemetry;
    private readonly Action<string> _status;
    private readonly Action<string, object> _record;
    private readonly ICombatPositionNavigation _geometry;
    private readonly Func<DateTime> _clock;
    private DateTime? _castHeldAt;
    private DateTime? _shieldHeldAt;
    private bool _navigationStartConfirmed;
    private readonly EnemyChaseAttackWindow _attackWindow = new();
    private readonly CombatRepositionState _reposition = new();
    private readonly HohCombatRecoveryState _hohRecovery = new();
    private readonly HohRecoveryCastState _hohCasts = new();
    private readonly HohTargetApproachState _targetApproach = new();
    private bool _castProtected;
    private readonly Dictionary<uint, bool> _offensiveActions = new();
    private readonly List<Vector3> _seeds = new(264), _candidates = new(32), _rejected = new(3);
    private CancellationTokenSource? _cancel;
    private Task<List<Vector3>>? _pending;
    private Phase _phase;
    private ulong _targetId;
    private uint _observedHp;
    private long _observedNativeHit;
    private bool _shieldWithoutObserver;
    private DateTime _lastProgressAt, _lastDamageAt, _deadline, _nextGeometryAt, _verifyAt, _lastMoveAt;
    private Vector3 _targetAtPlan, _startPosition, _destination, _lastMovePosition;
    private int _seedIndex, _candidateIndex, _pathQueries, _failedPositions, _playerRoom;
    private float _actionRange, _playerRadius, _targetRadius;
    private bool _ownsPath;
    private bool _attemptNavigationStarted;
    private bool _targetEngaged;
    private bool _staticClearTarget;
    private bool _escapeLeg;
    private bool _regionalResetUsed, _regionalSearch, _committedResetLeg, _regionalVerification;
    private int PathQueryLimit => HohLocalRecovery ? (_targetApproach.Active ? HohTargetApproachState.PathQueryLimit : _regionalSearch ? 16 : 12) : 4;
    private readonly HashSet<Vector3> _escapeCandidates = new();
    private readonly HashSet<Vector3> _wideCandidates = new();
    private readonly HashSet<Vector3> _firingFallbacks = new();
    private Vector3 _legStart;
    private bool HohLocalRecovery => _ctx.Duty.DungeonId == 2 && UsesBoundedReposition;
    private bool _resumeSearch;
    private DateTime? _yieldedAt;
    private Vector3 _lastPlayerPosition;
    private Vector3? _preferredReturn;
    private bool UsesBoundedReposition => MobCombatPolicy.HighFloorEnabled(_ctx.FarmingPlan?.Mode, _ctx.Duty.DungeonId, _ctx.Duty.Floor) ||
        HohCurrentDutyResumePolicy.UsesBoundedReposition(_ctx.FarmingPlan?.Mode, _ctx.Duty.DungeonId, _ctx.Duty.Floor);
    private int MaximumTargetRecoveryFailures => HohLocalRecovery ? 4 : CombatRecoveryBudget.MaximumFailuresPerTarget;
    private float MaximumRecoveryPathLength => HohLocalRecovery && _targetApproach.Active
        ? HohTargetApproachPolicy.MaximumPathLength : CombatPositionPolicy.MaximumPathLength;

    public CombatPositionRecoveryController(RunContext context, NavigationHelper navigation, EnemyChaseHelper chase,
        NormalFloorGraphSnapshot? graph, CombatRecoveryBudget budget, RunFloorTelemetryTrace? telemetry,
        Action<string> setStatus, Action<string, object> record,
        ICombatPositionNavigation? geometry = null, Func<DateTime>? clock = null)
    {
        _ctx = context; _navigation = navigation; _chase = chase; _graph = graph; _budget = budget;
        _telemetry = telemetry; _status = setStatus; _record = record;
        _geometry = geometry ?? new CombatPositionNavigation(); _clock = clock ?? (() => DateTime.UtcNow);
    }
    public DateTime AttackWindowStartedAt => _attackWindow.StartedAt;
    public bool IsAttackHolding(DateTime now) => _attackWindow.IsHolding(now);
    public void ObserveChaseLegArrival(DateTime now) => _attackWindow.Observe(false, true, now);
    public bool IsActive => _phase != Phase.Idle;
    internal bool HoldsProtectedCast => HohLocalRecovery && !_staticClearTarget && _castProtected;

    public unsafe bool Update(InstanceContentDeepDungeon* dd, EnemyChaseTarget target, IBattleChara? current,
        IPlayerCharacter player, bool aggro, bool attackOpportunity)
    {
        var now = _clock();
        if (_yieldedAt is { } yielded)
        {
            var pause = now - yielded;
            _deadline += pause;
            _lastProgressAt += pause;
            _yieldedAt = null;
            _cancel?.Dispose();
            _cancel = CancellationTokenSource.CreateLinkedTokenSource(_ctx.Token);
            _cancel.CancelAfter(_deadline > now ? _deadline - now : TimeSpan.FromMilliseconds(1));
        }
        if (current == null || current.IsDead || current.CurrentHp == 0)
        { ResetEngagedTargetProgress(); return false; }
        if (_targetId != target.GameObjectId)
        {
            ResetEngagedTargetProgress();
            _targetId = target.GameObjectId; _observedHp = current.CurrentHp; _lastProgressAt = now;
            if (_ctx.HohCombatTargets.TryGetHit(_targetId, out var previousHit)) _observedNativeHit = previousHit.Revision;
        }
        _targetEngaged = aggro;
        _staticClearTarget = HohLocalRecovery && target.Reason == EnemyChaseTargetReason.Petrified;
        _lastPlayerPosition = player.Position;
        if (aggro && UsesBoundedReposition && _reposition.ResumeAfterMechanics(now))
            _record("post-dodge-recovery-resumed", new { targetId = _targetId, graceSeconds = CombatRepositionState.DodgeGrace.TotalSeconds });
        _attackWindow.Observe(attackOpportunity, false, now);
        _actionRange = _ctx.CombatEngageRange;
        _playerRadius = player.HitboxRadius; _targetRadius = current.HitboxRadius;
        // Geometry can be clear while the game still rejects output (floor75).
        // Stop at range only after real displacement and retain the bounded verification attempt.
        if (HohLocalRecovery && _targetApproach.Active && _phase == Phase.Moving &&
            CombatPositionPolicy.HasRealHorizontalMove(_legStart, player.Position, .7f) &&
            CanHoldCurrentOutput(player.Position, current.Position, now))
        {
            StopPath(); _phase = Phase.Verifying; _verifyAt = now;
            _ctx.SetHohRecoveryMovement(this, false);
            _record("sight-recovery-position-arrived", new { targetId = _targetId,
                outputVerified = false, reason = "first-usable-position-after-movement" });
        }
        bool nativeOutput = HohLocalRecovery && _ctx.HohCombatTargets.ConsumeNewHit(_targetId, ref _observedNativeHit, now);
        _shieldWithoutObserver = HohLocalRecovery && !_ctx.DeathRecorder.NativeOutputAvailable &&
            current.StatusList.Any(s => s.StatusId == 151);
        if (current.CurrentHp < _observedHp || nativeOutput)
        {
            // A DoT can tick through a wall. HP alone cannot prove this position restored output.
            bool clearOutput = nativeOutput || CombatPositionPolicy.WithinRange(player.Position, current.Position,
                _actionRange, _playerRadius, _targetRadius, 0, 0) &&
                _geometry.TraceSight(player.Position, current.Position, out _) == CombatSight.Clear &&
                _geometry.TraceSight(player.Position - new Vector3(0, 7, 0), current.Position, out _) == CombatSight.Clear;
            if (clearOutput)
            {
                _lastDamageAt = _lastProgressAt = now;
                _budget.ObserveVerifiedOutput(_targetId, aggro);
                if (HohLocalRecovery) _ctx.HohApproachProgress.ObserveOutput();
                _reposition.ObserveOutput(player.Position, now);
                if (HohLocalRecovery && !_committedResetLeg)
                { _hohRecovery.ObserveOutput(); _regionalResetUsed = false; }
                if (HohLocalRecovery) _hohCasts.Reset();
            }
            if (_phase != Phase.Idle && clearOutput && !_committedResetLeg)
            {
                _record("sight-recovery-completed", new { targetId = _targetId,
                    reason = nativeOutput ? "player-native-hit-progress" : "target-hp-progress", hp = current.CurrentHp });
                EndAttempt();
            }
        }
        if (_shieldWithoutObserver && _phase == Phase.Verifying && CanOutputAt(player.Position, current.Position))
        {
            _record("sight-recovery-unverified", new { targetId = _targetId, reason = "shield-native-observer-unavailable" });
            EndAttempt(); _hohRecovery.SuspendEvidence(); _hohCasts.Reset();
        }
        _observedHp = current.CurrentHp;
        bool casting = Service.Condition[global::Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting] ||
            HohLocalRecovery && player.IsCasting;
        if (HohLocalRecovery)
        {
            int previousCasts = _hohCasts.UnconfirmedCasts;
            _castProtected = _hohCasts.Observe(now, casting, IsOffensiveCast(player), player.CastActionId, player.CurrentCastTime);
            if (_hohCasts.UnconfirmedCasts > previousCasts)
                _record("hoh-recovery-offensive-cast-without-output", new { targetId = _targetId,
                    unconfirmedCasts = _hohCasts.UnconfirmedCasts, casting, actionId = player.CastActionId,
                    castProtected = _castProtected, hp = current.CurrentHp });
            // A verified hit is stronger than a conservative geometry probe. Do not restart pursuit between GCDs.
            if (HasRecentVerifiedOutput(player.Position, current.Position, now))
            { if (_phase != Phase.Idle) EndAttempt(); _hohRecovery.SuspendEvidence(); return false; }
        }
        if (PauseForShieldOutput(player.Position,current.Position,now)) return true;
        if (PauseForProtectedCast(now)) return true;
        bool postDodge = aggro && !_staticClearTarget && UsesBoundedReposition && _reposition.InDodgeGrace(now);
        if (aggro && !_staticClearTarget && UsesBoundedReposition)
        {
            // A safe usable position gets a chance to fire before any further relocation.
            if (postDodge && !_committedResetLeg && CanOutputAt(player.Position, current.Position))
            { EndAttempt(); return false; }
            var retry = _reposition.Retry(player.Position, current.Position, EnemyLayout(), now);
            if (retry == CombatRepositionRetry.Waiting)
            {
                _navigation.Cancel();
                _status("躲避／站位恢复：等待已接战怪靠近或安全路线变化");
                return true;
            }
            if (retry == CombatRepositionRetry.TimedOut)
                if (HandleWaitTimeout(current)) return true;
            if (retry == CombatRepositionRetry.Changed) _nextGeometryAt = DateTime.MinValue;
        }
        if (_budget.Exhausted && (!aggro || HohLocalRecovery))
        { Stop("Combat position recovery exhausted the floor retry limit."); return true; }
        if (_phase != Phase.Idle)
        {
            if (now >= _deadline) { Fail("recovery-deadline", current); return true; }
            if (_resumeSearch)
                BeginSearch(dd, player.Position, current.Position);
            else if (now >= _nextGeometryAt && ShouldReplanForTargetMovement(current.Position))
            {
                _nextGeometryAt = now.AddMilliseconds(200);
                if (!_geometry.HasStandingMargin(_destination, current.Position, _actionRange, _playerRadius, _targetRadius))
                    BeginSearch(dd, player.Position, current.Position);
            }
            return Advance(dd, player, current, now);
        }
        if (casting && !HohLocalRecovery) return false;
        if (now < _nextGeometryAt) return false;
        _nextGeometryAt = now.AddMilliseconds(200);
        bool inRange = CombatPositionPolicy.WithinRange(player.Position, current.Position, _actionRange,
            _playerRadius, _targetRadius, 0, 0);
        bool needsApproach = HohLocalRecovery && HohTargetApproachPolicy.NeedsApproach(
            CanHoldCurrentOutput(player.Position, current.Position, now), aggro, _ctx.PassageOpenForCombat,
            _staticClearTarget || _ctx.IsNewCombatTargetAllowed(current));
        if (!inRange && _attackWindow.StartedAt == default &&
            (!aggro || now - _lastProgressAt < CombatPositionPolicy.UnknownNoProgressLimit) && !postDodge && !needsApproach)
        {
            if (HohLocalRecovery) _hohRecovery.SuspendEvidence();
            return false;
        }
        var nativeSight = _geometry.TraceSight(player.Position, current.Position, out var nativeHit);
        var sight = nativeSight;
        var hit = nativeHit;
        var offsetSight = CombatSight.Unknown;
        Vector3 offsetHit = default;
        if (sight != CombatSight.Blocked || HohLocalRecovery)
        {
            offsetSight = _geometry.TraceSight(player.Position - new Vector3(0, 7, 0), current.Position, out offsetHit);
            if (sight != CombatSight.Blocked && offsetSight == CombatSight.Blocked)
            { sight = CombatSight.Blocked; hit = offsetHit; }
        }
        if (!(needsApproach && !_castProtected) && !ShouldStartRecovery(now, inRange, sight, aggro, attackOpportunity, postDodge)) return false;
        if (_budget.IsTargetExhausted(_targetId, aggro, MaximumTargetRecoveryFailures))
        {
            if (aggro) { Stop("Engaged target remains unreachable after bounded combat recovery; manual intervention required."); return true; }
            return false;
        }
        if (!_geometry.Ready) { Stop("Ground pathfinding is unavailable. Enable the navigation plugin."); return true; }
        _deadline = now + CombatPositionPolicy.AttemptLimit;
        _attemptNavigationStarted = false;
        _cancel = CancellationTokenSource.CreateLinkedTokenSource(_ctx.Token);
        _cancel.CancelAfter(CombatPositionPolicy.AttemptLimit);
        _failedPositions = 0; _rejected.Clear(); _navigation.Cancel();
        if (HohLocalRecovery) _hohRecovery.BeginAttempt(player.Position);
        _telemetry?.ObserveStalledEngageRecoveryStarted();
        _record("sight-recovery-started", new
        {
            targetId = _targetId, aggro, reason = sight == CombatSight.Blocked ? "native-los-blocked" : "no-target-progress",
            sight = sight.ToString(), hitPoint = new { hit.X, hit.Y, hit.Z },
            nativeSight = nativeSight.ToString(), offsetSight = offsetSight.ToString(),
            nativeHit = new { nativeHit.X, nativeHit.Y, nativeHit.Z }, offsetHit = new { offsetHit.X, offsetHit.Y, offsetHit.Z },
            casting, unconfirmedCasts = HohLocalRecovery ? _hohCasts.UnconfirmedCasts : 0,
            player = new { player.Position.X, player.Position.Y, player.Position.Z },
            target = new { current.Position.X, current.Position.Y, current.Position.Z }, deadlineUtc = _deadline,
            offsets = new[] { 0, -7 }, failures = _budget.TotalFailures,
            hohFastRecovery = HohLocalRecovery, directedApproach = needsApproach,
            hohEscalation = HohLocalRecovery ? _hohRecovery.FailedPositions : 0
        });
        if (HohLocalRecovery) _targetApproach.Begin(player.Position, current.Position);
        BeginSearch(dd, player.Position, current.Position);
        return true;
    }

    private bool PauseForShieldOutput(Vector3 player, Vector3 target, DateTime now)
    {
        if (HohLocalRecovery && _ctx.HohShieldBreak.HoldsOutput(_targetId,now) && CanOutputAt(player,target))
        {
            // Transfer cast-only timing before starting a shield hold; count overlap once.
            if (_castHeldAt is {} castHeld)
            {
                var pause=now>castHeld?now-castHeld:TimeSpan.Zero;
                _deadline+=pause; _verifyAt+=pause; _lastMoveAt+=pause; _castHeldAt=null;
            }
            _shieldHeldAt??=now;
            StopPath(); _ctx.SetHohRecoveryMovement(this,false);
            if (_phase==Phase.Querying || _phase==Phase.Moving) _resumeSearch=true;
            if (_phase!=Phase.Idle) _cancel?.CancelAfter(Timeout.InfiniteTimeSpan);
            _status("石肤破盾兜底：保持安全输出位置，等待技能命中与护盾解除");
            return true;
        }
        SettleShieldHold(now);
        return false;
    }

    private void SettleShieldHold(DateTime now)
    {
        if (_shieldHeldAt is {} held)
        {
            var duration=now>held?now-held:TimeSpan.Zero;
            _deadline+=duration; _lastProgressAt+=duration; _verifyAt+=duration; _lastMoveAt+=duration;
            _shieldHeldAt=null;
            if (_phase!=Phase.Idle)
                _cancel?.CancelAfter(_deadline>now?_deadline-now:TimeSpan.FromMilliseconds(1));
        }
    }

    private bool PauseForProtectedCast(DateTime now)
    {
        if (HoldsProtectedCast && _phase != Phase.Idle)
        {
            _castHeldAt ??= now;
            if (_phase == Phase.Moving) { StopPath(); _resumeSearch = true; }
            _ctx.SetHohRecoveryMovement(this, false);
            _cancel?.CancelAfter(Timeout.InfiniteTimeSpan);
            _status("保护当前读条，完成后复核输出与接近路线");
            return true;
        }
        if (_castHeldAt is { } held)
        {
            var pause = now > held ? now - held : TimeSpan.Zero;
            _deadline += pause; _verifyAt += pause; _lastMoveAt += pause;
            _castHeldAt = null;
            if (_phase != Phase.Idle)
            {
                _cancel?.CancelAfter(_deadline > now ? _deadline - now : TimeSpan.FromMilliseconds(1));
                _ctx.SetHohRecoveryMovement(this, _phase != Phase.Verifying);
            }
        }
        return false;
    }

    private bool ShouldStartRecovery(DateTime now, bool inRange, CombatSight sight, bool aggro,
        bool attackOpportunity, bool postDodge)
    {
        if (HohLocalRecovery)
        {
            if (_shieldWithoutObserver && sight != CombatSight.Blocked)
            { _hohRecovery.SuspendEvidence(); return false; }
            bool evidence = _hohRecovery.ShouldStart(now, inRange, attackOpportunity, sight == CombatSight.Blocked);
            return !_castProtected && (postDodge || _staticClearTarget && _attackWindow.StartedAt != default &&
                now - _attackWindow.StartedAt >= HohCombatRecoveryState.NoOutputDelay ||
                evidence);
        }
        bool stalled = (aggro ? now - _lastProgressAt : _attackWindow.StartedAt == default
            ? TimeSpan.Zero : now - _attackWindow.StartedAt) >= CombatPositionPolicy.UnknownNoProgressLimit;
        return inRange && sight == CombatSight.Blocked && now - _lastDamageAt >= TimeSpan.FromSeconds(1) || stalled || postDodge;
    }

    // Waiting is not a second recovery attempt. Only a new search/path failure can charge HoH again.
    private bool HandleWaitTimeout(IBattleChara current)
    {
        if (!HohLocalRecovery) { Fail("combat-layout-unchanged-after-bounded-wait", current); return true; }
        _nextGeometryAt = DateTime.MinValue;
        _record("hoh-recovery-wait-ended", new { targetId = _targetId, failureCharged = false, reason = "new-search-required" });
        return false;
    }

    internal void DeferPetrifiedApproach(IBattleChara target) => Fail("petrified-approach-unreachable", target);

    internal void FailPetrifiedContact(IBattleChara target, IPlayerCharacter player, string reason)
    {
        ResetEngagedTargetProgress();
        _targetId = target.GameObjectId; _targetEngaged = _ctx.IsEngagedCombatTarget(target);
        _staticClearTarget = HohLocalRecovery; _lastPlayerPosition = player.Position;
        Fail(reason, target);
    }

    private unsafe void BeginSearch(InstanceContentDeepDungeon* dd, Vector3 player, Vector3 target)
    {
        if (HohLocalRecovery) _ctx.SetHohRecoveryMovement(this, true);
        StopPath();
        _resumeSearch = false;
        _pending = null; _phase = Phase.Searching; _startPosition = player; _targetAtPlan = target;
        _playerRoom = _ctx.ResolveHohCombatRoom(dd, _graph?.ReachableRooms);
        _seedIndex = _candidateIndex = _pathQueries = 0; _seeds.Clear(); _candidates.Clear(); _escapeCandidates.Clear(); _wideCandidates.Clear(); _firingFallbacks.Clear();
        _preferredReturn = null;
        if (HohLocalRecovery && _targetApproach.Active)
        {
            _seeds.AddRange(_targetApproach.Seeds(player, target));
            _nextGeometryAt = DateTime.MinValue;
            _record("hoh-target-approach-search", new { targetId = _targetId, stage = _targetApproach.Stage.ToString(),
                player = new { player.X, player.Y, player.Z }, target = new { target.X, target.Y, target.Z },
                seedCount = _seeds.Count, deadlineUtc = _deadline, outputVerified = false });
            return;
        }
        if (_targetEngaged && UsesBoundedReposition && _graph != null && _playerRoom >= 0 &&
            _reposition.ReturnPosition(player, _clock()) is { } previous)
        {
            int room = RoomGraph.GetRoomIndexForPosition(dd, previous, _graph.ReachableRooms, -1);
            if (room >= 0 && _graph.RoomDistances[_playerRoom, room] is >= 0 and <= 1 &&
                !_rejected.Any(p => Vector3.DistanceSquared(p, previous) < 1) &&
                AllowsRecoveryCandidate(player, previous, false) &&
                _geometry.HasStandingMargin(previous, target, _actionRange, _playerRadius, _targetRadius))
            {
                _preferredReturn = previous; _candidates.Add(previous);
                if (HohLocalRecovery && _geometry.HasGroundStandingMargin(previous, HohCombatRecoveryState.PlatformMargin))
                    _wideCandidates.Add(previous);
            }
        }
        var center = HohLocalRecovery ? _hohRecovery.Origin ?? player : player;
        AddSeeds(center, center.Y, target.Y);
        if (Vector2.DistanceSquared(new(player.X, player.Z), new(target.X, target.Z)) <= 225)
            AddSeeds(target, player.Y, target.Y);
        _nextGeometryAt = DateTime.MinValue;
    }
    private bool ShouldReplanForTargetMovement(Vector3 target) =>
        _phase == Phase.Moving && Vector3.DistanceSquared(_targetAtPlan, target) > 1 &&
        !(_ownsPath && HohLocalRecovery && _targetApproach.Active) &&
        // HoH59: a closing enemy can invalidate the firing point before the first movement frame.
        // Finish the already validated ground leg; arrival/output verification and deadlines still apply.
        !(_ownsPath && HohCurrentDutyResumePolicy.UsesBoundedReposition(
            _ctx.FarmingPlan?.Mode, _ctx.Duty.DungeonId, _ctx.Duty.Floor));

    private void AddSeeds(Vector3 center, float fromY, float toY)
    {
        for (int height = 0; height < 4; height++)
        {
            float y = fromY + (toY - fromY) * height / 3;
            if (HohLocalRecovery && height > 0 && Math.Abs(y - fromY) < .01f) continue;
            _seeds.Add(new(center.X, y, center.Z));
            foreach (int radius in HohLocalRecovery ? new[] { 3, 4, 6, 9, 12, 16, 20, 24 } : new[] { 3, 6, 9, 12 })
                foreach (var direction in CombatPositionPolicy.Directions)
                    _seeds.Add(new(center.X + direction.X * radius, y, center.Z + direction.Y * radius));
        }
    }
    private unsafe bool Advance(InstanceContentDeepDungeon* dd, IPlayerCharacter player, IBattleChara current, DateTime now)
    {
        if (!_geometry.Ready) { Stop("Navigation became unavailable during combat position recovery."); return true; }
        if (_phase == Phase.Searching)
        {
            _status(_targetApproach.Active ? $"目标受遮挡：{(_targetApproach.Stage == HohTargetApproachStage.NearSide ? "靠近原目标" : "绕到原目标另一侧")}" : "Searching for a clear combat position");
            if (now < _nextGeometryAt) return true;
            _nextGeometryAt = now.AddMilliseconds(50);
            for (int count = 0; count < (HohLocalRecovery ? 16 : 8) && _seedIndex < _seeds.Count; count++, _seedIndex++)
            {
                var point = _geometry.GroundPoint(_seeds[_seedIndex]);
                bool targetApproach = HohLocalRecovery && _targetApproach.Active;
                float maxSearchDistance = targetApproach ? HohTargetApproachPolicy.MaximumPathLength : HohLocalRecovery ? HohCombatRecoveryState.MaximumSearchDistance : CombatPositionPolicy.MaximumSearchDistance;
                if (point is not { } p || Vector2.DistanceSquared(new(p.X, p.Z), new(_startPosition.X, _startPosition.Z)) > maxSearchDistance * maxSearchDistance) continue;
                if (targetApproach)
                {
                    if (_graph == null || _playerRoom < 0 || !CombatPositionPolicy.HasRealHorizontalMove(_startPosition, p) ||
                        _rejected.Any(previous => Vector3.DistanceSquared(previous, p) < 1) ||
                        _candidates.Any(previous => Vector3.DistanceSquared(previous, p) < .25f)) continue;
                    int targetRoom = RoomGraph.GetRoomIndexForPosition(dd, p, _graph.ReachableRooms, -1);
                    if (targetRoom < 0 || !HohTargetApproachPolicy.AllowsRoom(_graph.RoomDistances[_playerRoom, targetRoom],
                            RestrictToHohCombatRoom, targetRoom, _ctx.MobCombat.HohCombatRoom) ||
                        !_geometry.HasGroundStandingMargin(p)) continue;
                    // A safe approach must not require the very firing line it is trying to establish.
                    // Endpoint, continuous-ground, mechanic and incidental-enemy checks still run on the full path.
                    _candidates.Add(p);
                    continue;
                }
                if (_regionalSearch && !CombatPositionPolicy.HasRealHorizontalMove(_startPosition, p, 8)) continue;
                bool expanded = AllowsRecoveryCandidate(_startPosition, p, false);
                bool firingFallback = AllowsFiringFallback(_startPosition, p);
                if (!expanded && !firingFallback) continue;
                if (_graph == null || _playerRoom < 0) continue;
                int room = RoomGraph.GetRoomIndexForPosition(dd, p, _graph.ReachableRooms, -1);
                if (room < 0 || _graph.RoomDistances[_playerRoom, room] is < 0 or > 1) continue;
                if (RestrictToHohCombatRoom && room != _ctx.MobCombat.HohCombatRoom) continue;
                if (_rejected.Any(previous => Vector3.DistanceSquared(previous, p) < 1) ||
                    _candidates.Any(previous => Vector3.DistanceSquared(previous, p) < 0.25f)) continue;
                if (_regionalSearch && _geometry.HasGroundStandingMargin(p))
                { _candidates.Add(p); _escapeCandidates.Add(p); }
                else if (_regionalSearch) continue;
                else if (_geometry.HasStandingMargin(p, current.Position, _actionRange, _playerRadius, _targetRadius))
                { _candidates.Add(p); if (!expanded) _firingFallbacks.Add(p); }
                else if (expanded && HohLocalRecovery && _hohRecovery.CanUseSafeExit &&
                    AllowsRecoveryCandidate(_startPosition, p, true) && _geometry.HasGroundStandingMargin(p))
                { _candidates.Add(p); _escapeCandidates.Add(p); }
                else continue;
                if (HohLocalRecovery && _geometry.HasGroundStandingMargin(p, HohCombatRecoveryState.PlatformMargin))
                    _wideCandidates.Add(p);
            }
            if (_seedIndex < _seeds.Count) return true;
            if (!_targetApproach.Active)
            { _candidates.Sort(CompareRecoveryCandidates); OrderRecoveryCandidates(); }
            if (_candidates.Count == 0) { RetryPathOrEscalate(dd, player, current, "no-compatible-local-position"); return true; }
            try { StartPathQuery(player.Position); }
            catch (Exception error) when (HohLocalRecovery) { Fail("path-query-dispatch-failed: " + error.Message, current); }
            return true;
        }
        if (_phase == Phase.Querying)
        {
            _status("Verifying the returned ground-path endpoint");
            if (_pending == null || !_pending.IsCompleted) return true;
            try
            {
                // An older SimpleMove query can otherwise overwrite this validated path.
                if (HohLocalRecovery && _geometry.SimpleMovePending) return true;
                var result = _pending.GetAwaiter().GetResult(); _pending = null;
                float length = 0;
                if (!CombatPositionPolicy.ValidatePathEndpoint(player.Position, _destination,
                        result, out length, out var reason, MaximumRecoveryPathLength))
                {
                    _record("sight-recovery-path-rejected", new { targetId = _targetId, reason, pathLength = length,
                        maximumPathLength = MaximumRecoveryPathLength, stage = _targetApproach.Stage.ToString() });
                    RetryPathOrEscalate(dd, player, current, "no-local-path-to-position");
                    return true;
                }
                var requested = _destination;
                bool directedPull = HohLocalRecovery && _targetApproach.Active && !_escapeLeg && !_regionalSearch;
                if (directedPull && !_staticClearTarget)
                {
                    var prefix = HohApproachPathPolicy.UntilFirstOutput(player.Position, result,
                        p => CanOutputAt(p, current.Position) && _geometry.HasGroundStandingMargin(p));
                    if (prefix.Count > 0 && Vector3.DistanceSquared(prefix[^1], result[^1]) > .01f)
                    {
                        result = prefix;
                        length = 0; var previous = player.Position;
                        foreach (var point in result) { length += Vector2.Distance(new(previous.X, previous.Z), new(point.X, point.Z)); previous = point; }
                        _record("hoh-approach-output-prefix", new { floor = _ctx.Duty.Floor, targetId = _targetId,
                            requested, endpoint = result[^1], pathLength = length, feetRequired = false });
                    }
                }
                if (!StaysInHohCombatRoom(dd, player.Position, result))
                {
                    _record("sight-recovery-path-rejected", new { targetId = _targetId, reason = "hoh91-combat-room-boundary", combatRoom = _ctx.MobCombat.HohCombatRoom });
                    RetryPathOrEscalate(dd, player, current, "no-safe-path-within-combat-room");
                    return true;
                }
                if (_ctx.WisdomConfirmed || UsesBoundedReposition)
                {
                    bool approachSelected = HohLocalRecovery && !_escapeLeg && !_targetEngaged && _ctx.IsNewCombatTargetAllowed(current);
                    Vector3 blocker = default; float initialDistance = 0, minimumDistance = 0; string safetyReason = "unengaged-enemy-on-local-path";
                    ulong blockerId = 0; int incidentalCount = 0;
                    bool safePath = directedPull
                        ? HohApproachPathPolicy.TryValidate(result, player.Position, ApproachObstacles(approachSelected),
                            HohApproachPathPolicy.IncidentalCapacity(HohHighFloorPolicy.ControlsCombat(_ctx.FarmingPlan?.Mode,
                                _ctx.Duty.DungeonId, _ctx.Duty.Floor), _ctx.ControlledCombatEngagedCount, _targetEngaged),
                            out blockerId, out blocker, out initialDistance, out minimumDistance, out incidentalCount,
                            out safetyReason, MaximumRecoveryPathLength)
                        : HohLocalRecovery
                        ? PassageCombatPolicy.TrySafeHohRecoveryPath(result, player.Position, UnengagedPositions(approachSelected),
                            out blocker, out initialDistance, out minimumDistance, out safetyReason, MaximumRecoveryPathLength)
                        : PassageCombatPolicy.SafeLocalPath(result, player.Position, UnengagedPositions());
                    if (!safePath)
                    {
                        if (blockerId == 0 && HohLocalRecovery) blockerId = Service.GameObjects?.OfType<IBattleChara>().FirstOrDefault(e =>
                            Vector3.DistanceSquared(e.Position, blocker) < .01f)?.GameObjectId ?? 0;
                        _record("sight-recovery-path-rejected", new { floor = _ctx.Duty.Floor, targetId = _targetId, reason = safetyReason,
                            blockerId, blocker = new { blocker.X, blocker.Y, blocker.Z }, initialDistance, minimumDistance,
                            candidate = new { _destination.X, _destination.Y, _destination.Z }, regional = _regionalSearch,
                            pathQueries = _pathQueries, queryLimit = PathQueryLimit, directedPull, incidentalCount });
                        RetryPathOrEscalate(dd, player, current, "no-safe-local-path-without-new-enemies");
                        return true;
                    }
                }
                if (HohLocalRecovery && (!_geometry.SafeAgainstMechanics(result, player.Position) ||
                    !CombatPositionPolicy.HasRealHorizontalMove(player.Position, result[^1]) ||
                    !CombatPositionPolicy.HasContinuousGround(result, player.Position, p => _geometry.GroundPoint(p, .15f, .45f),
                        (a, b) => _geometry.TraceSight(a, b, out _) == CombatSight.Clear, out _)))
                {
                    _record("sight-recovery-path-rejected", new { targetId = _targetId, reason = "no-horizontal-motion-or-discontinuous-ground" });
                    RetryPathOrEscalate(dd, player, current, "no-continuous-ground-relocation");
                    return true;
                }
                _destination = result[^1];
                if (_escapeLeg || HohLocalRecovery && _targetApproach.Active
                    ? !_geometry.HasGroundStandingMargin(_destination)
                    : !_geometry.HasStandingMargin(_destination, current.Position, _actionRange, _playerRadius, _targetRadius))
                {
                    if (HohLocalRecovery && _targetApproach.Active)
                        RetryPathOrEscalate(dd, player, current, "destination-no-longer-compatible");
                    else BeginSearch(dd, player.Position, current.Position);
                    return true;
                }
                _legStart = player.Position;
                // Dispatch failure is a real navigation failure; only pre-dispatch
                // geometry/eligibility rejection may defer an idle pull without charge.
                if (HohLocalRecovery)
                {
                    _attemptNavigationStarted = true;
                    _ownsPath = true; // Stop even if IPC throws after starting the path.
                }
                _geometry.Move(result);
                _ownsPath = true;
                _navigationStartConfirmed = false;
                _committedResetLeg = HohLocalRecovery && _regionalSearch;
                _phase = Phase.Moving; _lastMoveAt = now; _lastMovePosition = player.Position;
                _record("sight-recovery-path-started", new
                {
                    targetId = _targetId, status = _targetApproach.Active ? _targetApproach.Stage.ToString() : _committedResetLeg ? "RegionalReset" : _escapeLeg ? "SafeExitThenReacquireOutput" : "EndpointVerified", pathLength = length, margin = CombatPositionPolicy.StandingMargin,
                    hohMinimumOriginDistance = HohLocalRecovery ? _hohRecovery.MinimumOriginDistance : 0,
                    hohWidePlatform = HohLocalRecovery && _wideCandidates.Contains(requested),
                    hohVerifiedFiringFallback = HohLocalRecovery && _firingFallbacks.Contains(requested),
                    requested = new { requested.X, requested.Y, requested.Z },
                    destination = new { _destination.X, _destination.Y, _destination.Z }, offsets = new[] { 0, -7 },
                    deadlineUtc = _deadline, movementDispatched = true, nativeStartConfirmed = false
                });
            }
            catch (Exception error) { Fail((HohLocalRecovery && _ownsPath ? "navigation-dispatch-failed: " : "path-query-failed: ") + error.Message, current); }
            return true;
        }
        if (_phase == Phase.Moving)
        {
            if (HohLocalRecovery && !_navigationStartConfirmed)
            {
                _navigationStartConfirmed = _geometry.PathRunning ||
                    CombatPositionPolicy.HasRealHorizontalMove(_legStart, player.Position, .2f);
                if (_navigationStartConfirmed)
                    _record("hoh-approach-navigation-confirmed", new { targetId = _targetId, pathRunning = _geometry.PathRunning });
                else if (now - _lastMoveAt >= TimeSpan.FromSeconds(1))
                { Fail("navigation-dispatched-but-not-started", current); return true; }
            }
            _status(_targetApproach.Active ? $"目标受遮挡：{(_targetApproach.Stage == HohTargetApproachStage.NearSide ? "正在靠近原目标" : "正在绕到原目标另一侧")}" : "Moving to a clear combat position");
            if (Vector3.DistanceSquared(player.Position, _destination) <= CombatPositionPolicy.ArrivalRadius * CombatPositionPolicy.ArrivalRadius)
            {
                if (HohLocalRecovery && !CombatPositionPolicy.HasRealHorizontalMove(_legStart, player.Position, .7f))
                { RejectPosition(dd, player, current, "arrived-without-horizontal-displacement"); return true; }
                if (_committedResetLeg)
                {
                    _committedResetLeg = false; _regionalSearch = false; _regionalVerification = true;
                    StopPath(); _phase = Phase.Verifying; _verifyAt = now;
                    _ctx.SetHohRecoveryMovement(this, false); _hohRecovery.SuspendEvidence();
                    _record("hoh-regional-reset-arrived", new { targetId = _targetId,
                        position = new { player.Position.X, player.Position.Y, player.Position.Z }, outputVerified = false });
                    return true;
                }
                if (_escapeLeg && !CanOutputAt(player.Position, current.Position))
                {
                    _hohRecovery.CompleteSafeExit(player.Position); _rejected.Add(_destination); StopPath();
                    _record("sight-recovery-safe-exit-completed", new { targetId = _targetId, reason = "physical-exit-verified-reacquire-output", position = new { player.Position.X, player.Position.Y, player.Position.Z } });
                    BeginSearch(dd, player.Position, current.Position); return true;
                }
                StopPath(); _phase = Phase.Verifying; _verifyAt = now; _attackWindow.Observe(true, true, now);
                _ctx.SetHohRecoveryMovement(this, false);
                if (HohLocalRecovery) { _hohRecovery.SuspendEvidence(); _nextGeometryAt = now; }
                _record("sight-recovery-position-arrived", new { targetId = _targetId, stage = _targetApproach.Stage.ToString(),
                    outputVerified = false, position = new { player.Position.X, player.Position.Y, player.Position.Z } });
                return false;
            }
            if (Vector3.DistanceSquared(player.Position, _lastMovePosition) > 0.04f)
            { _lastMovePosition = player.Position; _lastMoveAt = now; }
            if (now - _lastMoveAt >= TimeSpan.FromSeconds(4)) RejectPosition(dd, player, current, "navigation-no-progress");
            return true;
        }
        if (_phase == Phase.Verifying)
        {
            if (HohLocalRecovery && !_regionalVerification && !_targetApproach.Active)
            {
                if (now >= _nextGeometryAt)
                {
                    _nextGeometryAt = now.AddMilliseconds(200);
                    bool blocked = _geometry.TraceSight(player.Position, current.Position, out _) == CombatSight.Blocked ||
                        _geometry.TraceSight(player.Position - new Vector3(0, 7, 0), current.Position, out _) == CombatSight.Blocked;
                    if (ShouldRejectBlockedArrival(now, blocked ? CombatSight.Blocked : CombatSight.Unknown, false))
                    { RejectPosition(dd, player, current, "arrival-still-los-blocked"); return true; }
                }
            }
            if (ArrivalVerificationExpired(now))
            { _regionalVerification = false; RejectPosition(dd, player, current, "attack-no-progress"); return true; }
            if (_regionalVerification) return true;
        }
        return false;
    }
    private bool AllowsRecoveryCandidate(Vector3 player, Vector3 point, bool safeExit) =>
        !HohLocalRecovery || _hohRecovery.AllowsCandidate(player, point, safeExit);

    private bool RestrictToHohCombatRoom => HohHighFloorPolicy.ControlsCombat(_ctx.FarmingPlan?.Mode, _ctx.Duty.DungeonId, _ctx.Duty.Floor) &&
        _ctx.ControlledCombatEngagedCount > 0;

    private unsafe bool StaysInHohCombatRoom(InstanceContentDeepDungeon* dd, Vector3 start, IReadOnlyList<Vector3> path)
    {
        if (!RestrictToHohCombatRoom) return true;
        if (dd == null || _graph == null) return false;
        return HohHighFloorPolicy.PathStaysInRoom(start, path, _ctx.MobCombat.HohCombatRoom,
            p => RoomGraph.GetRoomIndexForPosition(dd, p, _graph.ReachableRooms, -1));
    }

    private unsafe void RetryPathOrEscalate(InstanceContentDeepDungeon* dd, IPlayerCharacter player, IBattleChara current, string reason)
    {
        if (_candidateIndex < _candidates.Count && _pathQueries < PathQueryLimit)
        { StartPathQuery(player.Position); return; }
        if (TryAdvanceTargetApproach(reason))
        { BeginSearch(dd, player.Position, current.Position); return; }
        if (HohLocalRecovery && _targetApproach.Stage == HohTargetApproachStage.Finished &&
            !HohTargetApproachPolicy.AllowsUndirectedFallback(_targetEngaged, _staticClearTarget))
        { Fail("directed-approach-unreachable: " + reason, current); return; }
        if (TryBeginRegionalReset(reason))
        {
            BeginSearch(dd, player.Position, current.Position); return;
        }
        Fail(reason, current);
    }

    private bool TryBeginRegionalReset(string reason)
    {
        if (!HohLocalRecovery || _regionalResetUsed) return false;
        _regionalResetUsed = _regionalSearch = true;
        _record("hoh-local-search-exhausted", new { targetId = _targetId, reason,
            pathQueries = _pathQueries, candidateCount = _candidates.Count,
            failureCharged = false, nextStage = "regional-reset", minimumMove = 8, deadlineUtc = _deadline });
        return true;
    }

    private bool TryAdvanceTargetApproach(string reason)
    {
        if (!HohLocalRecovery || !_targetApproach.Active) return false;
        var previous = _targetApproach.Stage;
        _targetApproach.Advance();
        _record("hoh-target-approach-advanced", new { targetId = _targetId, reason, previousStage = previous.ToString(),
            nextStage = _targetApproach.Stage.ToString(), failureCharged = false, outputVerified = false, deadlineUtc = _deadline });
        return _targetApproach.Active;
    }

    private bool AllowsFiringFallback(Vector3 player, Vector3 point) =>
        HohLocalRecovery && _hohRecovery.AllowsFiringFallback(player, point);

    private bool ArrivalVerificationExpired(DateTime now) =>
        now - _verifyAt >= HohTargetApproachState.OutputWindow && (!HohLocalRecovery || !_castProtected);

    private bool ShouldRejectBlockedArrival(DateTime now, CombatSight sight, bool casting)
    {
        if (!HohLocalRecovery || _targetApproach.Active) return false;
        bool blocked = _hohRecovery.StillBlockedAtArrival(now, sight == CombatSight.Blocked, false);
        return !_castProtected && !casting && blocked;
    }

    private void OrderRecoveryCandidates()
    {
        if (!HohLocalRecovery || _escapeCandidates.Count == 0 && _firingFallbacks.Count == 0) return;
        var firing = _candidates.Where(p => !_escapeCandidates.Contains(p) && !_firingFallbacks.Contains(p)).ToArray();
        var fallback = _candidates.Where(p => _firingFallbacks.Contains(p)).ToArray();
        var exits = _candidates.Where(p => _escapeCandidates.Contains(p)).ToArray();
        // Keep one fully verified fallback inside the four-query budget, without removing safe exits.
        int exitSlots = fallback.Length > 0 ? 1 : 2;
        _candidates.Clear(); _candidates.AddRange(firing.Take(2)); _candidates.AddRange(fallback.Take(1));
        _candidates.AddRange(exits.Take(exitSlots)); _candidates.AddRange(firing.Skip(2));
        _candidates.AddRange(fallback.Skip(1)); _candidates.AddRange(exits.Skip(exitSlots));
    }

    private bool IsOffensiveCast(IPlayerCharacter player)
    {
        if (!player.IsCasting || player.CastActionId == 0 || player.CastTargetObjectId != _targetId) return false;
        if (_offensiveActions.TryGetValue(player.CastActionId, out bool offensive)) return offensive;
        var row = Service.DataManager.GetExcelSheet<global::Lumina.Excel.Sheets.Action>()?.GetRow(player.CastActionId);
        if (row == null) return false;
        return _offensiveActions[player.CastActionId] = row.Value.CanTargetHostile;
    }

    private int CompareRecoveryCandidates(Vector3 a, Vector3 b)
    {
        if (HohLocalRecovery && (_hohRecovery.FailedPositions > 0 || _escapeCandidates.Contains(a) || _escapeCandidates.Contains(b)))
            return _hohRecovery.CompareCandidates(_startPosition, a, _escapeCandidates.Contains(a), _wideCandidates.Contains(a),
                b, _escapeCandidates.Contains(b), _wideCandidates.Contains(b));
        int escapeOrder = _escapeCandidates.Contains(a).CompareTo(_escapeCandidates.Contains(b));
        if (escapeOrder != 0) return escapeOrder;
        if (_preferredReturn is { } preferred)
        {
            int order = (a == preferred ? 0 : 1).CompareTo(b == preferred ? 0 : 1);
            if (order != 0) return order;
        }
        return Vector3.DistanceSquared(_startPosition, a).CompareTo(Vector3.DistanceSquared(_startPosition, b));
    }
    private void StartPathQuery(Vector3 player)
    {
        _destination = _candidates[_candidateIndex++]; _pathQueries++;
        _escapeLeg = _escapeCandidates.Contains(_destination);
        _pending = _geometry.FindPath(player, _destination, _cancel!.Token);
        _pending.ContinueWith(task => { _ = task.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
        _phase = Phase.Querying;
    }
    private unsafe void RejectPosition(InstanceContentDeepDungeon* dd, IPlayerCharacter player, IBattleChara target, string reason)
    {
        if (HohLocalRecovery && _targetApproach.Active)
        {
            _rejected.Add(_destination);
            StopPath();
            if (!TryAdvanceTargetApproach(reason) &&
                !HohTargetApproachPolicy.AllowsUndirectedFallback(_targetEngaged, _staticClearTarget))
            { Fail("directed-approach-unreachable: " + reason, target); return; }
            BeginSearch(dd, player.Position, target.Position);
            return;
        }
        _rejected.Add(_destination); _failedPositions++;
        if (HohLocalRecovery) _hohRecovery.RejectPosition(_destination);
        _record("sight-recovery-position-rejected", new { targetId = _targetId, reason, failedPositions = _failedPositions,
            hohMinimumOriginDistance = HohLocalRecovery ? _hohRecovery.MinimumOriginDistance : 0 });
        if (_failedPositions >= 3) Fail(reason, target);
        else BeginSearch(dd, player.Position, target.Position);
    }
    private void Fail(string reason, IBattleChara target)
    {
        var now = _clock();
        if (DefersUnstartedApproach)
        {
            // All candidate routes were rejected before movement. This is a planning deferral,
            // not eight failed combat relocations. Let other targets / the existing room patrol run.
            var deferredTarget = _targetId;
            _chase.DeprioritizeCurrentTarget();
            _ctx.SuppressCombatTarget(deferredTarget, TimeSpan.FromSeconds(20));
            _ctx.SyncHohCombatScope();
            if (!_ctx.MobCombat.RotationPulling) _ctx.HohApproachProgress.Defer(_playerRoom, now);
            _ctx.ClearPreferredAggroTarget(); EndAttempt(); _targetId = 0; _attackWindow.Reset();
            _record("hoh-approach-plan-deferred", new { floor = _ctx.Duty.Floor, targetId = deferredTarget,
                reason, failureCharged = false, movementStarted = false, retryAfterSeconds = 20,
                failures = _budget.TotalFailures, consecutiveFailures = _budget.ConsecutiveFailures });
            _status("当前目标接近路线未通过校验，检查其他目标或继续房间巡查");
            return;
        }
        bool waitForChange = _targetEngaged && !_staticClearTarget && UsesBoundedReposition;
        if (waitForChange && _reposition.InDodgeGrace(now))
        {
            EndAttempt();
            _reposition.WaitForChange(_lastPlayerPosition, target.Position, EnemyLayout(), now);
            _record("post-dodge-recovery-waiting", new { targetId = _targetId, reason, failureCharged = false });
            _status("躲避后暂无安全复位路线；等待已接战怪靠近");
            return;
        }
        if (HohLocalRecovery && _failedPositions == 0) _hohRecovery.RejectPosition(_lastPlayerPosition);
        _budget.RecordFailure(_targetId, _targetEngaged);
        bool heldThreat = _targetEngaged;
        _record(heldThreat ? "held-target-recovery-attempt-failed" : "clearing-target-deprioritized", new { targetId = _targetId, reason, hp = target.CurrentHp, failures = _budget.TotalFailures,
            targetExhausted = _budget.IsTargetExhausted(_targetId, _targetEngaged, MaximumTargetRecoveryFailures),
            engaged = _targetEngaged, consecutiveFailures = _budget.ConsecutiveFailures });
        if (heldThreat)
        {
            // Keep the aggro obligation and marker. Never silently blacklist a live priority enemy.
            EndAttempt(); _lastProgressAt = _clock();
            if (waitForChange) _reposition.WaitForChange(_lastPlayerPosition, target.Position, EnemyLayout(), now);
            _record("held-target-recovery-failed", new { targetId = _targetId, reason, hp = target.CurrentHp });
            if (_budget.IsTargetExhausted(_targetId, true, MaximumTargetRecoveryFailures))
                Stop(HohLocalRecovery
                    ? "Engaged target remains out of sight after four HoH recovery attempts; manual continuation required."
                    : "Engaged target cannot be reached after two recovery attempts; manual continuation required.");
            return;
        }
        if (!_staticClearTarget) _chase.DeprioritizeCurrentTarget();
        _ctx.SuppressCombatTarget(_targetId, TimeSpan.FromSeconds(_staticClearTarget ? 5 : 20));
        _ctx.ClearPreferredAggroTarget(); EndAttempt(); _targetId = 0; _attackWindow.Reset();
        _status("Switching target after combat position recovery failed");
        if (_budget.Exhausted) Stop("Combat position recovery exhausted the floor retry limit.");
    }
    private void Stop(string reason)
    {
        EndAttempt(); _navigation.Cancel(); _ctx.StopExplorationRecoverably(reason); _status(reason);
        _record("sight-recovery-stopped", new { reason, targetId = _targetId, failures = _budget.TotalFailures,
            consecutiveFailures = _budget.ConsecutiveFailures, recoverable = _ctx.ExplorationRecovery.IsPaused });
    }
    private bool DefersUnstartedApproach => HohLocalRecovery && !_targetEngaged && !_staticClearTarget && !_attemptNavigationStarted;
    private void StopPath()
    {
        if (_ownsPath) { _geometry.Stop(); _ownsPath = false; }
    }
    private void EndAttempt()
    {
        _ctx.SetHohRecoveryMovement(this, false);
        StopPath(); _cancel?.Cancel(); _cancel?.Dispose(); _cancel = null; _pending = null; _phase = Phase.Idle;
        _resumeSearch = false; _yieldedAt = _castHeldAt = _shieldHeldAt = null;
        _regionalSearch = _committedResetLeg = _regionalVerification = false;
        _targetApproach.Reset();
    }
    public void ResetEngagedTargetProgress()
    {
        EndAttempt(); _targetId = 0; _observedHp = 0; _lastProgressAt = _lastDamageAt = default; _attackWindow.Reset();
        _reposition.Reset(); _preferredReturn = null; _hohRecovery.ObserveOutput();
        _staticClearTarget = _targetEngaged = false;
        _hohCasts.Reset(); _castProtected = false;
        _observedNativeHit = 0; _shieldWithoutObserver = false;
        _attemptNavigationStarted = false;
        _regionalResetUsed = false;
    }
    public void SuspendForTargetSelection()
    {
        if (!HohLocalRecovery) return;
        EndAttempt(); _attackWindow.Reset(); _hohRecovery.SuspendEvidence(); _hohCasts.Reset(); _castProtected = false;
    }
    public void Dispose() => ResetEngagedTargetProgress();
    public void YieldMovement()
    {
        SettleShieldHold(_clock());
        // Settle cast-only pause before starting a mechanics pause; never count the overlap twice.
        if (_castHeldAt is { } held)
        {
            var now = _clock();
            var pause = now > held ? now - held : TimeSpan.Zero;
            _deadline += pause; _verifyAt += pause; _lastMoveAt += pause; _castHeldAt = null;
        }
        _ctx.SetHohRecoveryMovement(this, false);
        _hohCasts.Reset(); _castProtected = false;
        if (HohLocalRecovery) _hohRecovery.SuspendEvidence();
        if (_targetId != 0 && UsesBoundedReposition) _reposition.YieldToMechanics();
        if (_phase == Phase.Idle) return;
        StopPath();
        _yieldedAt ??= _clock();
        _cancel?.Cancel();
        _pending = null;
        _resumeSearch = true;
    }
    internal static bool HasClearOutputPosition(Vector3 player, Vector3 target, float range, float playerRadius, float targetRadius) =>
        CombatPositionPolicy.WithinRange(player, target, range, playerRadius, targetRadius, 0, 0) &&
        HohTargetApproachPolicy.CanHoldOutput(true,
            CombatPositionNavigation.Sight(player, target, out _) == CombatSight.Clear,
            CombatPositionNavigation.Sight(player - new Vector3(0, 7, 0), target, out _) == CombatSight.Clear);
    private bool CanOutputAt(Vector3 player, Vector3 target) =>
        CombatPositionPolicy.WithinRange(player, target, _actionRange, _playerRadius, _targetRadius, 0, 0) &&
        HohTargetApproachPolicy.CanHoldOutput(true,
            _geometry.TraceSight(player, target, out _) == CombatSight.Clear,
            _geometry.TraceSight(player - new Vector3(0, 7, 0), target, out _) == CombatSight.Clear);
    private bool HasRecentVerifiedOutput(Vector3 player, Vector3 target, DateTime now) =>
        HohLocalRecovery && _lastDamageAt != default && now >= _lastDamageAt &&
        now - _lastDamageAt < HohCombatRecoveryState.NoOutputDelay &&
        CombatPositionPolicy.WithinRange(player, target, _actionRange, _playerRadius, _targetRadius, 0, 0);
    internal bool CanHoldCurrentOutput(Vector3 player, Vector3 target, DateTime now) =>
        HasRecentVerifiedOutput(player, target, now) || CanOutputAt(player, target);

    private IEnumerable<Vector3> UnengagedPositions(bool approachSelected = false) => HohLocalRecovery
        ? (Service.GameObjects?.OfType<IBattleChara>() ?? Enumerable.Empty<IBattleChara>()).Where(e => e.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
            (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)e.SubKind == global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant &&
            e.IsTargetable && !e.IsDead && e.CurrentHp > 0 && (!approachSelected || e.GameObjectId != _targetId) &&
            !_ctx.IsEngagedCombatTarget(e) && !RunContext.IsHohPetrified(e)).Select(e => e.Position)
        : _ctx.MobCombat.Unengaged.Select(e => e.Position);

    private HohApproachObstacle[] ApproachObstacles(bool approachSelected)
    {
        bool canPull = !_targetEngaged && !_ctx.PassageOpenForCombat && !_ctx.WisdomConfirmed && !_staticClearTarget && !_ctx.HohCentaurFightActive;
        return (Service.GameObjects?.OfType<IBattleChara>() ?? Enumerable.Empty<IBattleChara>())
            .Where(e => e.ObjectKind == global::Dalamud.Game.ClientState.Objects.Enums.ObjectKind.BattleNpc &&
                (global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind)e.SubKind == global::Dalamud.Game.ClientState.Objects.Enums.BattleNpcSubKind.Combatant &&
                e.IsTargetable && !e.IsDead && e.CurrentHp > 0 && (!approachSelected || e.GameObjectId != _targetId) &&
                !_ctx.IsEngagedCombatTarget(e) && !RunContext.IsHohPetrified(e))
            .Select(e => new HohApproachObstacle(e.GameObjectId, e.Position,
                canPull && !Hoh70FloorPolicy.IsCentaur(e.BaseId, e.NameId) && _ctx.IsNewCombatTargetAllowed(e))).ToArray();
    }

    private ulong EnemyLayout()
    {
        ulong signature = 0;
        if (!HohLocalRecovery)
        {
            foreach (var enemy in _ctx.MobCombat.Unengaged)
            {
                ulong ex = unchecked((uint)(int)MathF.Floor(enemy.Position.X / 2));
                ulong ez = unchecked((uint)(int)MathF.Floor(enemy.Position.Z / 2));
                signature ^= unchecked(enemy.GameObjectId * 1099511628211UL ^ ex * 16777619UL ^ ez);
            }
            return signature;
        }
        foreach (var position in UnengagedPositions())
        {
            // Coarse cells avoid treating idle animation/jitter as a new safe route.
            ulong x = unchecked((uint)(int)MathF.Floor(position.X / 2));
            ulong z = unchecked((uint)(int)MathF.Floor(position.Z / 2));
            signature ^= unchecked(x * 16777619UL ^ z * 1099511628211UL);
        }
        return signature;
    }
    public static unsafe bool TryResolveRoomDestination(InstanceContentDeepDungeon* dd, int room, out Vector3 destination)
    {
        destination = default;
        return room >= 0 && dd != null && MapPos.TryGetRoomCenter(dd, room, out destination);
    }
}
