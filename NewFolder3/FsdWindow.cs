using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;
using DeepDungeon.Fsd.Dalamud;

namespace NewFolder3;

internal sealed class FsdWindow : Window
{
    private readonly FsdApplication _application;
    private readonly YAxisAdjustController _yAxis;
    private readonly LocalSpeedController _localSpeed;
    private readonly AntiKickController _antiKick;
    private readonly AntiDisplacementController _antiDisplacement;
    private readonly ForcedMovementController _forcedMovement;

    public FsdWindow(FsdApplication application, YAxisAdjustController yAxis, LocalSpeedController localSpeed,
        AntiKickController antiKick, AntiDisplacementController antiDisplacement, ForcedMovementController forcedMovement)
        : base("你是壞孩子###NewFolder3")
    {
        _application = application ?? throw new ArgumentNullException(nameof(application));
        _yAxis = yAxis ?? throw new ArgumentNullException(nameof(yAxis));
        _localSpeed = localSpeed;
        _antiKick = antiKick;
        _antiDisplacement = antiDisplacement;
        _forcedMovement = forcedMovement;
    }

    private static void DrawUsageNotes()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.TextUnformatted("使用說明");
        foreach (string note in DeepDungeon.Fsd.Core.FeatureUsageNotes.Items)
            ImGui.TextWrapped(note);
    }

    public override void Draw()
    {
        if (!ImGui.BeginTabBar("##FsdHostTabs"))
            return;

        if (ImGui.BeginTabItem("說明"))
        {
            _application.DrawCompanionSettings();
            ImGui.Spacing();
            ImGui.Separator();
            ImGui.Spacing();
            ImGui.TextWrapped("它也許可以幫助你挖到一些寶藏並成為死靈術士");
            ImGui.TextWrapped("請啟用你所用輸出外掛的非戰鬥時主動攻擊能力.\n請關閉任意其他外掛的目標選擇.\n請關閉任意其他外掛的移動速度修正.");
            ImGui.TextWrapped("死靈術士需要強強PC, 請勿制限低幀");
            _localSpeed.DrawSettings();
            _antiKick.DrawSettings();
            _antiDisplacement.DrawSettings();
            _forcedMovement.DrawSettings();
            _application.DrawGcdProtectionSettings();
            // Keep this last: add future feature controls above the usage notes.
            DrawUsageNotes();
            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("FSD"))
        {
            _application.DrawDeepDungeonFormalPanel();
            ImGui.Separator();
            ImGui.Text("通用輔助");
            _application.DrawGeneralAssistantSettings();
            _yAxis.DrawSettings();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

}
