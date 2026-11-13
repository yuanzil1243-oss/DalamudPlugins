using System.Collections.Concurrent;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Encodings.Web;
using Dalamud.Hooking;
using global::Dalamud.Game.ClientState.Objects.Types;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime.Helpers;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;
using OmenTools.Interop.Game;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Combat;

/// <summary>Local evidence only. Observe packets, resolve names on the framework thread, retain deaths separately from rotating run logs.</summary>
internal sealed unsafe class DeathRecorder : IDisposable
{
    private Hook<ActionEffectHandler.Delegates.Receive>? _hook;
    private readonly ConcurrentQueue<RawDamage> _pending = new();
    private readonly Queue<DamageEvidence> _recent = new();
    private readonly Queue<object> _hpSamples = new();
    private readonly Dictionary<uint, (string Name, uint NameId, uint BaseId)> _names = new();
    private volatile bool _watching;
    private volatile uint _player, _hp;
    private bool _unavailable, _deadRecorded;
    private uint _lastHp;
    private int _floor;
    private readonly record struct RawDamage(DateTime At, uint Source, uint Action, byte Type, int Amount, uint Sequence, uint HpBefore);
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public void Tick(RunContext context)
    {
        var player = Service.LocalPlayer;
        _watching = context.Duty.IsInDuty && !context.Duty.IsTransitioning && player != null;
        if (!_watching) { Drain(); return; }
        _player = player!.EntityId;
        if (!_unavailable && _hook == null)
        {
            try
            {
                _hook = Service.GameInteropProvider.HookFromAddress<ActionEffectHandler.Delegates.Receive>(
                    ActionEffectHandler.Addresses.Receive.Value, Receive);
                NativeDeepDungeonLogMessageSource.EnableHook(_hook, "FSD.DeathEvidence");
            }
            catch (Exception e)
            {
                _hook?.Dispose(); _hook = null; _unavailable = true;
                context.RecordDiagnostic?.Invoke("death-recorder-unavailable", new { error = e.Message });
            }
        }
        if (_floor != context.Duty.Floor)
        { _floor = context.Duty.Floor; _recent.Clear(); _hpSamples.Clear(); _names.Clear(); _deadRecorded = false; Drain(); }
        foreach (var obj in Service.GameObjects)
            if (obj is IBattleChara npc)
                _names[npc.EntityId] = (npc.Name.TextValue, npc.NameId, npc.BaseId);
        while (_pending.TryDequeue(out var d))
        {
            var source = _names.GetValueOrDefault(d.Source, ("未知來源", 0u, 0u));
            string skill = $"Action {d.Type}:{d.Action}";
            if (d.Type == 1)
                try { skill = Service.DataManager.GetExcelSheet<global::Lumina.Excel.Sheets.Action>()?.GetRow(d.Action).Name.ExtractText() ?? skill; } catch { }
            var evidence = new DamageEvidence(d.At, d.Source, source.Item2, source.Item3, source.Item1,
                d.Action, d.Type, skill, d.Amount, d.Sequence, d.HpBefore);
            _recent.Enqueue(evidence);
            context.RecordDiagnostic?.Invoke("player-damage", evidence);
        }
        var now = DateTime.UtcNow;
        while (_recent.Count > 128 || _recent.TryPeek(out var first) && now - first.AtUtc > TimeSpan.FromSeconds(30)) _recent.Dequeue();
        if (player.CurrentHp != _lastHp)
        {
            _hpSamples.Enqueue(new { atUtc = now, hp = player.CurrentHp, maxHp = player.MaxHp });
            while (_hpSamples.Count > 128) _hpSamples.Dequeue();
            _lastHp = player.CurrentHp;
        }
        if (!player.IsDead && player.CurrentHp > 0) { _hp = player.CurrentHp; _deadRecorded = false; return; }
        if (_deadRecorded) return;
        var last = DeathEvidencePolicy.RecentDamage(_recent, now);
        var lethal = DeathEvidencePolicy.IsLethalCandidate(last);
        var report = new
        {
            localBuild = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == "NewFolder3")?.GetName().Version?.ToString() ?? "unknown", deathAtUtc = now, dungeonId = context.Duty.DungeonId, floor = _floor,
            mode = context.FarmingPlan?.DisplayName, lastPositiveHp = _hp, maxHp = player.MaxHp,
            attribution = lethal ? "direct-damage-lethal-candidate" : last != null ? "recent-damage-only-not-confirmed-killer" : "unknown-no-direct-damage-event",
            lastDamage = last, recentDamage = _recent.ToArray(), hpSamples = _hpSamples.ToArray(),
            statuses = player.StatusList.Select(s => new { id = s.StatusId, s.SourceId, s.Param, s.RemainingTime }).ToArray(),
            position = new { player.Position.X, player.Position.Y, player.Position.Z }, observerAvailable = !_unavailable,
            note = "僅本機保存。致死候選按最近直接傷害與命中前HP推定；DOT／地形／未觀測傷害不得冒認為最近技能擊殺。狀態來源及HP變化供人工核驗。"
        };
        try
        {
            string dir = Path.Combine(Service.PluginInterface.GetPluginConfigDirectory(), "DeathRecords");
            Directory.CreateDirectory(dir);
            string path = Path.Combine(dir, $"{now:yyyyMMdd-HHmmss-fff}-floor{_floor}-{Guid.NewGuid():N}.json");
            File.WriteAllText(path, JsonSerializer.Serialize(report, Json), new UTF8Encoding(false));
            context.RecordDiagnostic?.Invoke("player-death-evidence-saved", new { path, floor = _floor,
                monster = last?.Monster, skill = last?.Skill, actionId = last?.ActionId, damage = last?.Damage, lethalCandidate = lethal });
            Service.Log.Warning($"[FSD.Death] floor={_floor}; source={last?.Monster}; skill={last?.Skill}; damage={last?.Damage}; lethalCandidate={lethal}; saved={path}");
            _deadRecorded = true;
        }
        catch (Exception e) { Service.Log.Error($"[FSD.Death] Cannot preserve evidence: {e.Message}"); }
    }

    private void Receive(uint source, Character* caster, Vector3* position, ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects, GameObjectId* targets)
    {
        try
        {
            if (_watching && header != null && effects != null && targets != null && header->NumTargets <= 64)
                for (int i = 0; i < header->NumTargets; i++)
                {
                    if ((uint)(ulong)targets[i] != _player) continue;
                    for (int j = 0; j < 8; j++)
                    {
                        var effect = effects[i].Effects[j];
                        if (effect.Type is not (3 or 5 or 6) || (effect.Param4 & 0x80) != 0) continue;
                        int damage = DeathEvidencePolicy.DecodeDamage(effect.Param3, effect.Param4, effect.Value);
                        if (damage > 0 && _pending.Count < 256)
                            _pending.Enqueue(new(DateTime.UtcNow, source, header->ActionId, header->ActionType, damage, header->GlobalSequence, _hp));
                    }
                }
        }
        catch { /* Observation must never interrupt the game's original handler. */ }
        finally { _hook!.Original(source, caster, position, header, effects, targets); }
    }
    private void Drain() { while (_pending.TryDequeue(out _)) { } }
    public void Dispose() { _watching = false; _hook?.Dispose(); _hook = null; Drain(); }
}
