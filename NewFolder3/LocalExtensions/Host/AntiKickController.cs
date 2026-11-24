using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using OmenTools.Info.Game.Packets.Abstractions;
using OmenTools.Info.Game.Packets.Upstream;
using OmenTools.OmenService;

namespace NewFolder3;

/// <summary>
/// Sends an ordinary position-update packet only after the player has remained
/// still and out of combat in an active deep-dungeon run for several minutes.
/// </summary>
internal sealed class AntiKickController
{
    private static readonly long StationaryDelayMs = (long)TimeSpan.FromMinutes(4).TotalMilliseconds;
    private static readonly long RepeatDelayMs = (long)TimeSpan.FromMinutes(4).TotalMilliseconds;
    private readonly Configuration configuration;
    private readonly Func<bool> runActive;
    private readonly ICondition conditions;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;
    private Vector3 lastPosition;
    private long stationarySince;
    private long lastPacketAt;
    private bool hasPosition;

    public AntiKickController(Configuration configuration, Func<bool> runActive,
        ICondition conditions, IObjectTable objects, IPluginLog log)
    {
        this.configuration = configuration;
        this.runActive = runActive;
        this.conditions = conditions;
        this.objects = objects;
        this.log = log;
    }

    public void Update()
    {
        if (!configuration.AntiKickEnabled || !runActive() ||
            !conditions[ConditionFlag.InDeepDungeon] || conditions[ConditionFlag.InCombat] ||
            objects.LocalPlayer is not { } player)
        {
            Reset();
            return;
        }

        long now = Environment.TickCount64;
        Vector3 position = player.Position;
        if (!hasPosition || Vector3.DistanceSquared(position, lastPosition) >= 0.04f)
        {
            hasPosition = true;
            lastPosition = position;
            stationarySince = now;
            return;
        }

        if (now - stationarySince < StationaryDelayMs || now - lastPacketAt < RepeatDelayMs)
            return;

        int opcode = UpstreamOpcode.PositionUpdateInstanceOpcode;
        if (opcode <= 0)
        {
            log.Warning("[NewFolder3.AntiKick] Instance position-update opcode unavailable; no keepalive packet sent.");
            lastPacketAt = now;
            return;
        }

        try
        {
            var packet = new StationaryPositionUpdatePacket(opcode, player.Rotation, position);
            GamePacketManager.Instance().SendPackt(packet);
            lastPacketAt = now;
            log.Info("[NewFolder3.AntiKick] Sent stationary deep-dungeon position update.");
        }
        catch (Exception error)
        {
            lastPacketAt = now;
            log.Warning($"[NewFolder3.AntiKick] Keepalive packet failed: {error.Message}");
        }
    }

    public void DrawSettings()
    {
        bool enabled = configuration.AntiKickEnabled;
        if (ImGui.Checkbox("防止深宫无操作踢出##antiKick", ref enabled))
        {
            configuration.AntiKickEnabled = enabled;
            Reset();
            configuration.Save(configuration.Fsd);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("仅在 FSD 运行、位于深宫、非战斗且原地静止 4 分钟后发送一次当前位置包；不改变坐标。默认关闭。");
    }

    private void Reset()
    {
        hasPosition = false;
        stationarySince = 0;
        lastPacketAt = 0;
    }

    public void Dispose() => Reset();

    [StructLayout(LayoutKind.Explicit, Size = 68)]
    private struct StationaryPositionUpdatePacket : IUpstreamPacket
    {
        public StationaryPositionUpdatePacket(int opcode, float rotation, Vector3 position)
        {
            this = default;
            Opcode = opcode;
            Length = 56;
            Rotation = rotation;
            RotationNew = rotation;
            Move = 0;
            Position = position;
            PositionNew = position;
        }

        [FieldOffset(0)] public int Opcode;
        [FieldOffset(8)] public uint Length;
        [FieldOffset(32)] public float Rotation;
        [FieldOffset(36)] public float RotationNew;
        [FieldOffset(40)] public uint Move;
        [FieldOffset(44)] public Vector3 Position;
        [FieldOffset(56)] public Vector3 PositionNew;

        public string Log() => "Stationary deep-dungeon position update";
        public void Send() => GamePacketManager.Instance().SendPackt(this);
    }
}
