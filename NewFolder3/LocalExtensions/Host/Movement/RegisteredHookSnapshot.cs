// SPDX-License-Identifier: GPL-3.0-only
using System.Collections;
using System.Reflection;

namespace MoveSpeedLab;

/// <summary>
/// Read-only adapter for the host's registered pre-hook bytes. No Unhook/Trim/registration
/// method is called. An unsupported host layout fails closed, never a guessed JMP allowance.
/// Verified against Dalamud 9455e31b528488a12fe98d21d75e0a9932dee0d6.
/// </summary>
internal static class RegisteredHookSnapshot
{
    internal static bool TryReadPrefix(Assembly hostAssembly, nint address, out byte[] prefix)
    {
        prefix = [];
        if (address == 0) return false;
        try
        {
            const BindingFlags statics = BindingFlags.Static | BindingFlags.NonPublic;
            const BindingFlags instance = BindingFlags.Instance | BindingFlags.NonPublic;
            var manager = hostAssembly.GetType("Dalamud.Hooking.Internal.HookManager", false);
            if (manager?.GetProperty("Unhookers", statics)?.GetValue(null) is not IDictionary entries ||
                entries[address] is not { } record) return false;
            var type = record.GetType();
            if (type.Assembly != hostAssembly || type.FullName != "Dalamud.Hooking.Internal.Unhooker" ||
                type.GetField("address", instance)?.GetValue(record) is not nint registeredAddress || registeredAddress != address ||
                type.GetField("trimmed", instance)?.GetValue(record) is not true ||
                type.GetField("originalBytes", instance)?.GetValue(record) is not byte[] bytes ||
                bytes.Length is < 5 or > 0x32) return false;
            prefix = (byte[])bytes.Clone();
            return true;
        }
        catch { return false; }
    }
}
