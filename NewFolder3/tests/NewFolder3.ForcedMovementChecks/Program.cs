// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Numerics;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text.Json;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using NativeConditions=FFXIVClientStructs.FFXIV.Client.Game.Conditions;
namespace ForcedMovementLab;
internal static class Program
{
    private static readonly List<string> passed=[];
    private static void Check(bool condition,string name){if(!condition)throw new Exception(name);passed.Add(name);}
    private static Scene Valid(long now)=>new(now,561,7,0x123456,0x2000,new(10,0,20),true,"valid");
    private static void Policy()
    {
        var gate=new MovementGate();const long now=1000;
        gate.Publish(Valid(now));gate.NativeReady(true);
        Check(!gate.Status(now).Active&&gate.Filter(0x2000,96,0,now)==0,"default-off");
        gate.Request(true);Check(gate.Status(now).Active,"one-switch-activates-no-direction-request");
        foreach(uint id in Enumerable.Range(0,1200).Select(i=>(uint)i))
            Check(gate.Filter(0x2000,id,0,now)==(MovementGate.PermissionIds.Contains(id)?1:0),"permission-scope-"+id);
        foreach(uint id in MovementGate.PermissionIds)
        {
            foreach(byte original in new byte[]{1,2,127,255})Check(gate.Filter(0x2000,id,original,now)==original,$"nonzero-preserved-{id}-{original}");
            Check(gate.Filter(0x2001,id,0,now)==0&&gate.Filter(0,id,0,now)==0,"foreign-instance-"+id);
        }
        Check(gate.Filter(0x2000,96,0,now+250)==1&&gate.Filter(0x2000,96,0,now+251)==0,"snapshot-expiry");
        Check(gate.Requested&&!gate.Status(now+251).Active,"expiry-pauses-retains-switch");
        gate.Publish(Valid(now+600000));Check(gate.Status(now+600000).Active,"no-five-minute-expiry");
        gate.Suspend("loading",now+600001);Check(gate.Requested&&!gate.Status(now+600001).Active,"zone-pauses-retains-switch");
        gate.Publish(Valid(now+600010) with{ConditionsAddress=0x3000,Territory=562});
        Check(gate.Status(now+600010).Active&&gate.Filter(0x2000,96,0,now+600010)==0&&gate.Filter(0x3000,96,0,now+600010)==1,"zone-resumes-new-instance-only");
        foreach(var invalid in new[]{Valid(now) with{Eligible=false},Valid(now) with{ActorId=0},Valid(now) with{ActorAddress=0},Valid(now) with{ConditionsAddress=0},Valid(now) with{Territory=0},Valid(now) with{Position=new(float.NaN,0,0)},Valid(now) with{At=now+1}})
        {gate.Publish(invalid);Check(!gate.Status(now).Active&&gate.Filter(invalid.ConditionsAddress,96,0,now)==0,"invalid-scene-"+passed.Count);}
        gate.Publish(Valid(now));gate.Request(false);Check(!gate.Requested&&gate.Filter(0x2000,96,0,now)==0,"off-immediate-policy-revocation");
        gate.Request(true);Check(!gate.Status(now).Active,"off-requires-native-ready-before-rearm");
        gate.NativeReady(true);gate.Fail("failure");Check(!gate.Requested&&!gate.Status(now).Active&&gate.Filter(0x2000,96,0,now)==0,"fault-revokes");
        gate.Request(true);gate.NativeReady(true);Check(gate.Status(now).Active&&gate.Status(now).Fault==null,"explicit-retry-clears-fault");
    }
    private static void Reader()
    {
        var host=new FakeHost();var reader=new GameSceneReader(host,host,host);
        Check(reader.Read(1000).Fresh(1000),"reader-valid");
        foreach(var flag in Enum.GetValues<ConditionFlag>().Except(new[]{ConditionFlag.SufferingStatusAffliction,ConditionFlag.SufferingStatusAffliction2,ConditionFlag.BoundByDuty,ConditionFlag.BoundByDuty56,ConditionFlag.BoundByDuty95}))
        {host.Flags.Add(flag);Check(!reader.Read(1000).Eligible,"reader-pause-"+flag);host.Flags.Clear();}
        host.Flags.UnionWith([ConditionFlag.SufferingStatusAffliction,ConditionFlag.SufferingStatusAffliction2]);host.LocalPlayer!.IsCasting=true;
        Check(reader.Read(1000).Eligible,"affliction-and-casting-do-not-disable-target-function");host.Flags.Clear();host.LocalPlayer.IsCasting=false;
        host.IsPvP=true;Check(!reader.Read(1000).Eligible,"pvp-pause");host.IsPvP=false;
        host.LocalPlayer.IsDead=true;Check(!reader.Read(1000).Eligible,"dead-pause");host.LocalPlayer.IsDead=false;
        host.IsLoggedIn=false;Check(!reader.Read(1000).Eligible,"logout-pause");host.IsLoggedIn=true;
        host.TerritoryType=0;Check(!reader.Read(1000).Eligible,"territory-zero");host.TerritoryType=561;
        host.LocalPlayer.Position=new(0,float.PositiveInfinity,0);Check(!reader.Read(1000).Eligible,"nonfinite-position");host.LocalPlayer=new();
        nint saved=NativeConditions.InstanceAddress;NativeConditions.InstanceAddress=0;Check(!reader.Read(1000).Eligible,"no-conditions");NativeConditions.InstanceAddress=saved;
        host.LocalPlayer=null;Check(!reader.Read(1000).Eligible,"no-player");
    }
    private static void Native()
    {
        var scanner=new FakeScanner();var ip=new FakeInterop();var gate=new MovementGate();using var backend=new NativeBackend(scanner,ip,gate,_=>true);
        Check(!backend.HookEnabled&&ip.Created==0,"default-no-hook-created");
        Check(backend.Prepare(out _)&&ip.Created==1,"one-permission-hook-only");
        var callback=Fixture.Callback(ip.Detour!);long now=Environment.TickCount64;
        gate.Publish(Valid(now));gate.Request(true);Fixture.OriginalValue=0;Fixture.OriginalCalls=0;
        Check(callback(0x2000,96,37,41)==1&&Fixture.OriginalCalls==1&&Fixture.Last==(0x2000,96u,37,41),"original-once-exclusions-preserved");
        Check(callback(0x2001,96,2,3)==0&&callback(0x2000,95,2,3)==0,"native-unrelated-preserved");
        Fixture.OriginalValue=255;Check(callback(0x2000,96,2,3)==255,"native-byte-nonzero-preserved");Fixture.OriginalValue=0;
        gate.Request(false);Check(callback(0x2000,96,2,3)==0,"off-before-unpatch-denies");backend.Disable();
        Check(!backend.HookEnabled&&backend.Prepare(out _)&&ip.Created==1,"rearm-reuses-hook");
        gate.Publish(Valid(Environment.TickCount64));gate.Request(true);
        Fixture.Entered=new(false);Fixture.Release=new(false);
        var inflight=Task.Run(()=>callback(0x2000,96,1,2));
        if(!Fixture.Entered.Wait(TimeSpan.FromSeconds(3)))throw new Exception("fixture callback did not enter");
        gate.Request(false);Fixture.Release.Set();Check(inflight.Result==0,"inflight-original-observes-off-before-filter");
        Fixture.Entered.Dispose();Fixture.Release.Dispose();Fixture.Entered=Fixture.Release=null;
        var metrics=backend.Snapshot();Check(metrics.Released==1&&metrics.Permissions.Single(m=>m.Id==96).Released==1,"actual-release-counter");
        backend.Dispose();Check(callback(0x2000,96,1,2)==0&&!backend.Prepare(out _),"dispose-preserves-inflight-original-no-rearm");
        foreach(string failure in new[]{"missing","ambiguous","bounds","fingerprint","sdk","hook"})
        {
            var sc=new FakeScanner();var interop=new FakeInterop();var g=new MovementGate();using var b=new NativeBackend(sc,interop,g,_=>true);
            nint sdk=NativeConditions.MemberFunctionPointers.HasPermission;byte first=Marshal.ReadByte(Fixture.Address);
            switch(failure){case "missing":sc.Missing=true;break;case "ambiguous":sc.Ambiguous=true;break;case "bounds":sc.ShortText=true;break;case "fingerprint":Marshal.WriteByte(Fixture.Address,0xE9);break;case "sdk":NativeConditions.MemberFunctionPointers.HasPermission=1;break;case "hook":interop.Fail=true;break;}
            Check(!b.Prepare(out string error)&&error.Length>0&&!g.Requested&&g.Status(Environment.TickCount64).Fault!=null&&!b.HookEnabled,"prepare-fail-closed-"+failure);
            Marshal.WriteByte(Fixture.Address,first);NativeConditions.MemberFunctionPointers.HasPermission=sdk;
        }
        // Probe is deliberately not called: the fake scan block is data, not executable code.
        // The real installation check covers native entry -> real detour dispatch separately.
    }
    private static object Artifact(string path)
    {
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path,"json")));
        var expectedVersion=Version.Parse(manifest.RootElement.GetProperty("AssemblyVersion").GetString()!);
        using var file=File.OpenRead(path);using var pe=new PEReader(file);var md=pe.GetMetadataReader();var asm=md.GetAssemblyDefinition();
        Check(md.GetString(asm.Name)=="NewFolder3"&&asm.Version==expectedVersion,"artifact-version");
        Check(pe.PEHeaders.CoffHeader.Machine==Machine.Amd64,"artifact-x64");
        string[] refs=md.AssemblyReferences.Select(h=>md.GetString(md.GetAssemblyReference(h).Name)).Order().ToArray();
        string[] host=["Dalamud","Dalamud.Bindings.ImGui","FFXIVClientStructs","InteropGenerator.Runtime","DeepDungeon.Fsd.Core","DeepDungeon.Fsd.Runtime","DeepDungeon.Fsd.Dalamud","DeepDungeon.MechanicRuntime","FFXIVClientStructs.Expansion","OmenTools","Lumina","Lumina.Excel","Newtonsoft.Json"];
        Check(refs.All(n=>n.StartsWith("System.")||host.Contains(n)),"artifact-only-system-and-host");
        Check(!refs.Contains("ForcedMovementLab")&&!refs.Contains("AntiDisplacementV2"),"artifact-no-experiment-plugin-dependencies");
        Check(!md.TypeDefinitions.Any(h=>md.GetString(md.GetTypeDefinition(h).Name).Contains("WalkCall")),"artifact-no-direction-output-hook");
        Check(manifest.RootElement.GetProperty("Name").GetString()=="NewFolder3"&&manifest.RootElement.GetProperty("DalamudApiLevel").GetInt32()==15,"artifact-manifest");
        Check(md.TypeDefinitions.Any(h=>md.GetString(md.GetTypeDefinition(h).Name)=="ForcedMovementController")&&
            md.TypeDefinitions.Any(h=>md.GetString(md.GetTypeDefinition(h).Name)=="AntiDisplacementController"),"artifact-both-integrated-controllers");
        return new{references=refs,otherPluginsRequired=false};
    }
    private static void HostPatch()
    {
        byte[] pristine=Enumerable.Range(1,16).Select(i=>(byte)i).ToArray();
        byte[] patched=(byte[])pristine.Clone();patched[0]=0xE9;
        Check(NativeEntryVerifier.KnownHostPatch(pristine,patched,pristine[..10]),"known-host-prefix-policy");
        byte[] corrupt=(byte[])patched.Clone();corrupt[15]^=1;
        Check(!NativeEntryVerifier.KnownHostPatch(pristine,corrupt,pristine[..10]),"host-tail-change-rejected");
        byte[] badPrefix=pristine[..10];badPrefix[0]^=1;
        Check(!NativeEntryVerifier.KnownHostPatch(pristine,patched,badPrefix),"host-original-prefix-change-rejected");
        Check(!NativeEntryVerifier.KnownHostPatch(pristine,patched,pristine[..4])&&!NativeEntryVerifier.KnownHostPatch(pristine,patched,pristine),"host-prefix-bounds");
        nint copy=Marshal.AllocHGlobal(16);Marshal.Copy(pristine,0,copy,16);
        var sc=new FakeScanner{CopyAddress=copy};var registry=Dalamud.Hooking.Internal.HookManager.Unhookers;
        try
        {
            Marshal.Copy(patched,0,Fixture.Address,16);
            bool Reject(){try{NativeEntryVerifier.Verify(sc,Fixture.Contract,Fixture.Address);return false;}catch(InvalidOperationException){return true;}}
            Check(Reject(),"unknown-patch-rejected-despite-good-copy");
            registry[Fixture.Address]=new Dalamud.Hooking.Internal.Unhooker(Fixture.Address,pristine[..10]);
            Check(NativeEntryVerifier.Verify(sc,Fixture.Contract,Fixture.Address).Contains("已核验"),"registered-host-patch-verified");
            Marshal.Copy(corrupt,0,Fixture.Address,16);Check(Reject(),"registered-patch-body-change-rejected");Marshal.Copy(patched,0,Fixture.Address,16);
            Marshal.WriteByte(copy,0xE8);Check(Reject(),"host-pristine-hash-change-rejected");Marshal.Copy(pristine,0,copy,16);
            registry[Fixture.Address]=new Dalamud.Hooking.Internal.Unhooker(Fixture.Address+1,pristine[..10]);Check(Reject(),"registry-address-mismatch-rejected");
            registry[Fixture.Address]=new Dalamud.Hooking.Internal.Unhooker(Fixture.Address,badPrefix);Check(Reject(),"registry-byte-mismatch-rejected");
            registry[Fixture.Address]=new Dalamud.Hooking.Internal.Unhooker(Fixture.Address,pristine[..10]){trimmed=false};Check(Reject(),"registry-untrimmed-rejected");
            registry[Fixture.Address]=new Dalamud.Hooking.Internal.Unhooker(Fixture.Address,pristine[..10]);
            using var backend=new NativeBackend(sc,new FakeInterop(),new MovementGate(),_=>true);
            Check(backend.Prepare(out _)&&backend.HookEnabled,"registered-host-patch-backend-rearm-compatible");
        }
        finally{registry.Clear();Marshal.Copy(pristine,0,Fixture.Address,16);Marshal.FreeHGlobal(copy);}
    }
    public static int Main(string[] args)
    {
        try
        {
            Fixture.Initialize();Policy();Reader();Native();HostPatch();HostIntegrationChecks.Run(Check);TriggerCounterIntegrationChecks.Run(Check);
            var known=new NativeDefinition("known","",3,"ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
            Check(known.Matches("abc"u8)&&!known.Matches("abd"u8)&&!known.Matches("ab"u8),"sha256-contract");
            Check(NativeBounds.Contains(100,50,149,1)&&!NativeBounds.Contains(100,50,149,2)&&!NativeBounds.Contains(0,50,1,1),"native-bounds");
            object artifact=Artifact(args[1]);
            File.WriteAllText(args[0],JsonSerializer.Serialize(new{at=DateTimeOffset.Now,passed=passed.Count,failed=0,scope="Production policy, reader and native callbacks with synthetic host; artifact metadata",gameplayEffectVerified=false,realNativeDispatchVerified=false,artifact,checks=passed},new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine($"PASS {passed.Count}; synthetic checks are not gameplay validation");return 0;
        }
        catch(Exception ex){Console.Error.WriteLine(ex);return 1;}
        finally{Fixture.Cleanup();}
    }
}
