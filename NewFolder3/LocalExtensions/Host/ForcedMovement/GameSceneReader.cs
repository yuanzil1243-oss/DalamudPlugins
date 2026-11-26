// SPDX-License-Identifier: AGPL-3.0-or-later
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using NativeConditions=FFXIVClientStructs.FFXIV.Client.Game.Conditions;

namespace ForcedMovementLab;

internal sealed class GameSceneReader(IClientState client,IObjectTable objects,ICondition flags)
{
    internal unsafe Scene Read(long now)
    {
        var player=objects.LocalPlayer;
        uint territory=client.TerritoryType;
        if(!client.IsLoggedIn||player==null||territory==0)
            return Scene.Empty with {At=now,Reason="尚未登录／区域未就绪"};
        var scene=new Scene(now,territory,player.GameObjectId,player.Address,
            (nint)NativeConditions.Instance(),player.Position,false,"等待检查");
        Scene Block(string why)=>scene with {Reason=why};
        if(scene.ActorId==0||scene.ActorAddress==0||scene.ConditionsAddress==0||
            !float.IsFinite(scene.Position.X)||!float.IsFinite(scene.Position.Y)||!float.IsFinite(scene.Position.Z))
            return Block("角色／Conditions 结构无效");
        if(client.IsPvP||flags[ConditionFlag.InDuelingArea])return Block("PvP／决斗场景");
        if(flags[ConditionFlag.BetweenAreas]||flags[ConditionFlag.BetweenAreas51]||
            flags[ConditionFlag.WatchingCutscene]||flags[ConditionFlag.WatchingCutscene78]||
            flags[ConditionFlag.OccupiedInCutSceneEvent]||flags[ConditionFlag.LoggingOut])
            return Block("加载／过场／登出中；结束后自动恢复");
        if(player.IsDead||flags[ConditionFlag.Unconscious])return Block("角色死亡；复活后自动恢复");
        if(flags[ConditionFlag.Mounted]||flags[ConditionFlag.RidingPillion]||flags[ConditionFlag.Mounting]||
            flags[ConditionFlag.UsingChocoboTaxi]||flags[ConditionFlag.InFlight]||flags[ConditionFlag.Swimming]||
            flags[ConditionFlag.Diving]||flags[ConditionFlag.Jumping]||flags[ConditionFlag.Jumping61])
            return Block("当前不是落地步行；落地后自动恢复");
        if(flags[ConditionFlag.Crafting]||flags[ConditionFlag.Gathering]||
            flags[ConditionFlag.OccupiedInEvent]||flags[ConditionFlag.OccupiedInQuestEvent])
            return Block("生产采集／事件交互中");
        // Stun/bind/affliction flags and casting do not shut down the switch or this gate.
        return scene with {Eligible=true,Reason="本人落地场景有效"};
    }
}
