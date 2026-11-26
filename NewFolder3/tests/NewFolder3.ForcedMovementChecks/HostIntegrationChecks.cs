// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using NewFolder3;
using FFXIVClientStructs.FFXIV.Client.Game.Event;

namespace ForcedMovementLab
{
    internal static class HostIntegrationChecks
    {
        internal static unsafe void Run(Action<bool,string> check)
        {
            int thread = Environment.CurrentManagedThreadId;
            var scene = new Scene(1000,561,7,0x123456,0x2000,new(10,0,20),true,"ok")
                {Dungeon=1,Floor=1,Thread=thread};
            var groups = new List<(uint Dungeon,uint Territory,int Start)>();
            for(int i=0;i<5;i++) groups.Add((1,(uint)(561+i),1+i*10));
            for(int i=0;i<15;i++) groups.Add((1,(uint)(593+i),51+i*10));
            uint[] hoh = [770,771,772,782,773,783,774,784,775,785];
            for(int i=0;i<10;i++) groups.Add((2,hoh[i],1+i*10));
            for(int i=0;i<10;i++) groups.Add((3,(uint)(1099+i),1+i*10));
            for(int i=0;i<10;i++) groups.Add((4,(uint)(1281+i),1+i*10));
            foreach(var group in groups)
            {
                foreach(int floor in Enumerable.Range(group.Start,10))
                {
                    var current=scene with {Dungeon=group.Dungeon,Territory=group.Territory,Floor=floor};
                    check(ForcedMovementScope.Allows(current,1000)&&ForcedMovementScope.Matches(current,current,1000,thread),
                        $"host-scope-dungeon-{group.Dungeon}-floor-{floor}");
                }
                check(!ForcedMovementScope.Allows(scene with {Dungeon=group.Dungeon,Territory=group.Territory,Floor=group.Start-1},1000)&&
                    !ForcedMovementScope.Allows(scene with {Dungeon=group.Dungeon,Territory=group.Territory,Floor=group.Start+10},1000),
                    $"host-scope-range-edges-{group.Dungeon}-{group.Start}");
            }
            foreach(var other in new[]{scene with{Eligible=false}, scene with{Dungeon=0},scene with{Dungeon=5},
                scene with{Dungeon=2},scene with{Territory=1},scene with{Territory=0},scene with{Floor=0},
                scene with{Floor=11},scene with{ActorId=0},scene with{ActorAddress=0},scene with{ConditionsAddress=0},
                scene with{At=1001},scene with{At=749},scene with{Thread=0},scene with{Position=new(float.NaN,0,0)}})
                check(!ForcedMovementScope.Allows(other,1000),"host-invalid-scope-"+other);
            foreach(var other in new[]{scene with{ActorId=8},scene with{ActorAddress=22},scene with{ConditionsAddress=33},
                scene with{Territory=562,Floor=11},scene with{Floor=2},scene with{Dungeon=2,Territory=770},scene with{Thread=thread+1}})
                check(!ForcedMovementScope.Matches(scene,other,1000,thread),"host-live-identity-changed-"+other);
            check(ForcedMovementScope.Matches(scene,scene with {Position=new(12,0,23)},1000,thread),"host-normal-position-change-allowed");
            check(!ForcedMovementScope.Matches(scene,scene,1000,thread+1),"host-other-callback-thread-denied");

            var config=new Configuration();var host=new FakeHost();var ip=new FakeInterop();var log=new FakeLog();
            EventFramework.Allocate();
            try
            {
                using var controller=new ForcedMovementController(config,host,host,host,new FakeScanner(),ip,log);
                controller.Update();check(ip.Created==0,"controller-default-off-no-hook");
                config.ForcedMovementEnabled=true;controller.Update();check(ip.Created==0,"controller-outside-duty-no-hook");
                host.Flags.Add(ConditionFlag.BoundByDuty);controller.Update();
                check(ip.Created==1,"controller-one-hook-on-valid-deep-dungeon");
                var callback=Fixture.Callback(ip.Detour!);Fixture.OriginalValue=0;Fixture.OriginalCalls=0;
                check(callback(0x2000,96,77,88)==1&&Fixture.OriginalCalls==1&&Fixture.Last==(0x2000,96u,77,88),"controller-original-once-exclusions");
                EventFramework.Available=false;
                check(callback(0x2000,96,0,0)==0,"controller-live-no-native-dungeon-revokes-before-update");
                controller.Update();EventFramework.Available=true;controller.Update();
                check(ip.Created==1&&callback(0x2000,96,0,0)==1,"controller-scope-return-reuses-hook");
                host.TerritoryType=1;check(callback(0x2000,96,0,0)==0,"controller-live-other-duty-revokes-before-update");
                controller.Update();host.TerritoryType=561;controller.Update();
                host.Flags.Add(ConditionFlag.BetweenAreas);check(callback(0x2000,96,0,0)==0,"controller-live-loading-revokes-before-update");
                controller.Update();host.Flags.Remove(ConditionFlag.BetweenAreas);controller.Update();
                host.LocalPlayer!.IsDead=true;check(callback(0x2000,96,0,0)==0,"controller-live-death-revokes-before-update");
                controller.Update();host.LocalPlayer.IsDead=false;controller.Update();
                host.Flags.Add(ConditionFlag.SufferingStatusAffliction);host.Flags.Add(ConditionFlag.SufferingStatusAffliction2);
                check(callback(0x2000,96,0,0)==1,"controller-affliction-does-not-defeat-feature");host.Flags.Clear();host.Flags.Add(ConditionFlag.BoundByDuty);
                EventFramework.Dungeon->Floor=2;
                check(callback(0x2000,96,0,0)==0,"controller-floor-change-revokes-old-snapshot");
                controller.Update();check(callback(0x2000,96,0,0)==1,"controller-valid-next-floor-resumes");
                check(Task.Run(()=>callback(0x2000,96,0,0)).Result==0,"controller-other-thread-passes-original");
                host.LocalPlayer.GameObjectId=99;check(callback(0x2000,96,0,0)==0,"controller-live-actor-change-revokes");
                controller.Update();check(callback(0x2000,96,0,0)==1,"controller-actor-republication-resumes");
                // Knockback has an independent configuration: changing it cannot revoke movement.
                config.AntiDisplacementEnabled=false;controller.Update();check(callback(0x2000,96,0,0)==1,"controller-knockback-off-independent");
                config.AntiDisplacementEnabled=true;controller.Update();check(callback(0x2000,96,0,0)==1,"controller-knockback-on-independent");
                config.ForcedMovementEnabled=false;controller.Update();check(callback(0x2000,96,0,0)==0,"controller-off-restores-original");
                config.ForcedMovementEnabled=true;controller.Update();check(callback(0x2000,96,0,0)==1,"controller-explicit-on-rearms");
                controller.RevokeForHostError();check(callback(0x2000,96,0,0)==0,"controller-host-error-revokes-immediately");
                controller.Update();check(callback(0x2000,96,0,0)==0,"controller-fault-stays-off-until-toggle");
                config.ForcedMovementEnabled=false;controller.Update();config.ForcedMovementEnabled=true;controller.Update();
                check(callback(0x2000,96,0,0)==1,"controller-toggle-recovers-fault");
                controller.Dispose();check(callback(0x2000,96,0,0)==0,"controller-dispose-inflight-original-safe");
                config.ForcedMovementEnabled=true;controller.Update();check(callback(0x2000,96,0,0)==0&&ip.Created==1,"controller-no-rearm-after-dispose");
            }
            finally {EventFramework.Free();}
        }
    }
}

namespace NewFolder3
{
    // Only configuration/UI/logging are doubled; tests compile the actual production controller.
    internal sealed class Configuration
    {
        public bool ForcedMovementEnabled {get;set;}
        public bool AntiDisplacementEnabled {get;set;}
        public object Fsd {get;}=new();
        public void Save(object _) {}
    }
}
namespace Dalamud.Plugin.Services
{
    internal interface IPluginLog {void Info(string message);void Warning(string message);}
    internal sealed class FakeLog:IPluginLog {public void Info(string message){} public void Warning(string message){}}
}
namespace Dalamud.Bindings.ImGui
{
    internal static class ImGui
    {
        internal static bool Checkbox(string label,ref bool value)=>false;
        internal static bool IsItemHovered()=>false;
        internal static void SetTooltip(string value){}
        internal static void TextWrapped(string value){}
    }
}
namespace FFXIVClientStructs.FFXIV.Client.Game.Event
{
    internal unsafe struct FakeDungeon {internal uint DeepDungeonId;internal int Floor;}
    internal unsafe struct EventFramework
    {
        private static EventFramework* instance;
        internal static FakeDungeon* Dungeon;
        internal static bool Available;
        internal static EventFramework* Instance()=>Available?instance:null;
        internal FakeDungeon* GetInstanceContentDeepDungeon()=>Available?Dungeon:null;
        internal static void Allocate()
        {
            instance=(EventFramework*)Marshal.AllocHGlobal(sizeof(EventFramework));
            Dungeon=(FakeDungeon*)Marshal.AllocHGlobal(sizeof(FakeDungeon));
            *Dungeon=new(){DeepDungeonId=1,Floor=1};Available=true;
        }
        internal static void Free()
        {
            Available=false;Marshal.FreeHGlobal((nint)Dungeon);Marshal.FreeHGlobal((nint)instance);
            instance=null;Dungeon=null;
        }
    }
}
