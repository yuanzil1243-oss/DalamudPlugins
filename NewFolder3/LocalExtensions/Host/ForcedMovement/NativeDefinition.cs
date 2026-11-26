// SPDX-License-Identifier: AGPL-3.0-or-later
using System.Security.Cryptography;

namespace ForcedMovementLab;

internal sealed record NativeDefinition(string Name, string Signature, int Length, string Sha256)
{
    internal bool Matches(ReadOnlySpan<byte> bytes) => bytes.Length == Length &&
        Convert.ToHexString(SHA256.HashData(bytes)).Equals(Sha256, StringComparison.OrdinalIgnoreCase);
}

internal static class NativeBounds
{
    internal static bool Contains(nint start, long size, nint address, int length) =>
        start > 0 && address >= start && length > 0 && size >= length &&
        (ulong)(address - start) <= (ulong)(size - length);
}
