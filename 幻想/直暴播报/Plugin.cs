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
    private bool iconBrowserOpen;
    private int iconSize = 36;
    private string iconSearch = string.Empty;
    private OutputPart? iconPickerTarget;
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
        EnsureTemplates();
        config.Version = 3;
        wasInCombat = Condition[ConditionFlag.InCombat];

        FlyTextGui.FlyTextCreated += OnFlyTextCreated;
        Condition.ConditionChange += OnConditionChange;
        Framework.Update += OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw += DrawSettings;
        PluginInterface.UiBuilder.Draw += DrawIconPicker;
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

    private void EnsureTemplates()
    {
        config.DirectHitTemplate ??= BuildDamageTemplate(config.DirectHitSuffix, config.DirectHitSuffixIcon);
        if (config.DirectHitTemplate.Count == 0)
            config.DirectHitTemplate = BuildDamageTemplate(config.DirectHitSuffix, config.DirectHitSuffixIcon);
        if (config.CriticalTemplate is null || config.CriticalTemplate.Count == 0)
            config.CriticalTemplate = BuildDamageTemplate(config.CriticalSuffix, config.CriticalSuffixIcon);
        if (config.DirectCriticalTemplate is null || config.DirectCriticalTemplate.Count == 0)
            config.DirectCriticalTemplate = BuildDamageTemplate(config.DirectCriticalSuffix, config.DirectCriticalSuffixIcon);
        if (config.CombatSeparatorTemplate is null || config.CombatSeparatorTemplate.Count == 0)
        {
            config.CombatSeparatorTemplate = new();
            if (!string.IsNullOrEmpty(config.CombatSeparator))
                config.CombatSeparatorTemplate.Add(new OutputPart { Kind = "Text", Value = config.CombatSeparator });
            AddLegacyIcon(config.CombatSeparatorTemplate, config.CombatSeparatorIcon);
        }
        if (config.CountdownTemplate is null || config.CountdownTemplate.Count == 0)
        {
            config.CountdownTemplate = new();
            if (!string.IsNullOrEmpty(config.CountdownAnnouncementText))
                config.CountdownTemplate.Add(new OutputPart { Kind = "Text", Value = config.CountdownAnnouncementText });
            AddLegacyIcon(config.CountdownTemplate, config.CountdownAnnouncementIcon);
        }
    }

    private List<OutputPart> BuildDamageTemplate(string suffix, int suffixIcon)
    {
        var parts = new List<OutputPart>
        {
            new() { Kind = "Placeholder", Value = "SkillName" },
            new() { Kind = "Text", Value = config.Delimiter },
        };
        AddLegacyIcon(parts, config.DelimiterIcon);
        parts.Add(new OutputPart { Kind = "Placeholder", Value = "HitType" });
        parts.Add(new OutputPart { Kind = "Text", Value = config.Delimiter });
        AddLegacyIcon(parts, config.DelimiterIcon);
        parts.Add(new OutputPart { Kind = "Placeholder", Value = "Damage" });
        if (!string.IsNullOrEmpty(suffix))
            parts.Add(new OutputPart { Kind = "Text", Value = suffix });
        AddLegacyIcon(parts, suffixIcon);
        return parts;
    }

    private static void AddLegacyIcon(List<OutputPart> parts, int iconId)
    {
        if (IsValidChatIcon(iconId))
            parts.Add(new OutputPart { Kind = "Icon", IconId = iconId });
    }

    private static bool IsValidChatIcon(int iconId) =>
        Enum.IsDefined(typeof(SeIconChar), (SeIconChar)iconId);

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
                PrintTemplate(config.CountdownTemplate);
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

        var (label, template, paletteColor, customColor, useCustomColor) = kind switch
        {
            FlyTextKind.DamageDh or FlyTextKind.AutoAttackOrDotDh =>
                ("直击", config.DirectHitTemplate, config.DirectHitColor, config.DirectHitCustomColor, config.DirectHitUseCustomColor),
            FlyTextKind.DamageCrit or FlyTextKind.AutoAttackOrDotCrit =>
                ("暴击", config.CriticalTemplate, config.CriticalColor, config.CriticalCustomColor, config.CriticalUseCustomColor),
            FlyTextKind.DamageCritDh or FlyTextKind.AutoAttackOrDotCritDh =>
                ("直暴", config.DirectCriticalTemplate, config.DirectCriticalColor, config.DirectCriticalCustomColor, config.DirectCriticalUseCustomColor),
            _ => (null, null, (ushort)0, 0u, false),
        };

        if (label is null || template is null)
            return;

        var skillName = text1.ToString().Trim();
        if (string.IsNullOrWhiteSpace(skillName))
            return;

        var builder = BeginMessage(paletteColor, customColor, useCustomColor);
        AppendTemplate(builder, template, skillName, label, val1);
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
        if (enabled && config.CombatSeparatorTemplate.Count > 0)
            PrintTemplate(config.CombatSeparatorTemplate);
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
            PrintPreview("直击", config.DirectHitTemplate, config.DirectHitColor, config.DirectHitCustomColor, config.DirectHitUseCustomColor);
            PrintPreview("暴击", config.CriticalTemplate, config.CriticalColor, config.CriticalCustomColor, config.CriticalUseCustomColor);
            PrintPreview("直暴", config.DirectCriticalTemplate, config.DirectCriticalColor, config.DirectCriticalCustomColor, config.DirectCriticalUseCustomColor);
            PrintTemplate(config.CountdownTemplate);
            return;
        }

        enabled = !enabled;
        ChatGui.Print($"直暴播报已{(enabled ? "开启" : "关闭")}。输入 /zhijibao config 可设置输出格式。");
    }

    private void PrintPreview(string label, List<OutputPart> template, ushort paletteColor, uint customColor, bool useCustomColor)
    {
        var builder = BeginMessage(paletteColor, customColor, useCustomColor);
        AppendTemplate(builder, template, "技能名称", label, 12345);
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

    private static void AppendTemplate(GameSeStringBuilder builder, IEnumerable<OutputPart> parts, string skillName, string hitType, int damage)
    {
        var nestedColor = false;
        foreach (var part in parts)
        {
            switch (part.Kind)
            {
                case "Text":
                    builder.Append(part.Value ?? string.Empty);
                    break;
                case "Icon":
                    if (IsValidChatIcon(part.IconId))
                        builder.Append(((SeIconChar)part.IconId).ToIconString());
                    break;
                case "Placeholder":
                    builder.Append(part.Value switch
                    {
                        "SkillName" => skillName,
                        "HitType" => hitType,
                        "Damage" => $"{damage:N0}",
                        _ => string.Empty,
                    });
                    break;
                case "Color":
                    if (nestedColor)
                        builder.PopColor();
                    builder.PushColorRgba(part.Color);
                    nestedColor = true;
                    break;
            }
        }
        if (nestedColor)
            builder.PopColor();
    }

    private void PrintTemplate(IEnumerable<OutputPart> parts)
    {
        var builder = new GameSeStringBuilder();
        AppendTemplate(builder, parts, string.Empty, string.Empty, 0);
        PrintBuiltMessage(builder, false);
    }

    private void PrintBuiltMessage(GameSeStringBuilder builder, bool hasColor)
    {
        if (hasColor)
            builder.PopColor();

        ChatGui.Print(SeString.Parse(builder.GetViewAsSpan()));
    }

    private void OpenSettings() => settingsOpen = true;

    private void DrawSettings()
    {
        if (!settingsOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(660, 720), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("直暴播报 - 文案编辑", ref settingsOpen))
        {
            ImGui.End();
            return;
        }

        var changed = false;
        var directHitColor = config.DirectHitColor;
        var directHitCustomColor = config.DirectHitCustomColor;
        var directHitUseCustomColor = config.DirectHitUseCustomColor;
        var criticalColor = config.CriticalColor;
        var criticalCustomColor = config.CriticalCustomColor;
        var criticalUseCustomColor = config.CriticalUseCustomColor;
        var directCriticalColor = config.DirectCriticalColor;
        var directCriticalCustomColor = config.DirectCriticalCustomColor;
        var directCriticalUseCustomColor = config.DirectCriticalUseCustomColor;
        var countdownEnabled = config.CountdownAnnouncementEnabled;
        DrawTemplateEditor("直击播报文案", config.DirectHitTemplate, ref changed, true);
        DrawOutputColor("直击文字颜色", ref directHitColor, ref directHitCustomColor, ref directHitUseCustomColor, ref changed);
        DrawTemplateEditor("暴击播报文案", config.CriticalTemplate, ref changed);
        DrawOutputColor("暴击文字颜色", ref criticalColor, ref criticalCustomColor, ref criticalUseCustomColor, ref changed);
        DrawTemplateEditor("直暴播报文案", config.DirectCriticalTemplate, ref changed);
        DrawOutputColor("直暴文字颜色", ref directCriticalColor, ref directCriticalCustomColor, ref directCriticalUseCustomColor, ref changed);

        ImGui.Separator();
        ImGui.TextUnformatted("战斗与倒计时文案");
        DrawTemplateEditor("战斗/死亡分隔文本", config.CombatSeparatorTemplate, ref changed);
        changed |= ImGui.Checkbox("到倒计时最后1秒时播报", ref countdownEnabled);
        DrawTemplateEditor("倒计时播报文本", config.CountdownTemplate, ref changed);
        if (countdownTimerHook is null)
            ImGui.TextDisabled("当前游戏版本未能识别倒计时信号，请查看 Dalamud 日志。");

        if (ImGui.Button("预览三种伤害文案"))
        {
            PrintPreview("直击", config.DirectHitTemplate, config.DirectHitColor, config.DirectHitCustomColor, config.DirectHitUseCustomColor);
            PrintPreview("暴击", config.CriticalTemplate, config.CriticalColor, config.CriticalCustomColor, config.CriticalUseCustomColor);
            PrintPreview("直暴", config.DirectCriticalTemplate, config.DirectCriticalColor, config.DirectCriticalCustomColor, config.DirectCriticalUseCustomColor);
        }
        ImGui.SameLine();
        ImGui.TextDisabled("占位符可插入技能名称、触发类型和伤害数值；聊天图标与文本可自由排序。");

        if (changed)
        {
            config.DirectHitColor = directHitColor;
            config.DirectHitCustomColor = directHitCustomColor;
            config.DirectHitUseCustomColor = directHitUseCustomColor;
            config.CriticalColor = criticalColor;
            config.CriticalCustomColor = criticalCustomColor;
            config.CriticalUseCustomColor = criticalUseCustomColor;
            config.DirectCriticalColor = directCriticalColor;
            config.DirectCriticalCustomColor = directCriticalCustomColor;
            config.DirectCriticalUseCustomColor = directCriticalUseCustomColor;
            config.CountdownAnnouncementEnabled = countdownEnabled;
            PluginInterface.SavePluginConfig(config);
        }

        ImGui.End();
    }

    private void DrawOutputColor(string label, ref ushort paletteColor, ref uint customColor, ref bool useCustomColor, ref bool changed)
    {
        ImGui.TextUnformatted(label);
        if (ImGui.Checkbox($"使用自定义颜色##{label}", ref useCustomColor))
            changed = true;

        var color = UnpackRgba(customColor);
        if (ImGui.ColorEdit4($"文字颜色##picker-{label}", ref color))
        {
            customColor = PackRgba(color);
            useCustomColor = true;
            changed = true;
        }
        if (!useCustomColor)
            ImGui.TextDisabled(paletteColor == 0 ? "使用游戏默认聊天颜色" : $"当前使用旧版游戏色号 {paletteColor}；勾选后可用色盘选色");
    }

    private void DrawTemplateEditor(string label, List<OutputPart> parts, ref bool changed, bool defaultOpen = false)
    {
        var flags = defaultOpen ? ImGuiTreeNodeFlags.DefaultOpen : ImGuiTreeNodeFlags.None;
        if (!ImGui.CollapsingHeader($"{label}##template-{label}", flags))
            return;

        var removeAt = -1;
        var moveFrom = -1;
        var moveTo = -1;
        if (ImGui.BeginTable($"##parts-{label}", 3, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("负荷", ImGuiTableColumnFlags.WidthFixed, 72);
            ImGui.TableSetupColumn("值");
            ImGui.TableSetupColumn("动作", ImGuiTableColumnFlags.WidthFixed, 104);
            ImGui.TableHeadersRow();

            for (var i = 0; i < parts.Count; i++)
            {
                var part = parts[i];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(part.Kind switch
                {
                    "Text" => "文本",
                    "Icon" => "图标",
                    "Placeholder" => "占位符",
                    "Color" => "颜色",
                    _ => part.Kind,
                });

                ImGui.TableNextColumn();
                switch (part.Kind)
                {
                    case "Text":
                    {
                        var value = part.Value;
                        if (ImGui.InputText($"##text-{label}-{i}", ref value, 128))
                        {
                            part.Value = value;
                            changed = true;
                        }
                        break;
                    }
                    case "Icon":
                    {
                        var glyph = IsValidChatIcon(part.IconId) ? ((SeIconChar)part.IconId).ToIconString() : "⊘";
                        if (ImGui.Button($"{glyph}  更改##chat-icon-{label}-{i}"))
                        {
                            iconPickerTarget = part;
                            iconBrowserOpen = true;
                        }
                        ImGui.SameLine();
                        ImGui.TextDisabled(IsValidChatIcon(part.IconId) ? $"{((SeIconChar)part.IconId)}" : "未选择");
                        break;
                    }
                    case "Placeholder":
                    {
                        var current = part.Value switch
                        {
                            "SkillName" => "技能名称",
                            "HitType" => "触发类型",
                            "Damage" => "伤害数值",
                            _ => "选择占位符",
                        };
                        if (ImGui.BeginCombo($"##placeholder-{label}-{i}", current))
                        {
                            foreach (var (value, text) in new[] { ("SkillName", "技能名称"), ("HitType", "触发类型"), ("Damage", "伤害数值") })
                            {
                                if (ImGui.Selectable($"{text}##placeholder-{label}-{i}-{value}", part.Value == value))
                                {
                                    part.Value = value;
                                    changed = true;
                                }
                            }
                            ImGui.EndCombo();
                        }
                        break;
                    }
                    case "Color":
                    {
                        var color = UnpackRgba(part.Color);
                        if (ImGui.ColorEdit4($"颜色##part-color-{label}-{i}", ref color))
                        {
                            part.Color = PackRgba(color);
                            changed = true;
                        }
                        break;
                    }
                }

                ImGui.TableNextColumn();
                if (ImGui.SmallButton($"↑##up-{label}-{i}") && i > 0)
                {
                    moveFrom = i;
                    moveTo = i - 1;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton($"↓##down-{label}-{i}") && i < parts.Count - 1)
                {
                    moveFrom = i;
                    moveTo = i + 1;
                }
                ImGui.SameLine();
                if (ImGui.SmallButton($"删##delete-{label}-{i}"))
                    removeAt = i;
            }
            ImGui.EndTable();
        }

        if (removeAt >= 0)
        {
            parts.RemoveAt(removeAt);
            changed = true;
        }
        else if (moveFrom >= 0)
        {
            var part = parts[moveFrom];
            parts.RemoveAt(moveFrom);
            parts.Insert(moveTo, part);
            changed = true;
        }

        if (ImGui.Button($"添加负荷##add-{label}"))
            ImGui.OpenPopup($"add-part-popup-{label}");
        if (ImGui.BeginPopup($"add-part-popup-{label}"))
        {
            if (ImGui.Selectable($"文本##add-text-{label}"))
            {
                parts.Add(new OutputPart { Kind = "Text" });
                changed = true;
            }
            if (ImGui.Selectable($"图标##add-icon-{label}"))
            {
                var part = new OutputPart { Kind = "Icon" };
                parts.Add(part);
                iconPickerTarget = part;
                iconBrowserOpen = true;
                changed = true;
            }
            if (ImGui.Selectable($"占位符##add-placeholder-{label}"))
            {
                parts.Add(new OutputPart { Kind = "Placeholder", Value = "SkillName" });
                changed = true;
            }
            if (ImGui.Selectable($"自定义颜色##add-color-{label}"))
            {
                parts.Add(new OutputPart { Kind = "Color", Color = 0xFFFFFFFF });
                changed = true;
            }
            ImGui.EndPopup();
        }
    }

    private void DrawIconPicker()
    {
        if (!iconBrowserOpen)
            return;

        ImGui.SetNextWindowSize(new Vector2(620, 520), ImGuiCond.FirstUseEver);
        if (!ImGui.Begin("聊天图标选择器", ref iconBrowserOpen))
        {
            ImGui.End();
            return;
        }

        ImGui.SetNextItemWidth(220);
        ImGui.InputText("搜索图标名称##chat-icon-search", ref iconSearch, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        ImGui.SliderInt("图标大小##chat-icon-size", ref iconSize, 18, 56);
        ImGui.SameLine();
        if (ImGui.Button("清除##clear-chat-icon"))
        {
            if (iconPickerTarget is not null)
            {
                iconPickerTarget.IconId = 0;
                PluginInterface.SavePluginConfig(config);
            }
            iconBrowserOpen = false;
        }

        ImGui.TextDisabled("这里只列出可以作为聊天字符写入消息的 Dalamud 图标。点击图标即可使用。");
        if (ImGui.BeginChild("##chat-icon-grid", Vector2.Zero, true, ImGuiWindowFlags.None))
        {
            var icons = Enum.GetValues<SeIconChar>()
                .Distinct()
                .Where(icon => string.IsNullOrWhiteSpace(iconSearch) ||
                               icon.ToString().Contains(iconSearch, StringComparison.OrdinalIgnoreCase))
                .OrderBy(icon => Convert.ToUInt32(icon))
                .ToArray();
            var spacing = ImGui.GetStyle().ItemSpacing;
            var columns = Math.Max(1, (int)((ImGui.GetContentRegionAvail().X + spacing.X) / (iconSize + spacing.X)));
            var rows = icons.Length == 0 ? 0 : (icons.Length + columns - 1) / columns;
            var clipper = ImGui.ImGuiListClipper();
            clipper.Begin(rows, iconSize + spacing.Y + ImGui.GetTextLineHeightWithSpacing());
            while (clipper.Step())
            {
                for (var row = Math.Max(0, clipper.DisplayStart); row < Math.Min(rows, clipper.DisplayEnd); row++)
                {
                    var start = row * columns;
                    var end = Math.Min(start + columns, icons.Length);
                    for (var i = start; i < end; i++)
                    {
                        var icon = icons[i];
                        if (ImGui.Button($"{icon.ToIconString()}##se-icon-{(int)icon}", new Vector2(iconSize + 8, iconSize + 8)))
                        {
                            if (iconPickerTarget is not null)
                            {
                                iconPickerTarget.IconId = (int)icon;
                                PluginInterface.SavePluginConfig(config);
                            }
                            iconBrowserOpen = false;
                        }
                        if (ImGui.IsItemHovered())
                            ImGui.SetTooltip($"{icon}\nU+{Convert.ToUInt32(icon):X4}");
                        if (i < end - 1)
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

    public void Dispose()
    {
        CommandManager.RemoveHandler(ToggleCommand);
        FlyTextGui.FlyTextCreated -= OnFlyTextCreated;
        Condition.ConditionChange -= OnConditionChange;
        Framework.Update -= OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= DrawSettings;
        PluginInterface.UiBuilder.Draw -= DrawIconPicker;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenSettings;
        actionHook?.Dispose();
        countdownTimerHook?.Dispose();
    }
}
