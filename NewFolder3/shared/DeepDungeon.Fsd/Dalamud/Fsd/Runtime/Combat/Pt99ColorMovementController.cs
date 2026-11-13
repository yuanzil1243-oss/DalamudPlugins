using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using global::Dalamud.Plugin.Ipc;
using OmenTools.Interop.Game;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

/// <summary>Color-floor interiors intersected with live BMR safety, without a blanket spatial-mechanic hold.</summary>
internal sealed class Pt99ColorMovementController
{
    private ICallGateSubscriber<Vector3, Vector3, bool>? _pathSafe;
    private ICallGateSubscriber<bool>? _moduleActive;
    private MovementInputController? _movement;
    private readonly Pt99ColorRouteSearch _routes = new();
    private readonly Pt99ColorRecoveryBudget _budget = new();
    private Pt99Color _desired;
    private bool _ownsMovement, _prepullRoute;
    private Vector3? _destination;
    private Vector3 _lastPosition;
    private DateTime _lastTick, _moveStarted, _lastProgress, _retryAt, _nextDiagnostic;
    private DateTime _nextRouteCheck, _requestedAt;
    internal string Status { get; private set; } = "inactive";

    internal void Update(RunContext context, Pt99Color desired, bool mechanicBlocked, bool required,
        bool allowPrepullFloorRoute, DateTime now)
    {
        if (desired == Pt99Color.None) { Reset(context); return; }
        var player = Service.LocalPlayer;
        if (player == null || player.IsDead) { Reset(context); return; }
        if (_desired != desired)
        {
            Reset(context);
            _desired = desired; _requestedAt = now;
            _budget.Reset(required);
            context.RecordDiagnostic?.Invoke("pt99-color-request", new { desired = desired.ToString(), required, observedUtc = now });
        }
        if (required && !_budget.Required)
        {
            _budget.SetRequired(true); _retryAt = default; _lastTick = now;
            context.RecordDiagnostic?.Invoke("pt99-color-required", new { desired = desired.ToString(), observedUtc = now,
                reason = "mandatory-recovery-starts-with-a-fresh-budget" });
        }
        else _budget.SetRequired(required);
        double elapsed = _lastTick == default ? 0 : (now - _lastTick).TotalSeconds;
        _lastTick = now;
        _budget.Tick(elapsed, mechanicBlocked, _ownsMovement);
        if (mechanicBlocked)
        {
            Wait(context, "drain-aether-bind-or-chains", "等待吸收以太结算或移动限制解除，BMR保有移动", now);
            return;
        }
        if (_budget.Exhausted)
        {
            Release(context, "bounded-recovery-exhausted");
            if (required)
            {
                context.RecordDiagnostic?.Invoke("pt99-color-recovery-exhausted", new { desired = desired.ToString(),
                    _budget.ActiveSeconds, _budget.EligibleSeconds, _budget.Failures, observedUtc = now });
                context.StatusLine = "99层必要取色超过恢复预算，已停手并释放移动；请检查战斗记录。";
                context.StatusIsError = true;
                Status = "必要取色失败，已停止";
            }
            else
            {
                Wait(context, "optional-preparation-deferred", "提前取色暂未完成，继续有效输出并交还BMR", now);
                _budget.Reset(); _retryAt = now.AddSeconds(5);
            }
            return;
        }
        if (now < _retryAt)
        { Wait(context, "retry-cooldown", "取色重试冷却，BMR保有移动", now); return; }
        var position = player.Position;
        if (!Pt99Arena.Contains(new(position.X, position.Z)))
        { Wait(context, "outside-known-arena", "不在已核对的99层场地内，等待BMR", now); return; }
        try
        {
            if (_prepullRoute != allowPrepullFloorRoute)
            {
                Release(context, "prepull-safety-state-changed");
                _routes.Reset(); _nextRouteCheck = default;
                _prepullRoute = allowPrepullFloorRoute;
            }
            if (!allowPrepullFloorRoute)
            {
                _moduleActive ??= Service.PluginInterface.GetIpcSubscriber<bool>("BossMod.HasActiveModule");
                _pathSafe ??= Service.PluginInterface.GetIpcSubscriber<Vector3, Vector3, bool>("BossMod.Hints.IsDashSafe");
                if (!_moduleActive.HasFunction || !_moduleActive.InvokeFunc() || !_pathSafe.HasFunction)
                { Wait(context, "live-safety-unavailable", "BMR战斗模块或安全接口未就绪，不接管移动", now); return; }
            }
            // Only the untouched, non-casting pre-pull encounter can use known static floor geometry.
            // During combat every segment sample must pass the current BMR forbidden-zone predicate.
            bool Safe(Vector3 from, Vector3 to) => allowPrepullFloorRoute
                ? Pt99Arena.Contains(new(to.X, to.Z)) : _pathSafe!.InvokeFunc(from, to);
            bool Visible(Vector3 from, Vector3 to) => CombatPositionNavigation.Sight(from, to, out _) == CombatSight.Clear;

            if (!_ownsMovement && now < _nextRouteCheck) return;
            if (_destination is not { } destination)
            {
                _nextRouteCheck = now.AddMilliseconds(500);
                if (!_routes.TryFind(position, desired, Safe, Visible, out destination))
                { Wait(context, "no-safe-direct-color-interior", "等待安全区内可达的颜色位置，BMR保有移动", now); return; }
                _destination = destination;
            }
            if (_ownsMovement && now >= _nextRouteCheck)
            {
                _nextRouteCheck = now.AddMilliseconds(150);
                if (!Pt99WalkSafety.IsSafe(position, destination, Safe) || !Visible(position, destination))
                { Wait(context, "route-became-unsafe", "取色途中安全区改变，交还BMR", now); return; }
            }
            if (!_ownsMovement)
            {
                if (context.SetBossMovementOverride?.Invoke(true) != true)
                { Wait(context, "movement-handoff-unconfirmed", "BMR暂停未确认，不接管移动", now); return; }
                _ownsMovement = true;
                try
                {
                    context.Navigator.CancelAll();
                    _movement ??= new MovementInputController { IsAutoMove = false, Precision = 0.25f };
                }
                catch { Release(context, "movement-initialization-failed"); throw; }
                _moveStarted = _lastProgress = now;
                _lastPosition = position;
                _nextRouteCheck = now.AddMilliseconds(150);
                context.RecordDiagnostic?.Invoke("pt99-color-ownership", new { active = true, observedUtc = now, required,
                    prepullFloorRoute = allowPrepullFloorRoute, probes = _routes.LastProbeCount,
                    desired = desired.ToString(), destination = new { destination.X, destination.Y, destination.Z } });
            }
            if (Vector2.DistanceSquared(new(position.X, position.Z), new(_lastPosition.X, _lastPosition.Z)) > 0.16f)
            { _lastPosition = position; _lastProgress = now; }
            if (now - _moveStarted > TimeSpan.FromSeconds(10) || now - _lastProgress > TimeSpan.FromSeconds(3))
            {
                _budget.RecordFailure();
                Wait(context, "no-progress-or-buff-unconfirmed", $"取色第{_budget.Failures}次未确认，已释放移动", now);
                _retryAt = now.AddSeconds(2);
                return;
            }
            bool arrived = Vector2.DistanceSquared(new(position.X, position.Z), new(destination.X, destination.Z)) <= 0.25f * 0.25f;
            _movement!.DesiredPosition = destination;
            _movement.Enabled = !arrived;
            Status = arrived ? "已到颜色区域，等待角色实际Buff" : $"暂停BMR，主动前往{(desired == Pt99Color.Light ? "光" : "暗")}色区域";
            if (now >= _nextDiagnostic)
            {
                _nextDiagnostic = now.AddSeconds(2);
                context.RecordDiagnostic?.Invoke("pt99-color-movement", new { desired = desired.ToString(), required, arrived,
                    _budget.ActiveSeconds, _budget.EligibleSeconds, _budget.Failures, observedUtc = now,
                    position = new { position.X, position.Y, position.Z }, destination = new { destination.X, destination.Y, destination.Z } });
            }
        }
        catch (Exception error)
        {
            Release(context, "movement-error");
            _budget.RecordFailure(); _retryAt = now.AddSeconds(2);
            context.RecordDiagnostic?.Invoke("pt99-color-error", new { error = error.ToString(), _budget.Failures, required });
            Status = "取色接口异常，已释放移动";
        }
    }

    private void Wait(RunContext context, string reason, string status, DateTime now)
    {
        Release(context, reason);
        Status = status;
        if (now < _nextDiagnostic) return;
        _nextDiagnostic = now.AddSeconds(2);
        context.RecordDiagnostic?.Invoke("pt99-color-wait", new { reason, desired = _desired.ToString(),
            required = _budget.Required, requestSeconds = (now - _requestedAt).TotalSeconds,
            _budget.ActiveSeconds, _budget.EligibleSeconds, _budget.Failures, observedUtc = now });
    }

    private void Release(RunContext? context, string reason)
    {
        if (_movement?.Enabled == true) _movement.Enabled = false;
        _destination = null;
        if (!_ownsMovement) return;
        _ownsMovement = false;
        try { context?.SetBossMovementOverride?.Invoke(false); }
        finally { context?.RecordDiagnostic?.Invoke("pt99-color-ownership", new { active = false, reason, observedUtc = DateTime.UtcNow }); }
    }

    internal void Reset(RunContext? context)
    {
        Release(context, "color-confirmed-or-controller-reset");
        _movement?.Dispose(); _movement = null;
        _desired = Pt99Color.None; _prepullRoute = false;
        _lastTick = _retryAt = _nextRouteCheck = _nextDiagnostic = _requestedAt = default;
        _budget.Reset(); _routes.Reset();
        Status = "inactive";
    }
}
