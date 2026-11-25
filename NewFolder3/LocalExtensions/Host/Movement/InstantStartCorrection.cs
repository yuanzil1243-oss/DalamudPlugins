// SPDX-License-Identifier: GPL-3.0-only
namespace MoveSpeedLab;

/// <summary>
/// Owns only the derived linear CurrentSpeed produced by one native speed update.
/// Does not change acceleration tables, input, rotation, coordinates, or the native target.
/// Caller serializes Apply/Restore and rechecks the lease/context immediately before Apply.
/// </summary>
internal sealed unsafe class InstantStartCorrection
{
    private nint address, parent;
    private float written;

    internal bool Owns(WalkController* controller) => (nint)controller == address && address != 0 &&
        WalkControllerValidation.IsValid(controller) && controller->Parent == parent &&
        BitConverter.SingleToInt32Bits(controller->CurrentSpeed) == BitConverter.SingleToInt32Bits(written);

    internal bool Apply(WalkController* controller, float nativeTarget, float returnedTarget)
    {
        // A positive result from this native update is required. Never turn a native zero into movement.
        if (!WalkControllerValidation.IsValid(controller) || controller->SpeedUpdateActive == 0 ||
            controller->InputLocked != 0 || controller->Jumping != 0 || controller->CurrentSpeed <= 0 ||
            !float.IsFinite(nativeTarget) || nativeTarget <= 0 ||
            !float.IsFinite(returnedTarget) || returnedTarget <= 0)
            return false;
        address = (nint)controller;
        parent = controller->Parent;
        written = returnedTarget;
        controller->CurrentSpeed = returnedTarget;
        return true;
    }

    internal void Restore(WalkController* controller, float nativeTarget)
    {
        // Re-read the unmodified native getter at release time, not a saved six/default/old scene value.
        // Do not increase speed on release, overwrite another writer, or resurrect an old controller.
        if (Owns(controller) && float.IsFinite(nativeTarget) && nativeTarget >= 0)
            controller->CurrentSpeed = Math.Min(controller->CurrentSpeed, nativeTarget);
        Forget();
    }

    internal void Forget() { address = parent = 0; written = 0; }
}
