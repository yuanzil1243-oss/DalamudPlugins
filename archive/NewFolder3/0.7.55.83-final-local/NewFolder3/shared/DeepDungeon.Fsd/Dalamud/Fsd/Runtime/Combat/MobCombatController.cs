using System.Numerics;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Dalamud.Map;
using DeepDungeon.Fsd.Dalamud.Runtime.Floor;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.Types;
using FFXIVClientStructs.FFXIV.Client.Game.UI;
using OmenTools.OmenService;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

internal sealed partial class MobCombatController : IDisposable
{
    private DateTime _nextScan, _nextMarker;
    private uint _dungeon;
    private int _floor;
    private FarmingMode? _mode;
    private ulong _sticky, _ownedMarker;
    private readonly CombatConsolidationState _consolidation = new();
    public IReadOnlyList<IBattleChara> Engaged { get; private set; } = [];
    public IReadOnlyList<IBattleChara> Unengaged { get; private set; } = [];
    public IBattleChara? Focus { get; private set; }
    public bool Consolidating => _consolidation.Active;
    private int TargetPriority(IBattleChara enemy) => Hoh70FloorPolicy.Priority(_mode, _dungeon, _floor,
        enemy.BaseId, enemy.NameId, MobCombatPolicy.Priority(_dungeon, _floor, enemy.NameId));
    public bool PriorityFight => Focus != null && TargetPriority(Focus) > 0;
    public bool DangerousEngaged => Engaged.Any(e => MobPullPolicy.RequiresSolo(_mode, _dungeon, _floor, e.NameId));
    public bool MovementTaskPaused => Consolidating || PriorityFight;
    public bool Relocating { get; set; }
    public bool PauseRotationForRelocation { get; set; }
    internal int HohCombatRoom { get; private set; } = -1;

    public unsafe bool TryUpdateConsolidation(InstanceContentDeepDungeon* dd, FloorExplorationController floor)
    {
        if (!Consolidating || dd == null || floor.Kind != FloorRuntimeKind.Normal ||
            !MobCombatPolicy.UsesControlledHighFloor(_mode, floor.DungeonId, floor.Floor))
            return false;

        // HoH active groups gather before consolidation; an engaged priority enemy takes precedence separately.
        // Neither mode waits for a quota or returns to a remote consolidation room.
        floor.UpdateHeldCombat(dd);
        floor.SetCombatConsolidationStatus($"{(_dungeon == 2 ? (_floor < 90 ? "81–89" : "91–99") : "91–98")}层就近清理当前接战怪：{Engaged.Count}只");
        return true;
    }

    public unsafe void Tick(RunContext context)
    {
        var now = DateTime.UtcNow;
        if (now < _nextScan) return;
        _nextScan = now.AddMilliseconds(100);
        var player = Service.LocalPlayer;
        if (player == null || player.IsDead || !context.Duty.IsInDuty || context.Duty.IsTransitioning ||
            context.Duty.IsBossFloor || !MobCombatPolicy.TrackingEnabled(context.FarmingPlan?.Mode, context.Duty.DungeonId, context.Duty.Floor))
        { Reset(); return; }
        if (_dungeon != context.Duty.DungeonId || _floor != context.Duty.Floor)
        { Reset(); _dungeon = context.Duty.DungeonId; _floor = context.Duty.Floor; }
        _mode = context.FarmingPlan?.Mode;
        var engaged = new List<IBattleChara>();
        var idle = new List<IBattleChara>();
        foreach (var obj in Service.GameObjects)
            if (obj is IBattleChara npc && obj.ObjectKind == ObjectKind.BattleNpc &&
                (BattleNpcSubKind)obj.SubKind == BattleNpcSubKind.Combatant && npc.IsTargetable && !npc.IsDead && npc.CurrentHp > 0)
            {
                if (EnemyChaseHelper.IsAggroedToPlayer(npc.GameObjectId) || npc.TargetObjectId == player.GameObjectId ||
                    _dungeon == 2 && context.HohCombatTargets.IsEngaged(npc.GameObjectId))
                    engaged.Add(npc);
                else idle.Add(npc);
            }
        Engaged = engaged; Unengaged = idle;
        if (engaged.Count == 0)
        {
            Relocating = PauseRotationForRelocation = false;
        }
        var choice = MobCombatPolicy.Select(_dungeon, _floor, engaged.Select(n => new CombatEnemy(
            n.GameObjectId, n.NameId, n.CurrentHp, Vector3.Distance(player.Position, n.Position), true)), _sticky);
        var priorityEnemy = _dungeon == 2 ? engaged.Where(e => TargetPriority(e) > 0).OrderByDescending(TargetPriority)
            .ThenByDescending(e => e.GameObjectId == _sticky)
            .ThenBy(e => e.CurrentHp).ThenBy(e => Vector3.DistanceSquared(player.Position, e.Position)).FirstOrDefault() : null;
        if (priorityEnemy != null) choice = new CombatEnemy(priorityEnemy.GameObjectId, priorityEnemy.NameId,
            priorityEnemy.CurrentHp, Vector3.Distance(player.Position, priorityEnemy.Position), true);
        var previous = _sticky;
        _sticky = choice?.Id ?? 0;
        Focus = engaged.FirstOrDefault(e => e.GameObjectId == _sticky);
        bool before = Consolidating;
        int heldCount = context.ControlledCombatEngagedCount;
        int threshold=HohHighFloorPolicy.ConsolidationThreshold(_mode, _dungeon, _floor, context.PassageOpenForCombat);
        if (HohHighFloorPolicy.ControlsCombat(_mode,_dungeon,_floor))
            threshold=Math.Min(threshold,Math.Max(1,HohPassagePullPolicy.MaximumEngaged(context.HohPassageProgress)));
        _consolidation.Update(MobCombatPolicy.UsesControlledHighFloor(context.FarmingPlan?.Mode, _dungeon, _floor), heldCount, threshold);
        if (_dungeon == 2 && heldCount > 0 && (HohCombatRoom < 0 || HohHighFloorPolicy.EarlierGroup(_mode, _dungeon, _floor)))
        {
            var dd = (InstanceContentDeepDungeon*)context.CurrentDeepDungeon;
            if (dd != null) HohCombatRoom = context.ResolveHohCombatRoom(dd);
        }
        else if (heldCount == 0) HohCombatRoom = -1;
        UpdateOutputTarget(context);
        if (_sticky != previous || before != Consolidating)
            context.RecordDiagnostic?.Invoke("mob-combat-policy", new { floor = _floor, count = engaged.Count,
                limit = HohHighFloorPolicy.SupplementLimit(_mode, _dungeon, _floor,
                    context.Configuration.Combat.GetHighFloorEnemyLimit(_dungeon)), consolidating = Consolidating,
                combatRoom = HohCombatRoom,
                focus = _sticky, name = Focus?.Name.TextValue, priority = Focus == null ? 0 : TargetPriority(Focus),
                enemies = engaged.Select(e => new { id = e.GameObjectId, nameId = e.NameId, name = e.Name.TextValue, hp = e.CurrentHp }).ToArray() });
    }

    public void ApplyPriorityOutput(RunContext context)
    {
        if (PriorityFight && !context.NormalCombatOutputSuppressed &&
            !(context.Duty.DungeonId == 2 && context.HohPetrification.Clearing))
        {
            Service.TargetManager.Target = Focus;
            MarkPriority(context, DateTime.UtcNow);
        }
        else ClearOwnedMarker();
    }

    private unsafe void MarkPriority(RunContext context, DateTime now)
    {
        if (Focus == null || now < _nextMarker) return;
        _nextMarker = now.AddSeconds(1);
        try
        {
            var marks = MarkingController.Instance();
            if (marks == null) return;
            if ((ulong)marks->Markers[0] == Focus.GameObjectId) return;
            ChatManager.Instance().SendMessage("/mk attack1 <t>");
            _ownedMarker = Focus.GameObjectId;
            context.RecordDiagnostic?.Invoke("mob-priority-marker-requested", new { target = _ownedMarker, marker = "attack1", name = Focus.Name.TextValue });
        }
        catch (Exception e) { Service.Log.Warning($"[MobCombat] Attack marker unavailable: {e.Message}"); }
    }

    private unsafe void ClearOwnedMarker()
    {
        if (_ownedMarker == 0) return;
        try
        {
            var marks = MarkingController.Instance();
            if (marks != null && (ulong)marks->Markers[0] == _ownedMarker)
                ChatManager.Instance().SendMessage("/mk off <attack1>");
        }
        catch { }
        _ownedMarker = 0;
    }
    private void Reset()
    {
        _pendingSupplement = 0; _nextSupplementAt = _nextSupplementDiagnosticAt = default;
        _lastPassageProgress = null; _lastPassageLimit = -1;
        HohCombatRoom = -1;
        ClearOwnedMarker(); _sticky = 0; Focus = null; Engaged = []; Unengaged = [];
        OutputTarget = null; OutputTargetHittable = false; _outputSelection.Reset(); _rotationPull.Reset();
        _consolidation.Update(false, 0, 3); Relocating = PauseRotationForRelocation = false;
    }
    public void Dispose() => Reset();
}
