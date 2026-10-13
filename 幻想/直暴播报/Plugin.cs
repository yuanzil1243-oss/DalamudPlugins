using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.FlyText;
using Dalamud.Game.Text;
using Dalamud.Game.Text.SeStringHandling;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
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
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;

    private const string ToggleCommand = "/zhijibao";
    private const int MaxInputLength = 64;
    private const string CountdownTimerSignature = "40 53 48 83 EC 40 80 79 38 00";
    private bool enabled = true;
    private bool settingsOpen;
    private bool iconBrowserOpen;
    private int selectedIconCategory;
    private int iconSize = 36;
    private readonly int[] iconScanRanges = new int[IconCategories.Length];
    private readonly int[] nextIconsToScan = IconCategories.Select(category => category.Ranges[0].Start).ToArray();
    private string iconSearch = string.Empty;
    private string? iconPickerTarget;
    private (string Target, int IconId)? pendingIconSelection;
    private readonly List<int>[] iconCategoryResults = Enumerable.Range(0, IconCategories.Length).Select(_ => new List<int>()).ToArray();
    private readonly HashSet<int>[] iconCategorySets = Enumerable.Range(0, IconCategories.Length).Select(_ => new HashSet<int>()).ToArray();
    private readonly bool[] iconCategoryScanned = new bool[IconCategories.Length];
    private readonly Dictionary<int, ISharedImmediateTexture> iconTextures = new();
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
        PluginInterface.UiBuilder.Draw += DrawIconBrowser;
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
            _ => (null, null, 0, (ushort)0, 0u, false),
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

    private void PrintPreview(string label, string suffix, int suffixIcon, ushort paletteColor, uint customColor, bool useCustomColor)
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

    private static GameSeStringBuilder BeginMessage(ushort paletteColor, uint customColor, bool useCustomColor)
    {
        var builder = new GameSeStringBuilder();
        if (useCustomColor)
            builder.PushColorRgba(customColor);
        else if (paletteColor != 0)
            builder.PushColorType(paletteColor);
        return builder;
    }

    private static Vector4 UnpackRgba(uint color) => new(
        (color & 0xFF) / 255f,
        ((color >> 8) & 0xFF) / 255f,
        ((color >> 16) & 0xFF) / 255f,
        ((color >> 24) & 0xFF) / 255f);

    private static uint PackRgba(Vector4 color)
    {
        var red = (uint)Math.Clamp((int)MathF.Round(color.X * 255), 0, 255);
        var green = (uint)Math.Clamp((int)MathF.Round(color.Y * 255), 0, 255);
        var blue = (uint)Math.Clamp((int)MathF.Round(color.Z * 255), 0, 255);
        var alpha = (uint)Math.Clamp((int)MathF.Round(color.W * 255), 0, 255);
        return red | (green << 8) | (blue << 16) | (alpha << 24);
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

    private static bool IsValidIcon(int iconId) => iconId > 0 && iconId <= 1_000_000;

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

        if (pendingIconSelection is { } selection)
        {
            switch (selection.Target)
            {
                case "DelimiterIcon": delimiterIcon = selection.IconId; break;
                case "CombatSeparatorIcon": combatSeparatorIcon = selection.IconId; break;
                case "DirectHitSuffixIcon": directHitSuffixIcon = selection.IconId; break;
                case "CriticalSuffixIcon": criticalSuffixIcon = selection.IconId; break;
                case "DirectCriticalSuffixIcon": directCriticalSuffixIcon = selection.IconId; break;
                case "CountdownAnnouncementIcon": countdownIcon = selection.IconId; break;
            }
            pendingIconSelection = null;
            changed = true;
        }

        changed |= DrawTextIconEditor("间隔符", "DelimiterIcon", ref delimiter, ref delimiterIcon);
        changed |= DrawTextIconEditor("战斗/死亡分隔文本", "CombatSeparatorIcon", ref combatSeparator, ref combatSeparatorIcon);
        ImGui.TextDisabled("间隔符与末尾符号可同时使用文本和游戏内图标；图标可留空。");

        DrawHitSettings("直击", "DirectHitSuffixIcon", ref directHitColor, ref directHitCustomColor, ref directHitUseCustomColor,
            ref directHitSuffix, ref directHitSuffixIcon, ref changed);
        DrawHitSettings("暴击", "CriticalSuffixIcon", ref criticalColor, ref criticalCustomColor, ref criticalUseCustomColor,
            ref criticalSuffix, ref criticalSuffixIcon, ref changed);
        DrawHitSettings("直暴", "DirectCriticalSuffixIcon", ref directCriticalColor, ref directCriticalCustomColor, ref directCriticalUseCustomColor,
            ref directCriticalSuffix, ref directCriticalSuffixIcon, ref changed);

        ImGui.Separator();
        ImGui.Text("战斗开始倒计时");
        changed |= ImGui.Checkbox("到最后1秒时播报", ref countdownEnabled);
        changed |= DrawTextIconEditor("倒计时播报文本", "CountdownAnnouncementIcon", ref countdownText, ref countdownIcon);
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

    private void DrawHitSettings(
        string label,
        string iconKey,
        ref ushort paletteColor,
        ref uint customColor,
        ref bool useCustomColor,
        ref string suffix,
        ref int suffixIcon,
        ref bool changed)
    {
        ImGui.Separator();
        ImGui.Text(label);
        if (ImGui.Checkbox($"使用自定义颜色##useCustom-{label}", ref useCustomColor))
            changed = true;

        var color = UnpackRgba(customColor);
        if (ImGui.ColorEdit4($"文字颜色##color-{label}", ref color))
        {
            customColor = PackRgba(color);
            useCustomColor = true;
            changed = true;
        }

        if (!useCustomColor)
            ImGui.TextDisabled(paletteColor == 0 ? "当前使用游戏默认聊天颜色" : $"当前使用旧版游戏色号 {paletteColor}；启用上方色盘可选自定义色");

        changed |= DrawTextIconEditor($"{label}伤害末尾符号", iconKey, ref suffix, ref suffixIcon);
    }

    private bool DrawTextIconEditor(string label, string iconKey, ref string text, ref int iconId)
    {
        var changed = ImGui.InputText($"{label}##text-{label}", ref text, MaxInputLength);
        ImGui.SameLine();
        if (ImGui.Button($"选择图标##icon-{label}"))
        {
            iconPickerTarget = iconKey;
            iconBrowserOpen = true;
        }
        ImGui.SameLine();
        ImGui.Text(iconId > 0 ? $"图标 ID: {iconId}" : "无图标");
        if (iconId > 0 && TryGetIconTexture(iconId, out var texture))
        {
            ImGui.SameLine();
            ImGui.Image(texture.Handle, new Vector2(22, 22));
        }

        return changed;
    }

    private void DrawIconBrowser()
    {
        if (!iconBrowserOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(760, 560), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("图标浏览器", ref iconBrowserOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.SetNextItemWidth(210);
        ImGui.InputText("搜索图标 ID", ref iconSearch, 32);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(180);
        ImGui.SliderInt("图标大小", ref iconSize, 20, 64);
        ImGui.SameLine();
        if (ImGui.Button("清除图标"))
        {
            if (iconPickerTarget is not null)
                pendingIconSelection = (iconPickerTarget, 0);
            iconBrowserOpen = false;
        }

        if (ImGui.BeginTabBar("##CritChatIconCategories", ImGuiTabBarFlags.FittingPolicyScroll))
        {
            for (var i = 0; i < IconCategories.Length; i++)
            {
                if (ImGui.BeginTabItem($"{IconCategories[i].Name}##iconCategory{i}"))
                {
                    selectedIconCategory = i;
                    ImGui.EndTabItem();
                }
            }
            ImGui.EndTabBar();
        }

        if (iconSearch.Length == 0)
            ScanIconCategory(selectedIconCategory, 64);
        var icons = GetVisibleIcons(selectedIconCategory);
        ImGui.TextDisabled(iconSearch.Length == 0
            ? $"已找到 {iconCategoryResults[selectedIconCategory].Count} 个图标；列表会逐步扫描。点击图标即可选择。"
            : "搜索框输入完整图标 ID 后，即可查找游戏图标。");

        if (ImGui.BeginChild("##CritChatIconGrid", Vector2.Zero, true, ImGuiWindowFlags.None))
        {
            if (icons.Count == 0 && int.TryParse(iconSearch, out var searchId) && IsGameIconAvailable(searchId))
                icons.Add(searchId);

            var spacing = ImGui.GetStyle().ItemSpacing;
            var cell = iconSize + spacing.X;
            var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing.X) / cell));
            var rows = icons.Count == 0 ? 0 : (icons.Count + columns - 1) / columns;
            var clipper = ImGui.ImGuiListClipper();
            clipper.Begin(rows, iconSize + spacing.Y + ImGui.GetTextLineHeightWithSpacing());
            while (clipper.Step())
            {
                for (var row = Math.Max(0, clipper.DisplayStart); row < Math.Min(rows, clipper.DisplayEnd); row++)
                {
                    var start = row * columns;
                    var end = Math.Min(start + columns, icons.Count);
                    for (var n = start; n < end; n++)
                    {
                        var id = icons[n];
                        if (TryGetIconTexture(id, out var iconTexture))
                            ImGui.Image(iconTexture.Handle, new Vector2(iconSize));
                        else
                            ImGui.Button($"{id}##missing{id}", new Vector2(iconSize));

                        if (ImGui.IsItemClicked())
                        {
                            if (iconPickerTarget is not null)
                                pendingIconSelection = (iconPickerTarget, id);
                            iconBrowserOpen = false;
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"图标 ID: {id}");
                        if (n < end - 1)
                            ImGui.SameLine();
                    }
                }
            }
            clipper.End();
            clipper.Destroy();
        }
        ImGui.EndChild();
        ImGui.End();
    }

    private List<int> GetVisibleIcons(int category)
    {
        var icons = iconCategoryResults[category];
        if (string.IsNullOrWhiteSpace(iconSearch))
            return icons;
        if (int.TryParse(iconSearch, out var id))
            return icons.Where(icon => icon == id).ToList();
        return icons.Where(icon => icon.ToString().Contains(iconSearch, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    private void ScanIconCategory(int categoryIndex, int budget)
    {
        var category = IconCategories[categoryIndex];
        if (iconCategoryScanned[categoryIndex])
            return;
        var rangeIndex = iconScanRanges[categoryIndex];
        var nextIcon = nextIconsToScan[categoryIndex];
        var icons = iconCategoryResults[categoryIndex];
        var seen = iconCategorySets[categoryIndex];
        while (budget-- > 0 && rangeIndex < category.Ranges.Length)
        {
            var range = category.Ranges[rangeIndex];
            if (nextIcon >= range.End)
            {
                rangeIndex++;
                if (rangeIndex < category.Ranges.Length)
                    nextIcon = category.Ranges[rangeIndex].Start;
                continue;
            }

            var id = nextIcon++;
            if (IsGameIconAvailable(id) && seen.Add(id))
                icons.Add(id);
        }
        iconScanRanges[categoryIndex] = rangeIndex;
        nextIconsToScan[categoryIndex] = nextIcon;
        if (rangeIndex >= category.Ranges.Length)
            iconCategoryScanned[categoryIndex] = true;
    }

    private bool IsGameIconAvailable(int iconId)
    {
        if (iconId <= 0)
            return false;
        var folder = iconId / 1000;
        var basePath = $"ui/icon/{folder:D3}000/{iconId:D6}";
        return DataManager.FileExists($"{basePath}.tex") || DataManager.FileExists($"{basePath}_hr1.tex");
    }

    private bool TryGetIconTexture(int iconId, out IDalamudTextureWrap texture)
    {
        texture = null!;
        if (!iconTextures.TryGetValue(iconId, out var shared) &&
            !TextureProvider.TryGetFromGameIcon(new GameIconLookup((uint)iconId), out shared))
            return false;
        if (shared is null)
            return false;
        iconTextures[iconId] = shared;
        var wrap = shared.GetWrapOrDefault();
        if (wrap is null)
            return false;
        texture = wrap;
        return true;
    }

    private sealed record IconCategory(string Name, (int Start, int End)[] Ranges);

    private static readonly IconCategory[] IconCategories =
    [
        new("精选", [(1, 100), (62_000, 62_600), (66_000, 66_400)]),
        new("技能", [(100, 4_000), (5_100, 10_000), (19_600, 20_000)]),
        new("坐骑与宠物", [(4_000, 5_100), (59_000, 60_000), (68_000, 69_000)]),
        new("物品", [(20_000, 30_000), (50_000, 54_000), (58_000, 59_000)]),
        new("装备", [(30_000, 50_000), (54_000, 58_000), (200_000, 210_000)]),
        new("状态效果", [(210_000, 230_000)]),
        new("美术素材", [(130_000, 142_000), (250_000, 251_704)]),
        new("杂项", [(60_000, 62_000), (62_600, 68_000), (70_000, 85_000), (180_000, 181_500), (230_000, 250_000)])
    ];

    public void Dispose()
    {
        CommandManager.RemoveHandler(ToggleCommand);
        FlyTextGui.FlyTextCreated -= OnFlyTextCreated;
        Condition.ConditionChange -= OnConditionChange;
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= DrawSettings;
        PluginInterface.UiBuilder.Draw -= DrawIconBrowser;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        actionHook?.Dispose();
        countdownTimerHook?.Dispose();
    }
}
