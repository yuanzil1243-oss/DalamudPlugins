// Test-only host stand-ins. The REAL NativeSpeedBackend.cs is linked into this executable.
// No Dalamud, process access, executable memory, hook installation, or game connection occurs.
namespace Dalamud.Hooking
{
    public sealed class Hook<T>(T original) : IDisposable where T : Delegate
    {
        public T Original { get; } = original;
        public bool IsEnabled { get; private set; }
        public bool Disposed { get; private set; }
        public void Enable() => IsEnabled = true;
        public void Disable() => IsEnabled = false;
        public void Dispose() { Disposed = true; IsEnabled = false; }
    }
}
namespace Dalamud.Plugin.Services
{
    public interface ISigScanner
    {
        IEnumerable<nint> ScanAllText(string signature);
        nint ScanText(string signature);
        nint GetStaticAddressFromSig(string signature);
    }
    public interface IGameInteropProvider
    {
        Dalamud.Hooking.Hook<T> HookFromAddress<T>(nint address, T detour) where T : Delegate;
    }
}
