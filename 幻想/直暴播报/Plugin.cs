using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Hooking;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using Lumina.Text.Payloads;
using GameSeStringBuilder = Lumina.Text.SeStringBuilder;

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
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;

    private const string ToggleCommand = "/zhijibao";
    private const int MaxInputLength = 64;
    private const string CountdownTimerSignature = "40 53 48 83 EC 40 80 79 38 00";
    private bool enabled = true;
    private bool settingsOpen;
    private bool wasInCombat;
    private bool deathSeparatorPrintedThisCombat;
    private bool countdownAnnouncementSent;
    private long countdownDataPointer;
    private readonly Configuration config;
    private readonly object pendingHitsLock = new();
    private readonly List<PendingHit> pendingHits = new();
    private const long HitCorrelationWindowMs = 2000;
    private int loggedLocalHitHook;
    private int loggedFlyTextEvent;

    private sealed record PendingHit(FlyTextKind Kind, int Damage, long CreatedAt);
    private readonly Hook<AddToScreenLogDelegate>? actionHook;
    private readonly Hook<CountdownTimerDelegate>? countdownTimerHook;

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

    [UnmanagedFunctionPointer(CallingConvention.ThisCall)]
    private delegate IntPtr CountdownTimerDelegate(ulong countdownData);

    public Plugin()
    {
        config = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        config.Version = 2;
        wasInCombat = Condition[ConditionFlag.InCombat];

        FlyTextGui.FlyTextCreated += OnFlyTextCreated;
        Condition.ConditionChange += OnConditionChange;
        Framework.Update += OnFrameworkUpdate;
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

        try
        {
            var address = SigScanner.ScanText(CountdownTimerSignature);
            countdownTimerHook = GameInteropProvider.HookFromAddress<CountdownTimerDelegate>(address, CountdownTimerDetour);
            countdownTimerHook.Enable();
        }
        catch (Exception exception)
        {
            countdownTimerHook = null;
            PluginLog.Warning(exception, "无法定位游戏战斗倒计时；直暴播报的其他功能仍可使用。");
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

    private IntPtr CountdownTimerDetour(ulong countdownData)
    {
        System.Threading.Interlocked.Exchange(ref countdownDataPointer, unchecked((long)countdownData));
        return countdownTimerHook!.Original(countdownData);
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        var pointer = System.Threading.Interlocked.Read(ref countdownDataPointer);
        if (pointer == 0)
            return;

        try
        {
            var active = Marshal.ReadByte(new IntPtr(pointer + 0x38)) == 1;
            var secondsRemaining = Marshal.PtrToStructure<float>(new IntPtr(pointer + 0x2c));
            active &= secondsRemaining > 0f;

            if (!active)
            {
                countdownAnnouncementSent = false;
                return;
            }

            if (enabled && config.CountdownAnnouncementEnabled && !countdownAnnouncementSent && secondsRemaining <= 1f)
            {
                PrintTextWithIcon(config.CountdownAnnouncementText, config.CountdownAnnouncementIcon);
                countdownAnnouncementSent = true;
            }
        }
        catch (Exception exception)
        {
            PluginLog.Warning(exception, "读取游戏倒计时失败；本次倒计时播报已停用。");
            countdownAnnouncementSent = true;
        }
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

        var (label, suffix, suffixIcon, paletteColor, customColor, useCustomColor) = kind switch
        {
            FlyTextKind.DamageDh or FlyTextKind.AutoAttackOrDotDh =>
                ("直击", config.DirectHitSuffix, config.DirectHitSuffixIcon, config.DirectHitColor, config.DirectHitCustomColor, config.DirectHitUseCustomColor),
            FlyTextKind.DamageCrit or FlyTextKind.AutoAttackOrDotCrit =>
                ("暴击", config.CriticalSuffix, config.CriticalSuffixIcon, config.CriticalColor, config.CriticalCustomColor, config.CriticalUseCustomColor),
            FlyTextKind.DamageCritDh or FlyTextKind.AutoAttackOrDotCritDh =>
                ("直暴", config.DirectCriticalSuffix, config.DirectCriticalSuffixIcon, config.DirectCriticalColor, config.DirectCriticalCustomColor, config.DirectCriticalUseCustomColor),
            _ => (null, null, 0, (ushort)0, Vector4.One, false),
        };

        if (label is null)
            return;

        var skillName = text1.ToString().Trim();
        if (string.IsNullOrWhiteSpace(skillName))
            return;

        var builder = BeginMessage(paletteColor, customColor, useCustomColor);
        builder.Append(skillName);
        AppendTextAndIcon(builder, config.Delimiter, config.DelimiterIcon);
        builder.Append(label);
        AppendTextAndIcon(builder, config.Delimiter, config.DelimiterIcon);
        builder.Append($"{val1:N0}");
        AppendTextAndIcon(builder, suffix ?? string.Empty, suffixIcon);
        PrintBuiltMessage(builder, paletteColor != 0 || useCustomColor);
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
        if (enabled && (!string.IsNullOrEmpty(config.CombatSeparator) || IsValidIcon(config.CombatSeparatorIcon)))
            PrintTextWithIcon(config.CombatSeparator, config.CombatSeparatorIcon);
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
            PrintPreview("直击", config.DirectHitSuffix, config.DirectHitSuffixIcon, config.DirectHitColor, config.DirectHitCustomColor, config.DirectHitUseCustomColor);
            PrintPreview("暴击", config.CriticalSuffix, config.CriticalSuffixIcon, config.CriticalColor, config.CriticalCustomColor, config.CriticalUseCustomColor);
            PrintPreview("直暴", config.DirectCriticalSuffix, config.DirectCriticalSuffixIcon, config.DirectCriticalColor, config.DirectCriticalCustomColor, config.DirectCriticalUseCustomColor);
            PrintTextWithIcon(config.CountdownAnnouncementText, config.CountdownAnnouncementIcon);
            return;
        }

        enabled = !enabled;
        ChatGui.Print($"直暴播报已{(enabled ? "开启" : "关闭")}。输入 /zhijibao config 可设置输出格式。");
    }

    private void PrintPreview(string label, string suffix, int suffixIcon, ushort paletteColor, Vector4 customColor, bool useCustomColor)
    {
        var builder = BeginMessage(paletteColor, customColor, useCustomColor);
        builder.Append("技能名称");
        AppendTextAndIcon(builder, config.Delimiter, config.DelimiterIcon);
        builder.Append(label);
        AppendTextAndIcon(builder, config.Delimiter, config.DelimiterIcon);
        builder.Append("12,345");
        AppendTextAndIcon(builder, suffix, suffixIcon);
        PrintBuiltMessage(builder, paletteColor != 0 || useCustomColor);
    }

    private static GameSeStringBuilder BeginMessage(ushort paletteColor, Vector4 customColor, bool useCustomColor)
    {
        var builder = new GameSeStringBuilder();
        if (useCustomColor)
            builder.PushColorRgba(customColor);
        else if (paletteColor != 0)
            builder.PushColorType(paletteColor);
        return builder;
    }

    private static void AppendTextAndIcon(GameSeStringBuilder builder, string text, int iconId)
    {
        if (!string.IsNullOrEmpty(text))
            builder.Append(text);

        if (IsValidIcon(iconId))
            builder.BeginMacro(MacroCode.Icon).AppendUIntExpression((uint)iconId).EndMacro();
    }

    private void PrintTextWithIcon(string text, int iconId)
    {
        if (string.IsNullOrEmpty(text) && !IsValidIcon(iconId))
            return;

        var builder = new GameSeStringBuilder();
        AppendTextAndIcon(builder, text, iconId);
        PrintBuiltMessage(builder, false);
    }

    private void PrintBuiltMessage(GameSeStringBuilder builder, bool hasColor)
    {
        if (hasColor)
            builder.PopColor();

        ChatGui.Print(SeString.Parse(builder.GetViewAsSpan()));
    }

    private static bool IsValidIcon(int iconId) =>
        iconId > 0 && Enum.IsDefined(typeof(BitmapFontIcon), iconId);

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
        var delimiterIcon = config.DelimiterIcon;
        var combatSeparator = config.CombatSeparator;
        var combatSeparatorIcon = config.CombatSeparatorIcon;
        var directHitColor = config.DirectHitColor;
        var directHitCustomColor = config.DirectHitCustomColor;
        var directHitUseCustomColor = config.DirectHitUseCustomColor;
        var directHitSuffix = config.DirectHitSuffix;
        var directHitSuffixIcon = config.DirectHitSuffixIcon;
        var criticalColor = config.CriticalColor;
        var criticalCustomColor = config.CriticalCustomColor;
        var criticalUseCustomColor = config.CriticalUseCustomColor;
        var criticalSuffix = config.CriticalSuffix;
        var criticalSuffixIcon = config.CriticalSuffixIcon;
        var directCriticalColor = config.DirectCriticalColor;
        var directCriticalCustomColor = config.DirectCriticalCustomColor;
        var directCriticalUseCustomColor = config.DirectCriticalUseCustomColor;
        var directCriticalSuffix = config.DirectCriticalSuffix;
        var directCriticalSuffixIcon = config.DirectCriticalSuffixIcon;
        var countdownEnabled = config.CountdownAnnouncementEnabled;
        var countdownText = config.CountdownAnnouncementText;
        var countdownIcon = config.CountdownAnnouncementIcon;

        changed |= DrawTextIconEditor("间隔符", ref delimiter, ref delimiterIcon);
        changed |= DrawTextIconEditor("战斗/死亡分隔文本", ref combatSeparator, ref combatSeparatorIcon);
        ImGui.TextDisabled("间隔符与末尾符号可同时使用文本和游戏内图标；图标可留空。");

        DrawHitSettings("直击", ref directHitColor, ref directHitCustomColor, ref directHitUseCustomColor,
            ref directHitSuffix, ref directHitSuffixIcon, ref changed);
        DrawHitSettings("暴击", ref criticalColor, ref criticalCustomColor, ref criticalUseCustomColor,
            ref criticalSuffix, ref criticalSuffixIcon, ref changed);
        DrawHitSettings("直暴", ref directCriticalColor, ref directCriticalCustomColor, ref directCriticalUseCustomColor,
            ref directCriticalSuffix, ref directCriticalSuffixIcon, ref changed);

        ImGui.Separator();
        ImGui.Text("战斗开始倒计时");
        changed |= ImGui.Checkbox("到最后1秒时播报", ref countdownEnabled);
        changed |= DrawTextIconEditor("倒计时播报文本", ref countdownText, ref countdownIcon);
        if (countdownTimerHook is null)
            ImGui.TextDisabled("当前游戏版本未能识别倒计时信号；请查看 Dalamud 日志获取适配状态。");

        if (ImGui.Button("预览三种格式"))
        {
            PrintPreview("直击", config.DirectHitSuffix, config.DirectHitSuffixIcon, config.DirectHitColor, config.DirectHitCustomColor, config.DirectHitUseCustomColor);
            PrintPreview("暴击", config.CriticalSuffix, config.CriticalSuffixIcon, config.CriticalColor, config.CriticalCustomColor, config.CriticalUseCustomColor);
            PrintPreview("直暴", config.DirectCriticalSuffix, config.DirectCriticalSuffixIcon, config.DirectCriticalColor, config.DirectCriticalCustomColor, config.DirectCriticalUseCustomColor);
        }

        ImGui.SameLine();
        ImGui.TextDisabled("例：技能名 + 间隔符 + 直暴 + 间隔符 + 12,345 + 末尾符号");

        if (changed)
        {
            config.Delimiter = delimiter;
            config.DelimiterIcon = delimiterIcon;
            config.CombatSeparator = combatSeparator;
            config.CombatSeparatorIcon = combatSeparatorIcon;
            config.DirectHitColor = directHitColor;
            config.DirectHitCustomColor = directHitCustomColor;
            config.DirectHitUseCustomColor = directHitUseCustomColor;
            config.DirectHitSuffix = directHitSuffix;
            config.DirectHitSuffixIcon = directHitSuffixIcon;
            config.CriticalColor = criticalColor;
            config.CriticalCustomColor = criticalCustomColor;
            config.CriticalUseCustomColor = criticalUseCustomColor;
            config.CriticalSuffix = criticalSuffix;
            config.CriticalSuffixIcon = criticalSuffixIcon;
            config.DirectCriticalColor = directCriticalColor;
            config.DirectCriticalCustomColor = directCriticalCustomColor;
            config.DirectCriticalUseCustomColor = directCriticalUseCustomColor;
            config.DirectCriticalSuffix = directCriticalSuffix;
            config.DirectCriticalSuffixIcon = directCriticalSuffixIcon;
            config.CountdownAnnouncementEnabled = countdownEnabled;
            config.CountdownAnnouncementText = countdownText;
            config.CountdownAnnouncementIcon = countdownIcon;
            PluginInterface.SavePluginConfig(config);
        }

        ImGui.End();
    }

    private static void DrawHitSettings(
        string label,
        ref ushort paletteColor,
        ref Vector4 customColor,
        ref bool useCustomColor,
        ref string suffix,
        ref int suffixIcon,
        ref bool changed)
    {
        ImGui.Separator();
        ImGui.Text(label);
        if (ImGui.Checkbox($"使用自定义颜色##useCustom-{label}", ref useCustomColor))
            changed = true;

        if (ImGui.ColorEdit4($"文字颜色##color-{label}", ref customColor))
        {
            useCustomColor = true;
            changed = true;
        }

        if (!useCustomColor)
            ImGui.TextDisabled(paletteColor == 0 ? "当前使用游戏默认聊天颜色" : $"当前使用旧版游戏色号 {paletteColor}；启用上方色盘可选自定义色");

        changed |= DrawTextIconEditor($"{label}伤害末尾符号", ref suffix, ref suffixIcon);
    }

    private static bool DrawTextIconEditor(string label, ref string text, ref int iconId)
    {
        var changed = ImGui.InputText($"{label}##text-{label}", ref text, MaxInputLength);
        ImGui.SameLine();

        var iconName = Enum.IsDefined(typeof(BitmapFontIcon), iconId) && iconId != 0
            ? ((BitmapFontIcon)iconId).ToString()
            : "无图标";
        if (ImGui.BeginCombo($"图标##icon-{label}", iconName))
        {
            foreach (var icon in Enum.GetValues<BitmapFontIcon>().Distinct().OrderBy(value => (int)value))
            {
                var isSelected = (int)icon == iconId;
                var itemName = icon == BitmapFontIcon.None ? "无图标" : icon.ToString();
                if (ImGui.Selectable($"{itemName}##{(int)icon}", isSelected))
                {
                    iconId = (int)icon;
                    changed = true;
                }

                if (isSelected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }

        return changed;
    }

    public void Dispose()
    {
        CommandManager.RemoveHandler(ToggleCommand);
        FlyTextGui.FlyTextCreated -= OnFlyTextCreated;
        Condition.ConditionChange -= OnConditionChange;
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= DrawSettings;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        actionHook?.Dispose();
        countdownTimerHook?.Dispose();
    }
}
