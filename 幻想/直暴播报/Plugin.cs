using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Hooking;
using FFXIVClientStructs.FFXIV.Client.Game.Character;

namespace CritChat;

public sealed unsafe class Plugin : IDalamudPlugin
{
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static ISigScanner SigScanner { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFlyTextGui FlyTextGui { get; private set; } = null!;
    [PluginService] internal static IPluginLog PluginLog { get; private set; } = null!;

    private const string ToggleCommand = "/zhijibao";
    private bool enabled = true;
    private readonly object pendingHitsLock = new();
    private readonly List<PendingHit> pendingHits = new();
    private const long HitCorrelationWindowMs = 2000;
    private int loggedLocalHitHook;
    private int loggedFlyTextEvent;

    private sealed record PendingHit(FlyTextKind Kind, int Damage, long CreatedAt);
    private readonly Hook<AddToScreenLogDelegate>? actionHook;

    private delegate void AddToScreenLogDelegate(
        Character* target,
        Character* source,
        FlyTextKind kind,
        byte option,
        byte actionKind,
        int actionId,
        int val1,
        int val2,
        byte damageType);

    public Plugin()
    {
        FlyTextGui.FlyTextCreated += OnFlyTextCreated;
        try
        {
            var address = SigScanner.ScanText("E8 ?? ?? ?? ?? BF ?? ?? ?? ?? EB 39");
            actionHook = GameInteropProvider.HookFromAddress<AddToScreenLogDelegate>(address, AddToScreenLogDetour);
            actionHook.Enable();
        }
        catch (Exception exception)
        {
            actionHook = null;
            PluginLog.Error(exception, "无法定位游戏伤害飘字入口；为避免播报其他玩家的伤害，本次加载不会输出伤害消息。");
            ChatGui.PrintError("直暴播报：当前游戏版本无法识别本地伤害来源，请等待插件适配更新。");
        }
        CommandManager.AddHandler(ToggleCommand, new CommandInfo(OnToggle)
        {
            HelpMessage = "开启或关闭直暴伤害聊天播报。",
        });
    }

    private static bool IsBroadcastKind(FlyTextKind kind) => kind is
        FlyTextKind.DamageDh or FlyTextKind.AutoAttackOrDotDh or
        FlyTextKind.DamageCrit or FlyTextKind.AutoAttackOrDotCrit or
        FlyTextKind.DamageCritDh or FlyTextKind.AutoAttackOrDotCritDh;

    public string Name => "直暴播报";

    private void AddToScreenLogDetour(
        Character* target,
        Character* source,
        FlyTextKind kind,
        byte option,
        byte actionKind,
        int actionId,
        int val1,
        int val2,
        byte damageType)
    {
        var playerEntityId = ObjectTable.LocalPlayer?.EntityId;
        var isLocalPlayer = source != null && playerEntityId.HasValue &&
                            source->GameObject.EntityId == playerEntityId.Value;

        if (isLocalPlayer && IsBroadcastKind(kind) && val1 > 0)
        {
            var now = Environment.TickCount64;
            lock (pendingHitsLock)
            {
                pendingHits.RemoveAll(hit => now - hit.CreatedAt > HitCorrelationWindowMs);
                pendingHits.Add(new PendingHit(kind, val1, now));
            }

            if (System.Threading.Interlocked.Exchange(ref loggedLocalHitHook, 1) == 0)
                PluginLog.Information($"已捕获本地伤害事件：{kind}，伤害={val1}。");
        }

        actionHook!.Original(target, source, kind, option, actionKind, actionId, val1, val2, damageType);
    }

    private void OnFlyTextCreated(
        ref FlyTextKind kind,
        ref int val1,
        ref int val2,
        ref Dalamud.Game.Text.SeStringHandling.SeString text1,
        ref Dalamud.Game.Text.SeStringHandling.SeString text2,
        ref uint color,
        ref uint icon,
        ref uint damageTypeIcon,
        ref float yOffset,
        ref bool handled)
    {
        if (!enabled || val1 <= 0 || !IsBroadcastKind(kind))
            return;

        if (System.Threading.Interlocked.Exchange(ref loggedFlyTextEvent, 1) == 0)
            PluginLog.Information($"已收到伤害飘字：{kind}，伤害={val1}，技能文本={text1}。");

        var now = Environment.TickCount64;
        var flyTextKind = kind;
        var flyTextDamage = val1;
        var matchedLocalHit = false;
        lock (pendingHitsLock)
        {
            pendingHits.RemoveAll(hit => now - hit.CreatedAt > HitCorrelationWindowMs);
            var matchedIndex = pendingHits.FindLastIndex(hit => hit.Kind == flyTextKind && hit.Damage == flyTextDamage);
            if (matchedIndex >= 0)
            {
                pendingHits.RemoveAt(matchedIndex);
                matchedLocalHit = true;
            }
        }

        if (!matchedLocalHit)
            return;

        var result = kind switch
        {
            FlyTextKind.DamageDh or FlyTextKind.AutoAttackOrDotDh => "直击",
            FlyTextKind.DamageCrit or FlyTextKind.AutoAttackOrDotCrit => "暴击！",
            FlyTextKind.DamageCritDh or FlyTextKind.AutoAttackOrDotCritDh => "直暴！！",
            _ => null,
        };

        if (result is null)
            return;

        var skillName = text1.ToString().Trim();
        if (string.IsNullOrWhiteSpace(skillName))
            return;

        ChatGui.Print($"{skillName} {result} {val1:N0}");
    }

    private void OnToggle(string command, string args)
    {
        enabled = !enabled;
        ChatGui.Print($"直暴播报已{(enabled ? "开启" : "关闭")}。");
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(ToggleCommand);
        FlyTextGui.FlyTextCreated -= OnFlyTextCreated;
        actionHook?.Dispose();
    }
}

