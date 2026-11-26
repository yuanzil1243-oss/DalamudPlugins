// SPDX-License-Identifier: GPL-3.0-only
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace AntiDisplacementV2;

internal sealed class NativeMotion : IDisposable
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void NativeCall(nint actor, float angle, float distance, float duration, byte mode, ulong flags);
    private readonly Hook<NativeCall> hook;
    private readonly Func<Motion, Decision> decide;
    private readonly Action<Motion> forward;
    public string Verification { get; }
    public string Backend => hook.BackendName;
    public bool Enabled => !hook.IsDisposed && hook.IsEnabled;
    public nint Address { get; }

    public NativeMotion(ISigScanner scanner, IGameInteropProvider interop, Func<Motion, Decision> decide)
    {
        this.decide = decide;
        var matches = scanner.ScanAllText(NativeContract.BodySignature);
        if (matches.Length != 1) throw new InvalidOperationException($"位移函数体签名匹配数为{matches.Length}，要求恰好一个。");
        Address = matches[0] - NativeContract.BodyOffset;
        long rva = Address - scanner.Module.BaseAddress;
        if (rva < 0 || rva + NativeContract.Length > scanner.Module.ModuleMemorySize)
            throw new InvalidOperationException("位移入口不在游戏主模块内。");
        using var file = File.Open(scanner.Module.FileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var pe = new PEReader(file);
        var section = pe.PEHeaders.SectionHeaders.SingleOrDefault(s => s.Name == ".text");
        if (rva < section.VirtualAddress || rva + NativeContract.Length > section.VirtualAddress + section.VirtualSize)
            throw new InvalidOperationException("位移入口不在.text代码区。");
        byte[] disk = pe.GetSectionData((int)rva).GetContent(0, NativeContract.Length).ToArray();
        byte[] live = new byte[NativeContract.Length];
        Marshal.Copy(Address, live, 0, live.Length);
        Verification = NativeContract.Validate(disk, live);
        if (!disk.SequenceEqual(live)) CheckJumpStorage(live);
        hook = interop.HookFromAddress<NativeCall>(Address, Detour);
        if (hook.Address != Address)
        {
            hook.Dispose();
            throw new InvalidOperationException("宿主将Hook重定向到了其他地址，已撤销。");
        }
        forward = Forward;
    }

    // Before reading the jump table, validate the memory range and destination. No memory writes here.
    private static unsafe void CheckJumpStorage(byte[] bytes)
    {
        nint table = (nint)BitConverter.ToInt32(bytes, 3);
        if (!Readable(table, 8, false)) throw new InvalidOperationException("入口跳板指针表不可读。");
        nint target = Marshal.ReadIntPtr(table);
        if (target == 0 || !Readable(target, 1, true)) throw new InvalidOperationException("入口跳板目标不是可执行内存。");
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryInfo
    {
        public nint BaseAddress, AllocationBase;
        public uint AllocationProtect;
        public nuint RegionSize;
        public uint State, Protect, Type;
    }
    [DllImport("kernel32.dll")]
    private static extern nuint VirtualQuery(nint address, out MemoryInfo information, nuint length);
    private static bool Readable(nint address, int size, bool executable)
    {
        if (address == 0 || VirtualQuery(address, out var info, (nuint)Marshal.SizeOf<MemoryInfo>()) == 0 ||
            info.State != 0x1000 || (info.Protect & 0x101) != 0) return false;
        ulong offset = (ulong)(address - info.BaseAddress);
        if (offset > (ulong)info.RegionSize || (ulong)size > (ulong)info.RegionSize - offset) return false;
        uint protection = info.Protect & 0xFF;
        return executable ? protection is 0x10 or 0x20 or 0x40 or 0x80 : protection is 2 or 4 or 8 or 0x20 or 0x40 or 0x80;
    }
    private void Detour(nint actor, float angle, float distance, float duration, byte mode, ulong flags)
        => MotionDispatcher.Dispatch(new(actor, angle, distance, duration, mode, flags), decide, forward);
    private void Forward(Motion m) => hook.Original(m.Actor, m.Angle, m.Distance, m.Duration, m.NativeMode, m.Flags);
    public void Enable() => hook.Enable();
    public void Disable() { if (!hook.IsDisposed) hook.Disable(); }
    public void Dispose() { try { Disable(); } finally { hook.Dispose(); } }
}
