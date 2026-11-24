// SPDX-License-Identifier: GPL-3.0-only
namespace MoveSpeedLab;

public enum FloorKind { Unknown, Normal, Boss, Result }

public readonly record struct FloorIdentity(uint Territory, uint Dungeon, int Floor);

/// <summary>Host observation only. Unknown/transitioning data can never authorize speed.</summary>
public readonly record struct SpeedContext(
    FloorIdentity Identity, bool Ready, bool InDuty, bool InDeepDungeon,
    bool CanWalk, FloorKind Kind, string Reason);

public readonly record struct SpeedRequest(
    bool Enabled, bool RunActive, bool Paused, bool ExternalModifiersAcknowledged,
    float Multiplier);

public readonly record struct SpeedDecision(bool Apply, float Multiplier, string Reason);

public static class SpeedPolicy
{
    // User-requested experimental range; navigation compatibility remains unverified.
    public const float MinimumMultiplier = 0.1f;
    public const float MaximumMultiplier = 10f;
    public const int ContextStableMilliseconds = 500;
    public const int LeaseMilliseconds = 150;

    public static bool ValidMultiplier(float value) => float.IsFinite(value) &&
        value >= MinimumMultiplier && value <= MaximumMultiplier;

    public static bool IsEligibleFloor(in SpeedContext context) => context.Ready && context.CanWalk &&
        context.InDeepDungeon && context.InDuty && context.Kind is FloorKind.Normal or FloorKind.Result &&
        DeepDungeonFloors.TryRange(context.Identity.Territory, out uint expected, out int first, out int last) &&
        context.Identity.Dungeon == expected && context.Identity.Floor >= first && context.Identity.Floor <= last &&
        DeepDungeonFloors.Classify(expected, context.Identity.Floor) == context.Kind;

    public static SpeedDecision Decide(in SpeedRequest request, in SpeedContext context,
        bool contextStable)
    {
        if (!request.Enabled) return new(false, 1, "已关闭：交还原生计算");
        if (!request.RunActive || request.Paused) return new(false, 1, "已暂停或任务未运行");
        if (!request.ExternalModifiersAcknowledged) return new(false, 1, "请先确认已关闭其他移速修正");
        if (!ValidMultiplier(request.Multiplier)) return new(false, 1, "倍率无效，修正已撤销");
        if (!context.Ready || !context.CanWalk) return new(false, 1, context.Reason);
        if (!IsEligibleFloor(context))
            return new(false, 1, "Boss／副本外／未知层：已撤销，等待普通层或最终奖励层");
        if (!contextStable) return new(false, 1, "等待区域／楼层状态稳定 500ms");
        return new(request.Multiplier != 1, request.Multiplier,
            request.Multiplier != 1 ? "已授权实验修正；实际效果请看行走观测" : "1.00 倍：直接返回原生计算值");
    }

    public static float Adjust(float original, float multiplier, bool authorized)
    {
        // Always preserve zero/negative/NaN/native invalid values and native movement gates.
        if (!authorized || !ValidMultiplier(multiplier) || multiplier == 1 ||
            !float.IsFinite(original) || original <= 0)
            return original;
        float result = original * multiplier;
        return float.IsFinite(result) ? result : original;
    }
}

/// <summary>
/// The production Update loop and regression checks use this same session transition policy.
/// Temporary context changes revoke the decision, never the user's master switch.
/// Only an explicit stop or a real interface/runtime fault disables the session.
/// </summary>
public sealed class SpeedSession
{
    private readonly ContextStability stability = new();
    public bool Enabled { get; private set; }
    public bool Paused { get; private set; }
    public bool FaultLatched { get; private set; }
    public string? Error { get; private set; }

    public bool Enable()
    {
        if (FaultLatched) return false;
        Enabled = true; Paused = false; stability.Reset();
        return true;
    }
    public void Disable()
    {
        Enabled = false; Paused = false; stability.Reset();
    }
    public void Pause(bool value)
    {
        Paused = Enabled && value; stability.Reset();
    }
    public void Fault(string message)
    {
        Disable(); FaultLatched = true; Error = message;
    }
    public void ClearFault()
    {
        Disable(); FaultLatched = false; Error = null;
    }
    public void ResetContext() => stability.Reset();

    public SpeedDecision Evaluate(in SpeedContext context, float multiplier,
        bool externalModifiersAcknowledged, long now, bool runActive = true)
    {
        bool stable = stability.Observe(context, now);
        if (FaultLatched) return new(false, 1, "接口故障：已关闭，需处理后手动重新启用");
        var request = new SpeedRequest(Enabled, runActive, Paused, externalModifiersAcknowledged, multiplier);
        return SpeedPolicy.Decide(request, context, stable);
    }
}

/// <summary>GPL-derived floor rules and ranges from NewFolder3 be4fd99. No FSD dependency.</summary>
public static class DeepDungeonFloors
{
    public static FloorKind Classify(uint dungeon, int floor)
    {
        int last = dungeon == 1 ? 200 : 100;
        if (dungeon is < 1 or > 4 || floor < 1 || floor > last) return FloorKind.Unknown;
        if (floor == last) return FloorKind.Result;
        return floor % 10 == 0 || dungeon is 3 or 4 && floor == 99
            ? FloorKind.Boss : FloorKind.Normal;
    }

    public static bool TryRange(uint territory, out uint dungeon, out int first, out int last)
    {
        (dungeon, first) = territory switch
        {
            >= 561 and <= 565 => (1u, 1 + ((int)territory - 561) * 10),
            >= 593 and <= 607 => (1u, 51 + ((int)territory - 593) * 10),
            770 => (2u, 1), 771 => (2u, 11), 772 => (2u, 21), 782 => (2u, 31),
            773 => (2u, 41), 783 => (2u, 51), 774 => (2u, 61), 784 => (2u, 71),
            775 => (2u, 81), 785 => (2u, 91),
            >= 1099 and <= 1108 => (3u, 1 + ((int)territory - 1099) * 10),
            >= 1281 and <= 1290 => (4u, 1 + ((int)territory - 1281) * 10),
            _ => (0u, 0)
        };
        last = first == 0 ? 0 : first + 9;
        // Special territory 1333 (upstream range 1..1) is deliberately unknown.
        return dungeon != 0;
    }
}

public sealed class ContextStability
{
    private SpeedContext? previous;
    private long stableSince;
    public bool Observe(in SpeedContext current, long now)
    {
        if (previous is not { } old || old.Identity != current.Identity ||
            old.Ready != current.Ready || old.CanWalk != current.CanWalk ||
            old.InDuty != current.InDuty || old.InDeepDungeon != current.InDeepDungeon ||
            old.Kind != current.Kind)
        {
            previous = current;
            stableSince = now;
        }
        return current.Ready && current.CanWalk && now - stableSince >= SpeedPolicy.ContextStableMilliseconds;
    }
    public void Reset() => previous = null;
}

/// <summary>An immutable, short-lived managed grant. Never writes native speed fields.</summary>
public sealed record SpeedLease(bool Apply, float Multiplier, FloorIdentity Identity, long ExpiresAt)
{
    public static SpeedLease Released { get; } = new(false, 1, default, 0);
    public static SpeedLease Create(in SpeedDecision decision, in SpeedContext context, long now) =>
        decision.Apply && decision.Multiplier != 1 && SpeedPolicy.ValidMultiplier(decision.Multiplier) &&
        SpeedPolicy.IsEligibleFloor(context)
            ? new(true, decision.Multiplier, context.Identity, now + SpeedPolicy.LeaseMilliseconds) : Released;
    public bool Allows(in SpeedContext live, long now) => Apply && now < ExpiresAt &&
        SpeedPolicy.ValidMultiplier(Multiplier) && live.Identity == Identity && SpeedPolicy.IsEligibleFloor(live);
}

public readonly record struct SpeedObservation(long At, float OriginalTarget, float ReturnedTarget,
    float CurrentSpeed, float BaseSpeed, bool HasInput);

