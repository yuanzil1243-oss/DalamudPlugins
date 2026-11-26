// SPDX-License-Identifier: AGPL-3.0-or-later
// Permission IDs referenced from XA-Slave (AGPL); Conditions ABI from host FFXIVClientStructs.
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using NativeConditions=FFXIVClientStructs.FFXIV.Client.Game.Conditions;

namespace ForcedMovementLab;

internal sealed record PermissionMetric(uint Id,long Calls,long Denied,long Released);
internal sealed record NativeSnapshot(bool HookEnabled,long AllCalls,long LocalCalls,long Released,
    string LastDenied,string LastReleased,string EntryVerification,PermissionMetric[] Permissions);

/// <summary>Intercepts the shared normal input permission path, without manufacturing movement.</summary>
internal sealed class NativeBackend(ISigScanner scanner,IGameInteropProvider interop,MovementGate gate,Func<Scene,bool> validateLive,Action<Scene,uint,byte,byte,long>? observePermission=null):IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate byte PermissionCall(nint self,uint id,int excluded1,int excluded2);
    private Hook<PermissionCall>? hook;
    private PermissionCall? entry;
    private nint address;
    private volatile bool disposed;
    private long allCalls,localCalls,released;
    private readonly long[] calls=new long[8],denied=new long[8],changes=new long[8];
    private string lastDenied="尚未观察到移动权限拒绝",lastReleased="尚未发生拒绝→允许";
    private string verification="尚未核验原生入口";
    [ThreadStatic]private static bool probing;
    [ThreadStatic]private static int probeCalls;
    [ThreadStatic]private static byte probeOriginal;
    internal bool HookEnabled=>!disposed&&hook?.IsEnabled==true;

    internal unsafe bool Prepare(out string error)
    {
        error="";
        if(disposed){error="模块已卸载";return false;}
        if(HookEnabled){gate.NativeReady(true);return true;}
        try
        {
            var definition=NativeDefinitions.Permission;
            if(scanner.ScanAllText(definition.Signature).Take(2).Count()!=1)
                throw new InvalidOperationException("权限入口签名缺失／不唯一");
            nint found=scanner.ScanText(definition.Signature);
            nint text=scanner.Module.BaseAddress+checked((nint)scanner.TextSectionOffset);
            if(!NativeBounds.Contains(text,scanner.TextSectionSize,found,definition.Length))
                throw new InvalidOperationException("权限入口不在有效 .text 边界内");
            verification=NativeEntryVerifier.Verify(scanner,definition,found);
            if(found!=(nint)NativeConditions.MemberFunctionPointers.HasPermission)
                throw new InvalidOperationException("SDK 权限入口与核验入口不一致");
            if(hook==null)
            {
                address=found;entry=Marshal.GetDelegateForFunctionPointer<PermissionCall>(found);
                hook=interop.HookFromAddress<PermissionCall>(found,Detour);
            }
            else if(found!=address)throw new InvalidOperationException("权限入口地址在会话间发生变化");
            hook.Enable();gate.NativeReady(true);return true;
        }
        catch(Exception ex)
        {
            error=ex.GetBaseException().Message;gate.Fail(error);
            try{hook?.Disable();}catch{/* The managed permission gate is already revoked. */}
            return false;
        }
    }

    private byte Detour(nint self,uint id,int excluded1,int excluded2)
    {
        byte original=hook!.OriginalDisposeSafe(self,id,excluded1,excluded2); // Exactly once, both exclusions preserved.
        if(probing){++probeCalls;probeOriginal=original;return gate.Filter(self,id,original,Environment.TickCount64,validateLive);}
        Interlocked.Increment(ref allCalls);
        try
        {
            byte result=gate.Filter(self,id,original,Environment.TickCount64,validateLive);
            int i=Array.IndexOf(MovementGate.PermissionIds,id);
            if(i>=0)
            {
                Interlocked.Increment(ref calls[i]);
                bool local=self==gate.Current.ConditionsAddress;
                if(local)
                {
                    Interlocked.Increment(ref localCalls);
                    try { observePermission?.Invoke(gate.Current,id,original,result,Environment.TickCount64); }
                    catch { /* Observer failure must not change the original/filter result. */ }
                }
                if(original==0)
                {
                    Interlocked.Increment(ref denied[i]);
                    Volatile.Write(ref lastDenied,$"权限 {id}；本人实例={local}；返回 {(result==0?"仍拒绝":"允许")}");
                }
                if(original!=result)
                {
                    Interlocked.Increment(ref changes[i]);Interlocked.Increment(ref released);
                    Volatile.Write(ref lastReleased,$"权限 {id}：拒绝→允许（正常移动链）");
                }
            }
            return result;
        }
        catch(Exception ex){gate.Fail("权限回调异常："+ex.GetType().Name);return original;}
    }

    // Read-only native permission queries, not key presses, motion requests or actor writes.
    // Install checks run synchronously on the framework thread and disable in the caller's finally.
    internal object Probe(nint self)
    {
        if(!HookEnabled||entry==null||self==0)throw new InvalidOperationException("Hook／现场未准备好");
        var rows=new List<object>();
        try
        {
            probing=true;
            foreach(uint id in MovementGate.PermissionIds)
            {
                probeCalls=0;byte result=entry(self,id,0,0);
                if(probeCalls!=1)throw new InvalidOperationException("真实权限调用未恰好进入一次本插件 Hook");
                rows.Add(new{id,original=probeOriginal,returned=result,detourCalls=probeCalls});
            }
            return new{dispatchVerified=true,queries=rows.ToArray(),movementGenerated=false};
        }
        finally{probing=false;probeCalls=0;}
    }
    internal NativeSnapshot Snapshot()=>new(HookEnabled,Interlocked.Read(ref allCalls),Interlocked.Read(ref localCalls),
        Interlocked.Read(ref released),Volatile.Read(ref lastDenied),Volatile.Read(ref lastReleased),Volatile.Read(ref verification),
        MovementGate.PermissionIds.Select((id,i)=>new PermissionMetric(id,Interlocked.Read(ref calls[i]),
            Interlocked.Read(ref denied[i]),Interlocked.Read(ref changes[i]))).ToArray());
    internal void Disable()
    {
        gate.NativeReady(false);hook?.Disable();
    }
    public void Dispose()
    {
        if(disposed)return;
        gate.Request(false);disposed=true;hook?.Dispose();
        // Reference retained for already-entered callbacks using OriginalDisposeSafe.
    }
}
