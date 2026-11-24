// SPDX-License-Identifier: GPL-3.0-only
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace MoveSpeedLab;

/// <summary>Reads current host structs. Does not use FSD or any other plugin.</summary>
internal sealed class GameContextReader(IClientState client, ICondition condition, IObjectTable objects)
{
    public unsafe SpeedContext Read()
    {
        uint territory = client.TerritoryType;
        bool inDuty = condition[ConditionFlag.BoundByDuty] || condition[ConditionFlag.BoundByDuty56] ||
            condition[ConditionFlag.BoundByDuty95];
        bool knownTerritory = DeepDungeonFloors.TryRange(territory, out uint expected, out int first, out int last);
        bool inDeepDungeon = knownTerritory || territory == 1333 || condition[ConditionFlag.InDeepDungeon];
        var identity = new FloorIdentity(territory, 0, 0);
        SpeedContext Block(string why) => new(identity, false, inDuty, inDeepDungeon, false, FloorKind.Unknown, why);
        var player = objects.LocalPlayer;
        if (!client.IsLoggedIn || territory == 0 || player == null) return Block("等待角色与区域就绪");
        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51] ||
            condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78] ||
            condition[ConditionFlag.OccupiedInCutSceneEvent]) return Block("过场／加载中：已撤销修正");
        bool canWalk = !player.IsDead && !player.IsCasting &&
            !condition[ConditionFlag.Unconscious] && !condition[ConditionFlag.Mounted] &&
            !condition[ConditionFlag.RidingPillion] && !condition[ConditionFlag.InFlight] &&
            !condition[ConditionFlag.Swimming] && !condition[ConditionFlag.Diving] &&
            !condition[ConditionFlag.Jumping] && !condition[ConditionFlag.Jumping61];
        if (!inDeepDungeon)
            return new(identity, true, inDuty, false, canWalk, FloorKind.Unknown,
                canWalk ? "副本外／非地宫" : "角色当前不能进行普通步行实验");
        if (!knownTerritory || !inDuty) return Block("地宫区域未确认或不在有效副本中");
        var events = EventFramework.Instance();
        var dungeon = events != null ? events->GetInstanceContentDeepDungeon() : null;
        if (dungeon == null) return Block("地宫原生控制器不可用");
        uint id = dungeon->DeepDungeonId;
        int floor = dungeon->Floor;
        if (id != expected || floor < first || floor > last)
            return Block("地宫原生楼层与当前区域不一致：已撤销修正");
        identity = new(territory, id, floor);
        FloorKind kind = DeepDungeonFloors.Classify(id, floor);
        return new(identity, kind != FloorKind.Unknown, true, true, canWalk, kind,
            canWalk ? "地宫楼层已识别" : "角色当前不能进行普通步行实验");
    }
}
