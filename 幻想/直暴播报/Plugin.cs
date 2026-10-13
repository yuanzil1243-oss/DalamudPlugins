using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
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
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string ToggleCommand = "/zhijibao";
    private const int MaxInputLength = 64;
    private bool enabled = true;
    private bool settingsOpen;
    private bool wasInCombat;
    private bool deathSeparatorPrintedThisCombat;
    private readonly Configuration config;
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
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        wasInCombat = Condition[ConditionFlag.InCombat];

        FlyTextGui.FlyTextCreated += OnFlyTextCreated;
        Condition.ConditionChange += OnConditionChange;
        PluginInterface.UiBuilder.Draw += DrawSettings;
        PluginInterface.UiBuilder.OpenConfigUi += OpenSettings;
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

        CommandManager.AddHandler(ToggleCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "开启/关闭播报；输入 /zhijibao config 打开输出设置。",
        });
        PluginInterface.SavePluginConfig(config);
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

        var (label, suffix, colorKey) = kind switch
        {
            FlyTextKind.DamageDh or FlyTextKind.AutoAttackOrDotDh => ("直击", config.DirectHitSuffix, config.DirectHitColor),
            FlyTextKind.DamageCrit or FlyTextKind.AutoAttackOrDotCrit => ("暴击", config.CriticalSuffix, config.CriticalColor),
            FlyTextKind.DamageCritDh or FlyTextKind.AutoAttackOrDotCritDh => ("直暴", config.DirectCriticalSuffix, config.DirectCriticalColor),
            _ => (null, null, (ushort)0),
        };

        if (label is null)
            return;

        var skillName = text1.ToString().Trim();
        if (string.IsNullOrWhiteSpace(skillName))
            return;

        var message = $"{skillName}{config.Delimiter}{label}{config.Delimiter}{val1:N0}{suffix}";
        if (colorKey == 0)
        {
            ChatGui.Print(message);
        }
        else
        {
            ChatGui.Print(new SeStringBuilder().AddUiForeground(message, colorKey).AddUiForegroundOff().Build());
        }
    }

    private void OnConditionChange(ConditionFlag flag, bool value)
    {
        if (flag == ConditionFlag.InCombat)
        {
            if (value)
            {
                wasInCombat = true;
                deathSeparatorPrintedThisCombat = false;
            }
            else if (wasInCombat)
            {
                if (!deathSeparatorPrintedThisCombat)
                    PrintCombatSeparator();

                wasInCombat = false;
                deathSeparatorPrintedThisCombat = false;
            }
        }
        else if (flag == ConditionFlag.Unconscious && value)
        {
            PrintCombatSeparator();
            if (wasInCombat)
                deathSeparatorPrintedThisCombat = true;
        }
    }

    private void PrintCombatSeparator()
    {
        if (enabled && !string.IsNullOrEmpty(config.CombatSeparator))
            ChatGui.Print(config.CombatSeparator);
    }

    private void OnCommand(string command, string args)
    {
        var argument = args.Trim();
        if (argument.Equals("config", StringComparison.OrdinalIgnoreCase) || argument == "设置")
        {
            settingsOpen = true;
            return;
        }

        if (argument.Equals("test", StringComparison.OrdinalIgnoreCase) || argument == "预览")
        {
            PrintPreview("直击", config.DirectHitSuffix, config.DirectHitColor);
            PrintPreview("暴击", config.CriticalSuffix, config.CriticalColor);
            PrintPreview("直暴", config.DirectCriticalSuffix, config.DirectCriticalColor);
            return;
        }

        enabled = !enabled;
        ChatGui.Print($"直暴播报已{(enabled ? "开启" : "关闭")}。输入 /zhijibao config 可设置输出格式。");
    }

    private void PrintPreview(string label, string suffix, ushort colorKey)
    {
        var text = $"技能名称{config.Delimiter}{label}{config.Delimiter}12,345{suffix}";
        if (colorKey == 0)
            ChatGui.Print(text);
        else
            ChatGui.Print(new SeStringBuilder().AddUiForeground(text, colorKey).AddUiForegroundOff().Build());
    }

    private void OpenSettings() => settingsOpen = true;

    private void DrawSettings()
    {
        if (!settingsOpen)
            return;

        if (!ImGui.Begin("直暴播报 - 输出设置", ref settingsOpen, ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.End();
            return;
        }

        var changed = false;
        var delimiter = config.Delimiter;
        var combatSeparator = config.CombatSeparator;
        var directHitColor = config.DirectHitColor;
        var directHitSuffix = config.DirectHitSuffix;
        var criticalColor = config.CriticalColor;
        var criticalSuffix = config.CriticalSuffix;
        var directCriticalColor = config.DirectCriticalColor;
        var directCriticalSuffix = config.DirectCriticalSuffix;

        changed |= ImGui.InputText("统一间隔符", ref delimiter, MaxInputLength);
        changed |= ImGui.InputText("战斗/死亡分隔文本", ref combatSeparator, MaxInputLength);
        ImGui.TextDisabled("颜色编号采用游戏 UIColor 色号；设为 0 时使用默认聊天颜色。");

        DrawHitSettings("直击", ref directHitColor, ref directHitSuffix, ref changed);
        DrawHitSettings("暴击", ref criticalColor, ref criticalSuffix, ref changed);
        DrawHitSettings("直暴", ref directCriticalColor, ref directCriticalSuffix, ref changed);

        if (ImGui.Button("预览三种格式"))
        {
            PrintPreview("直击", config.DirectHitSuffix, config.DirectHitColor);
            PrintPreview("暴击", config.CriticalSuffix, config.CriticalColor);
            PrintPreview("直暴", config.DirectCriticalSuffix, config.DirectCriticalColor);
        }

        ImGui.SameLine();
        ImGui.TextDisabled("示例：技能名称 间隔符 直击 间隔符 12,345 末尾符号");

        if (changed)
        {
            config.Delimiter = delimiter;
            config.CombatSeparator = combatSeparator;
            config.DirectHitColor = directHitColor;
            config.DirectHitSuffix = directHitSuffix;
            config.CriticalColor = criticalColor;
            config.CriticalSuffix = criticalSuffix;
            config.DirectCriticalColor = directCriticalColor;
            config.DirectCriticalSuffix = directCriticalSuffix;
            PluginInterface.SavePluginConfig(config);
        }

        ImGui.End();
    }

    private static void DrawHitSettings(string label, ref ushort color, ref string suffix, ref bool changed)
    {
        ImGui.Separator();
        ImGui.Text(label);
        var colorInput = (int)color;
        if (ImGui.InputInt($"{label}文字颜色编号", ref colorInput))
        {
            color = (ushort)Math.Clamp(colorInput, 0, ushort.MaxValue);
            changed = true;
        }

        changed |= ImGui.InputText($"{label}伤害末尾符号", ref suffix, MaxInputLength);
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(ToggleCommand);
        FlyTextGui.FlyTextCreated -= OnFlyTextCreated;
        Condition.ConditionChange -= OnConditionChange;
        PluginInterface.UiBuilder.Draw -= DrawSettings;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        actionHook?.Dispose();
    }
}

