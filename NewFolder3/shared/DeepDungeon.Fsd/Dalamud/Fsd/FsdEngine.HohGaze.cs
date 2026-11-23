using System.Numerics;
using DeepDungeon.Fsd.Runtime;
using FFXIVClientStructs.FFXIV.Client.Game;
using OmenTools.Interop.Game;

namespace DeepDungeon.Fsd.Dalamud;

internal partial class FsdEngine
{
    private HohGazeMovementController? _hohGazeMovement;
    private MovementInputController? _hohGazeInput;

    private void UpdateHohGazeMovement(bool closed, Vector3 bossPosition)
    {
        if (!closed) { _hohGazeMovement?.Reset(); return; }
        // The shelved orbit experiment must never compete for this temporary BMR handoff.
        _hohBossOrbit?.Stop("眼神机制临时接管，绕圈已结束", notify: false);
        _hohGazeMovement ??= new(SetBossMovementOverride, point => {
            _ddHost?.Context?.Navigator.CancelAll();
            _hohGazeInput ??= new MovementInputController { IsAutoMove = false, Precision = .1f };
            _hohGazeInput.DesiredPosition = point;
            _hohGazeInput.Enabled = true;
        }, StopHohGazeInput, FaceHohGazeAway,
            p => _hohOrbitGeometry.GroundPoint(p, .15f, .25f), IsHohOrbitSegmentSafe,
            reason => {
                Service.Log.Info("[HOH50.GazeMovement] " + reason);
                _ddHost?.Context?.RecordDiagnostic?.Invoke("hoh50-gaze-movement", new { reason });
            });
        try { _hohGazeMovement.Update(_configuration.BossMechanics.UsesBmrBossHandling(),
            Service.LocalPlayer?.Position ?? default, bossPosition, DateTime.UtcNow); }
        catch (Exception error)
        {
            _hohGazeMovement.Reset();
            Service.Log.Error(error, "[HOH50.GazeMovement] Handoff failed; returned to BMR");
        }
    }

    private static unsafe void FaceHohGazeAway(Vector3 point)
    {
        var manager = ActionManager.Instance();
        if (manager != null) manager->AutoFaceTargetPosition(&point, 0);
    }
    private void StopHohGazeInput() { if (_hohGazeInput != null) _hohGazeInput.Enabled = false; }
    private void DisposeHohGazeMovement()
    {
        try { _hohGazeMovement?.Dispose(); }
        finally { _hohGazeInput?.Dispose(); _hohGazeInput = null; }
    }
}
