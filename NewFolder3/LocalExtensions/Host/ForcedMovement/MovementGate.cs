// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;

namespace ForcedMovementLab;

internal sealed record Scene(long At, uint Territory, ulong ActorId, nint ActorAddress,
    nint ConditionsAddress, Vector3 Position, bool Eligible, string Reason)
{
    // Host scope is separate from the reusable eight-permission policy.
    internal uint Dungeon { get; init; }
    internal int Floor { get; init; }
    internal int Thread { get; init; }
    internal static readonly Scene Empty = new(0,0,0,0,0,default,false,"等待角色现场");
    internal bool Fresh(long now) => Eligible && now >= At && now-At <= MovementGate.SceneLifetimeMs &&
        Territory != 0 && ActorId != 0 && ActorAddress != 0 && ConditionsAddress != 0 &&
        float.IsFinite(Position.X) && float.IsFinite(Position.Y) && float.IsFinite(Position.Z);
}
internal sealed record GateStatus(bool Requested, bool Active, bool NativeReady, string State, string? Fault);

/// <summary>Normal movement permission policy. It never generates a direction or moves the actor.</summary>
internal sealed class MovementGate
{
    internal const int SceneLifetimeMs=250;
    internal static readonly uint[] PermissionIds=[96,97,98,99,1001,1006,1007,1008];
    internal readonly object Sync=new();
    private Scene scene=Scene.Empty;
    private bool requested, nativeReady;
    private string? fault;
    internal Scene Current { get { lock(Sync)return scene; } }
    internal bool Requested { get { lock(Sync)return requested; } }
    internal void Request(bool value)
    {
        lock(Sync)
        {
            requested=value;
            if(value)fault=null;
            if(!value)nativeReady=false;
        }
    }
    internal void NativeReady(bool value) { lock(Sync)nativeReady=value; }
    internal void Publish(Scene value) { lock(Sync)scene=value; }
    internal void Suspend(string reason,long now)
    { lock(Sync)scene=Scene.Empty with {At=now,Reason=reason}; }
    internal void Fail(string reason)
    { lock(Sync){requested=false;nativeReady=false;fault=reason;} }
    internal GateStatus Status(long now)
    {
        lock(Sync)
        {
            bool active=requested&&nativeReady&&fault==null&&scene.Fresh(now);
            string state=fault!=null?"启用失败："+fault:!requested?"已关闭：使用游戏原始移动限制":
                !scene.Fresh(now)?"主开关已开，暂时暂停："+scene.Reason:
                !nativeReady?"主开关已开，正在接入原生移动权限入口":
                "已开启：普通移动权限入口已接入，直接按正常方式移动";
            return new(requested,active,nativeReady,state,fault);
        }
    }
    internal byte Filter(nint self,uint id,byte original,long now,Func<Scene,bool>? validateLive=null)
    {
        if(original!=0 || self==0 || Array.IndexOf(PermissionIds,id)<0)return original;
        lock(Sync)
            return requested&&nativeReady&&fault==null&&scene.Fresh(now)&&self==scene.ConditionsAddress&&
                (validateLive==null||validateLive(scene))?(byte)1:original;
    }
}
