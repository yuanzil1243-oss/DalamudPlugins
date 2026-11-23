using System.Globalization;
using System.Numerics;

namespace DeepDungeon.Fsd.Runtime;

public readonly record struct HohBossOrbitFrame(
    DeepDungeonStateSnapshot State, bool RunActive, bool InCombat, bool PlayerReady,
    bool BmrConfigured, ulong TargetId, Vector3 Player, Vector3 Center,
    float TargetRadius, float PlayerRadius);

public readonly record struct HohBossOrbitCommand(bool Stop, bool Status, float? Radius, bool Clockwise)
{
    public const string Usage = "/newfolder3 circle [半径] [cw|ccw]；停止：/newfolder3 circle off；状态：/newfolder3 circle status";

    public static HohBossOrbitCommand Parse(string[] args)
    {
        if (args.Length == 1 && args[0].Equals("off", StringComparison.OrdinalIgnoreCase))
            return new(true, false, null, true);
        if (args.Length == 1 && args[0].Equals("status", StringComparison.OrdinalIgnoreCase))
            return new(false, true, null, true);
        float? radius = null;
        bool clockwise = true, directionSet = false;
        foreach (string arg in args)
        {
            if (arg.Equals("cw", StringComparison.OrdinalIgnoreCase) || arg.Equals("ccw", StringComparison.OrdinalIgnoreCase))
            {
                if (directionSet) throw new ArgumentException(Usage);
                clockwise = arg.Equals("cw", StringComparison.OrdinalIgnoreCase);
                directionSet = true;
            }
            else
            {
                if (radius.HasValue || !float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out float r) ||
                    !float.IsFinite(r) || r is < 2 or > 20)
                    throw new ArgumentException("半径须为2–20米，且需大于Boss碰撞圈。" + Usage);
                radius = r;
            }
        }
        return new(false, false, radius, clockwise);
    }
}

/// <summary>Opt-in HoH-only experiment. All movement and geometry dependencies are injected.</summary>
public sealed class HohBossOrbitController : IDisposable
{
    private readonly Func<bool, bool> _ownership;
    private readonly Action<Vector3> _move;
    private readonly Action _stop;
    private readonly Func<Vector3, Vector3?> _ground;
    private readonly Func<Vector3, Vector3, bool> _safe;
    private readonly Action<string> _notice;
    private bool _ownsMovement, _clockwise;
    private ulong _target;
    private byte _floor;
    private Vector3 _center, _lastProgressPosition;
    private DateTime _nextTick, _lastProgress;
    public bool Enabled { get; private set; }
    public float Radius { get; private set; }
    public string Status { get; private set; } = "绕圈实验已关闭";

    public HohBossOrbitController(Func<bool, bool> ownership, Action<Vector3> move, Action stop,
        Func<Vector3, Vector3?> ground, Func<Vector3, Vector3, bool> safe, Action<string> notice)
    {
        _ownership = ownership; _move = move; _stop = stop;
        _ground = ground; _safe = safe; _notice = notice;
    }

    public bool Start(in HohBossOrbitFrame frame, float? radius, bool clockwise, DateTime now, out string message)
    {
        if (!Eligible(frame)) { message = "仅可在FSD运行中的天宫Boss战使用；请选中存活的敌方Boss，并启用BMR机制处理。"; return false; }
        float minimum = frame.TargetRadius + frame.PlayerRadius + .5f;
        float desired = radius ?? Math.Max(3, minimum + 1);
        if (!float.IsFinite(desired) || desired < Math.Max(2, minimum) || desired > 20)
        { message = $"当前Boss所需圆心半径至少{Math.Max(2, minimum):0.0}米，最大20米。"; return false; }
        float distance = Distance(frame.Player, frame.Center);
        if (Math.Abs(distance - desired) > 6 || distance < minimum - .5f)
        { message = "请先靠近合适的绕圈范围（距指定圆周不超过6米，且在Boss碰撞圈外）。"; return false; }
        Stop("重新设置绕圈", notify: false);
        Enabled = true; Radius = desired; _clockwise = clockwise;
        _target = frame.TargetId; _floor = frame.State.Floor; _center = frame.Center;
        _lastProgressPosition = frame.Player; _lastProgress = now; _nextTick = default;
        Status = $"绕圈实验已开启：自动跟随当前Boss，圆心半径{Radius:0.0}米，{(clockwise ? "顺时针" : "逆时针")}";
        message = Status;
        return true;
    }

    public void Update(in HohBossOrbitFrame frame, DateTime now)
    {
        if (!Enabled) return;
        if (!Eligible(frame) || frame.TargetId != _target || frame.State.Floor != _floor)
        { Stop("战斗、目标或运行状态改变，绕圈已结束"); return; }
        if (Vector3.DistanceSquared(_center, frame.Center) > 16)
        { Stop("Boss位置突变，绕圈已结束"); return; }
        _center = frame.Center;
        if (now < _nextTick) return;
        _nextTick = now.AddMilliseconds(200);
        try
        {
            float distance = Distance(frame.Player, frame.Center);
            float minimum = frame.TargetRadius + frame.PlayerRadius + .5f;
            if (Radius < minimum || distance < minimum - .5f || Math.Abs(distance - Radius) > 6)
            { Stop("绕圈范围失效，已交还移动控制"); return; }
            var delta = frame.Player - frame.Center;
            float angle = MathF.Atan2(delta.Z, delta.X);
            float nextRadius = Radius;
            if (Math.Abs(distance - Radius) > 1)
                nextRadius = distance + Math.Clamp(Radius - distance, -.8f, .8f);
            else
                angle += (_clockwise ? 1 : -1) * Math.Min(MathF.PI / 12, 1 / Radius);
            var destination = frame.Center + new Vector3(MathF.Cos(angle) * nextRadius, 0, MathF.Sin(angle) * nextRadius);
            if (!TrySafeSegment(frame.Player, destination, out var grounded))
            { Pause("绕圈暂让：地面或BMR安全校验未通过，原机制处理接管"); return; }
            if (!_ownsMovement)
            {
                if (!_ownership(true)) { Pause("绕圈暂让：等待BMR移动交接"); return; }
                _ownsMovement = true;
                _lastProgressPosition = frame.Player; _lastProgress = now;
            }
            if (Distance(frame.Player, _lastProgressPosition) >= .3f)
            { _lastProgressPosition = frame.Player; _lastProgress = now; }
            if (now - _lastProgress >= TimeSpan.FromSeconds(3))
            { Stop("绕圈3秒无移动进展，已结束实验并交还控制"); return; }
            _move(grounded);
            SetStatus($"绕圈中：{Radius:0.0}米，{(_clockwise ? "顺时针" : "逆时针")}");
        }
        catch (Exception error)
        { Stop("绕圈接口异常，已结束实验：" + error.Message); }
    }

    private bool TrySafeSegment(Vector3 from, Vector3 to, out Vector3 grounded)
    {
        grounded = default;
        int count = Math.Max(1, (int)MathF.Ceiling(Vector3.Distance(from, to) / .25f));
        if (count > 32) return false;
        for (int i = 0; i <= count; i++)
        {
            Vector3 seed = Vector3.Lerp(from, to, i / (float)count);
            var point = _ground(seed);
            if (point is not { } p || !Finite(p) || Distance(seed, p) > .2f || Math.Abs(seed.Y - p.Y) > .5f)
                return false;
            // Require a small walkable margin, including on the inside of a curved route.
            foreach (var offset in MarginOffsets)
            {
                var margin = _ground(p + offset);
                if (margin is not { } q || !Finite(q) || Vector3.DistanceSquared(q, p + offset) > .04f)
                    return false;
            }
            grounded = p;
        }
        return _safe(from, grounded);
    }

    private static readonly Vector3[] MarginOffsets = [new(.35f, 0, 0), new(-.35f, 0, 0), new(0, 0, .35f), new(0, 0, -.35f)];
    private static bool Eligible(in HohBossOrbitFrame f) => f.RunActive && f.InCombat && f.PlayerReady && f.BmrConfigured &&
        f.State is { IsValid: true, IsInDuty: true, IsInDeepDungeonTerritory: true, DungeonId: 2,
            IsTransitioning: false, FloorKind: DeepDungeonFloorKind.Boss, Floor: >= 10 and <= 90 } && f.State.Floor % 10 == 0 &&
        f.TargetId != 0 && Finite(f.Player) && Finite(f.Center) &&
        float.IsFinite(f.TargetRadius) && f.TargetRadius >= 0 && float.IsFinite(f.PlayerRadius) && f.PlayerRadius >= 0 &&
        Math.Abs(f.Player.Y - f.Center.Y) <= 1;
    private static bool Finite(Vector3 p) => float.IsFinite(p.X) && float.IsFinite(p.Y) && float.IsFinite(p.Z);
    private static float Distance(Vector3 a, Vector3 b) => Vector2.Distance(new(a.X, a.Z), new(b.X, b.Z));
    private void SetStatus(string status) { if (Status == status) return; Status = status; _notice(status); }
    private void Release()
    {
        if (!_ownsMovement) return;
        try { _stop(); }
        finally { _ownsMovement = false; _ownership(false); }
    }
    private void Pause(string reason) { Release(); SetStatus(reason); }
    public void Stop(string reason = "已手动停止绕圈", bool notify = true)
    {
        bool wasEnabled = Enabled;
        Enabled = false;
        try { Release(); }
        finally { Status = reason; if (notify && wasEnabled) _notice(reason); }
    }
    public void Dispose() => Stop("插件卸载，绕圈已结束", notify: false);
}
