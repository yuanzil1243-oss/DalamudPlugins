// SPDX-License-Identifier: GPL-3.0-only
// Layout adapted from AtmoOmen/OmenTools (MIT); see THIRD-PARTY-NOTICES.
using System.Runtime.InteropServices;

namespace MoveSpeedLab;

[StructLayout(LayoutKind.Explicit, Size = 320)]
internal struct WalkController
{
    [FieldOffset(32)] public nint Parent;
    [FieldOffset(64)] public byte Jumping;
    [FieldOffset(68)] public float CurrentSpeed;
    [FieldOffset(88)] public float BaseSpeed;
    [FieldOffset(136)] public byte HasInput;
    [FieldOffset(292)] public byte InputLocked;
}

internal static unsafe class WalkControllerValidation
{
    // Parent is a backlink, not a promise that Walk is embedded at Parent + 16.
    // The real CN client has a static Walk and a separate heap Parent (2026-11-23).
    // Identity is checked independently against the unique Walk signature in the detour.
    internal static bool IsValid(WalkController* controller) => controller != null &&
        controller->Parent != 0 &&
        float.IsFinite(controller->CurrentSpeed) && controller->CurrentSpeed >= 0 &&
        float.IsFinite(controller->BaseSpeed) && controller->BaseSpeed > 0;
}
