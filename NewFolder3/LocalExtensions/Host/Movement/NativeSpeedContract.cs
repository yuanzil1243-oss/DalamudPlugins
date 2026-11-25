// SPDX-License-Identifier: GPL-3.0-only
using System.Buffers.Binary;
using System.Security.Cryptography;

namespace MoveSpeedLab;

/// <summary>
/// Verified against the local CN executable, SHA256 52042b60631cf2c48851b3aaee36e9ec4fedd6d7f2c68f0e9a8c3befb0af9f98.
/// No runtime RVA is stored. Both relative CALLs must resolve to the separately scanned target getter.
/// The entire bounded updater body must match before either hook is installed. Fail closed on updates/patches.
/// </summary>
internal static class NativeSpeedContract
{
    internal const string UpdateSignature = "40 53 48 83 EC 50 80 79 3C 00 48 8B D9 0F 84 ?? ?? ?? ?? 48 89 7C 24 68 48 8B 79 20 48 8B CF E8";
    internal const int UpdateLength = 567;
    internal const string UpdateSha256 = "b23d9f0702a2887978008835d3da253fe500bd6fa09c78181424b4b734c93801";

    internal static bool Matches(ReadOnlySpan<byte> body, long updater, long target) =>
        body.Length == UpdateLength && updater != 0 && target != 0 &&
        CallsTarget(body, 0x84, updater, target) && CallsTarget(body, 0xfc, updater, target) &&
        Convert.ToHexString(SHA256.HashData(body)).Equals(UpdateSha256, StringComparison.OrdinalIgnoreCase);

    internal static bool CallsTarget(ReadOnlySpan<byte> body, int offset, long updater, long target) =>
        offset >= 0 && offset <= body.Length - 5 && body[offset] == 0xe8 &&
        updater + offset + 5 + BinaryPrimitives.ReadInt32LittleEndian(body.Slice(offset + 1, 4)) == target;
}
