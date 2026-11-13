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
    public bool PriorityFight => Focus != null && MobCombatPolicy.Priority(_dungeon, _floor, Focus.NameId) > 0;
    public bool DangerousEngaged => Engaged.Any(e => MobPullPolicy.RequiresSolo(_mode, _dungeon, _floor, e.NameId));
    public bool MovementTaskPaused => Consolidating || PriorityFight;
    public bool Relocating { get; set; }
    public bool PauseRotationForRelocation { get; set; }

    public unsafe bool TryUpdateConsolidation(InstanceContentDeepDungeon* dd, FloorExplorationController floor)
    {
        if (!Consolidating || dd == null || floor.Kind != FloorRuntimeKind.Normal ||
            !MobCombatPolicy.HighFloorEnabled(_mode, floor.DungeonId, floor.Floor))
            return false;

        // Any existing hate pauses exploration; the configured limit is a ceiling,
        // never a quota. Do not return across occupied rooms to a remote empty room.
        floor.UpdateHeldCombat(dd);
        floor.SetCombatConsolidationStatus($"91–98层就近清理当前接战怪：{Engaged.Count}只");
        return true;
    }

    public void Tick(RunContext context)
    {
        var now = DateTime.UtcNow;
        if (now < _nextScan) return;
        _nextScan = now.AddMilliseconds(100);
        var player = Service.LocalPlayer;
        if (player == null || player.IsDead || !context.Duty.IsInDuty || context.Duty.IsTransitioning ||
            context.Duty.IsBossFloor || context.Duty.DungeonId != 4 || !ProgressionModePolicy.IsProgression(context.FarmingPlan?.Mode))
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
                if (EnemyChaseHelper.IsAggroedToPlayer(npc.GameObjectId) || npc.TargetObjectId == player.GameObjectId)
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
        var previous = _sticky;
        _sticky = choice?.Id ?? 0;
        Focus = engaged.FirstOrDefault(e => e.GameObjectId == _sticky);
        bool before = Consolidating;
        _consolidation.Update(MobCombatPolicy.HighFloorEnabled(context.FarmingPlan?.Mode, _dungeon, _floor),
            engaged.Count, 1);
        UpdateOutputTarget(context);
        if (_sticky != previous || before != Consolidating)
            context.RecordDiagnostic?.Invoke("mob-combat-policy", new { floor = _floor, count = engaged.Count,
                limit = context.Configuration.Combat.HighFloorEnemyLimit, consolidating = Consolidating,
                focus = _sticky, name = Focus?.Name.TextValue, priority = Focus == null ? 0 : MobCombatPolicy.Priority(_dungeon, _floor, Focus.NameId),
                enemies = engaged.Select(e => new { id = e.GameObjectId, nameId = e.NameId, name = e.Name.TextValue, hp = e.CurrentHp }).ToArray() });
    }

    public void ApplyPriorityOutput(RunContext context)
    {
        if (PriorityFight && !context.NormalCombatOutputSuppressed)
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
        _pendingSupplement = 0; _nextSupplementAt = default;
        ClearOwnedMarker(); _sticky = 0; Focus = null; Engaged = []; Unengaged = [];
        OutputTarget = null; OutputTargetHittable = false; _outputSelection.Reset();
        _consolidation.Update(false, 0, 3); Relocating = PauseRotationForRelocation = false;
    }
    public void Dispose() => Reset();
}
