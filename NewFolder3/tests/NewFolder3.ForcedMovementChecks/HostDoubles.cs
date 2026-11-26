// SPDX-License-Identifier: AGPL-3.0-or-later
// Test doubles only. Never connects to a game or executes the synthetic memory block.
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using ForcedMovementLab;

namespace Dalamud.Game.ClientState.Conditions
{
    public enum ConditionFlag
    {
        InDuelingArea,BetweenAreas,BetweenAreas51,WatchingCutscene,WatchingCutscene78,
        OccupiedInCutSceneEvent,LoggingOut,Unconscious,Mounted,RidingPillion,Mounting,
        UsingChocoboTaxi,InFlight,Swimming,Diving,Jumping,Jumping61,Crafting,Gathering,
        OccupiedInEvent,OccupiedInQuestEvent,SufferingStatusAffliction,SufferingStatusAffliction2,
        BoundByDuty,BoundByDuty56,BoundByDuty95,InDeepDungeon
    }
}
namespace Dalamud.Plugin.Services
{
    using Dalamud.Game.ClientState.Conditions;
    using Dalamud.Hooking;
    public interface ISigScanner
    {
        FakeModule Module{get;} long TextSectionOffset{get;} long TextSectionSize{get;} bool IsCopy{get;} nint SearchBase{get;}
        IEnumerable<nint> ScanAllText(string signature); nint ScanText(string signature);
    }
    public sealed record FakeModule(nint BaseAddress);
    public interface IGameInteropProvider {Hook<T> HookFromAddress<T>(nint address,T detour) where T:Delegate;}
    public interface IClientState {uint TerritoryType{get;} bool IsLoggedIn{get;} bool IsPvP{get;}}
    public interface IObjectTable {FakePlayer? LocalPlayer{get;}}
    public interface ICondition {bool this[ConditionFlag flag]{get;}}
    public sealed class FakePlayer
    {
        public ulong GameObjectId{get;set;}=7;public nint Address{get;set;}=0x123456;
        public Vector3 Position{get;set;}=new(10,0,20);
        public bool IsDead{get;set;}public bool IsCasting{get;set;}
    }
    public sealed class FakeHost:IClientState,IObjectTable,ICondition
    {
        public uint TerritoryType{get;set;}=561;public bool IsLoggedIn{get;set;}=true;public bool IsPvP{get;set;}
        public FakePlayer? LocalPlayer{get;set;}=new();public HashSet<ConditionFlag> Flags{get;}=[];
        public bool this[ConditionFlag flag]=>Flags.Contains(flag);
    }
}
namespace FFXIVClientStructs.FFXIV.Client.Game
{
    public unsafe struct Conditions
    {
        public static nint InstanceAddress=0x2000;
        public static Conditions* Instance()=>(Conditions*)InstanceAddress;
        public static class MemberFunctionPointers {public static nint HasPermission;}
    }
}
namespace Dalamud.Hooking
{
    public sealed class Hook<T>(T original,T detour):IDisposable where T:Delegate
    {
        public T OriginalDisposeSafe=>original;public T Detour=>detour;
        public bool IsEnabled{get;private set;}public bool IsDisposed{get;private set;}
        public void Enable(){if(IsDisposed)throw new ObjectDisposedException("fixture");IsEnabled=true;}
        public void Disable()=>IsEnabled=false;
        public void Dispose(){IsEnabled=false;IsDisposed=true;}
    }
}
namespace Dalamud.Hooking.Internal
{
    public static class HookManager {public static System.Collections.Hashtable Unhookers{get;}=new();}
    public sealed class Unhooker
    {
        internal nint address;internal bool trimmed=true;internal byte[] originalBytes;
        internal Unhooker(nint at,byte[] prefix){address=at;originalBytes=prefix;}
    }
}
namespace ForcedMovementLab
{
    internal static class NativeDefinitions {internal static NativeDefinition Permission=>Fixture.Contract;}
    internal sealed class FakeScanner:Dalamud.Plugin.Services.ISigScanner
    {
        internal bool Missing,Ambiguous,ShortText;internal nint CopyAddress;
        public Dalamud.Plugin.Services.FakeModule Module=>new(0x1000);
        public long TextSectionOffset=>(long)Fixture.Address-0x1000;
        public long TextSectionSize=>ShortText?15:16;
        public bool IsCopy=>CopyAddress!=0;
        public nint SearchBase=>CopyAddress==0?0:checked(CopyAddress-(nint)TextSectionOffset);
        public IEnumerable<nint> ScanAllText(string signature)=>Missing?[]:Ambiguous?[1,2]:[1];
        public nint ScanText(string signature)=>Fixture.Address;
    }
    internal sealed class FakeInterop:Dalamud.Plugin.Services.IGameInteropProvider
    {
        internal Delegate? Detour;internal int Created;internal bool Fail;
        public Dalamud.Hooking.Hook<T> HookFromAddress<T>(nint address,T detour) where T:Delegate
        {
            if(Fail)throw new InvalidOperationException("fixture creation failure");
            ++Created;Detour=detour;
            return new((T)Delegate.CreateDelegate(typeof(T),typeof(Fixture).GetMethod(nameof(Fixture.Original))!),detour);
        }
    }
    internal static class Fixture
    {
        internal static nint Address;internal static NativeDefinition Contract=null!;
        internal static int OriginalCalls;internal static byte OriginalValue;
        internal static (nint,uint,int,int) Last;
        internal static ManualResetEventSlim? Entered,Release;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate byte PermissionCall(nint self,uint id,int e1,int e2);
        internal static void Initialize()
        {
            Address=Marshal.AllocHGlobal(16);byte[] bytes=Enumerable.Range(1,16).Select(i=>(byte)i).ToArray();
            Marshal.Copy(bytes,0,Address,16);Contract=new("Permission","Permission",16,Convert.ToHexString(SHA256.HashData(bytes)));
            FFXIVClientStructs.FFXIV.Client.Game.Conditions.MemberFunctionPointers.HasPermission=Address;
        }
        public static byte Original(nint self,uint id,int e1,int e2)
        {
            Interlocked.Increment(ref OriginalCalls);Last=(self,id,e1,e2);
            Entered?.Set();Release?.Wait(TimeSpan.FromSeconds(5));return OriginalValue;
        }
        internal static PermissionCall Callback(Delegate d)=>(PermissionCall)Delegate.CreateDelegate(typeof(PermissionCall),d.Target,d.Method);
        internal static void Cleanup()=>Marshal.FreeHGlobal(Address);
    }
}
