// SPDX-License-Identifier: GPL-3.0-only
using System.Security.Cryptography;
namespace AntiDisplacementV2;

public static class NativeContract
{
    // The first eleven bytes may be a host-managed Reloaded jump. Resolve from the untouched body.
    public const string BodySignature = "0F 29 70 E8 0F 28 C1 44 0F 29 50 A8 0F 28 F1 0F 29 78 D8 44 0F 28 D3";
    public const int BodyOffset = 11;
    public const int Length = 327;
    public const string Sha256 = "6738b4dc5dd61545cbf839a27012e4d78094107b11d55715bff185e75b30386b";
    public static bool DiskMatches(ReadOnlySpan<byte> disk) => disk.Length == Length &&
        Convert.ToHexString(SHA256.HashData(disk)).Equals(Sha256, StringComparison.OrdinalIgnoreCase);

    public static string Validate(ReadOnlySpan<byte> disk, ReadOnlySpan<byte> live)
    {
        if (!DiskMatches(disk)) throw new InvalidOperationException("磁盘原生函数不属于已核验版本。");
        if (live.Length != Length) throw new InvalidOperationException("原生函数读取不完整。");
        if (disk.SequenceEqual(live)) return "原始入口";
        if (!disk[BodyOffset..].SequenceEqual(live[BodyOffset..]))
            throw new InvalidOperationException("入口之外的原生函数内容有变化，停止安装。");
        // Reloaded's seven-byte absolute indirect jump plus NOP alignment to the original instruction boundary.
        if (live[0] == 0xFF && live[1] == 0x24 && live[2] == 0x25 &&
            live[7] == 0x90 && live[8] == 0x90 && live[9] == 0x90 && live[10] == 0x90)
            return "已识别宿主跳板（独立Hook共存）";
        throw new InvalidOperationException("发现未知入口改写，停止安装；不会接管未知跳转。");
    }
}
