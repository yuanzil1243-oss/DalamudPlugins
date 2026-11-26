// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace ForcedMovementLab;

/// <summary>Verifies untouched code, or a bounded prefix registered by the Dalamud host itself.</summary>
internal static class NativeEntryVerifier
{
    internal static string Verify(ISigScanner scanner,NativeDefinition definition,nint found)
    {
        byte[] live=new byte[definition.Length];Marshal.Copy(found,live,0,live.Length);
        if(definition.Matches(live))return "实时原始入口指纹一致";
        if(!scanner.IsCopy)throw new InvalidOperationException("入口已改写，且没有宿主原始扫描副本");
        nint copy=checked(scanner.SearchBase+(found-scanner.Module.BaseAddress));
        nint copyText=checked(scanner.SearchBase+checked((nint)scanner.TextSectionOffset));
        if(!NativeBounds.Contains(copyText,scanner.TextSectionSize,copy,definition.Length))
            throw new InvalidOperationException("宿主扫描副本边界无效");
        byte[] pristine=new byte[definition.Length];Marshal.Copy(copy,pristine,0,pristine.Length);
        if(!definition.Matches(pristine))throw new InvalidOperationException("宿主原始副本指纹不符，客户端版本可能变化");

        // Read-only compatibility with the host's own hook registry. No external plugin lookup,
        // Unhook call, prefix write, or arbitrary trampoline following is performed.
        const BindingFlags stat=BindingFlags.Static|BindingFlags.Public|BindingFlags.NonPublic;
        const BindingFlags inst=BindingFlags.Instance|BindingFlags.Public|BindingFlags.NonPublic;
        Type? manager=typeof(Hook<>).Assembly.GetType("Dalamud.Hooking.Internal.HookManager",false);
        if(manager?.GetProperty("Unhookers",stat)?.GetValue(null) is not IDictionary registry||
            registry[found] is not{} record)
            throw new InvalidOperationException("入口实时指纹不符，宿主没有对应 Hook 记录");
        Type type=record.GetType();
        if(type.FullName!="Dalamud.Hooking.Internal.Unhooker"||
            type.GetField("address",inst)?.GetValue(record) is not nint savedAddress||savedAddress!=found||
            type.GetField("trimmed",inst)?.GetValue(record) is not true||
            type.GetField("originalBytes",inst)?.GetValue(record) is not byte[] prefix||
            !KnownHostPatch(pristine,live,prefix))
            throw new InvalidOperationException("宿主 Hook 原字节／长度／剩余正文核验失败");
        return $"宿主已登记跳板已核验（原始 {prefix.Length} 字节＋完整剩余正文）";
    }

    internal static bool KnownHostPatch(ReadOnlySpan<byte> pristine,ReadOnlySpan<byte> live,ReadOnlySpan<byte> prefix)
        => pristine.Length==live.Length&&prefix.Length is>=5 and<=32&&prefix.Length<pristine.Length&&
            prefix.SequenceEqual(pristine[..prefix.Length])&&live[prefix.Length..].SequenceEqual(pristine[prefix.Length..]);
}
