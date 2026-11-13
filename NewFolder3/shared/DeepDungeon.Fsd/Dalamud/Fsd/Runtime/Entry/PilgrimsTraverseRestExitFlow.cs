using System;
using System.Numerics;
using global::Dalamud.Game.ClientState.Objects.Types;
using global::Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.GameState;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;
using DeepDungeon.Fsd.Core;
using global::Dalamud.Game.ClientState.Conditions;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry
{
	/// <summary>
	/// Handles the special PT rest room that appears after leaving duty by interacting with the exit event object.
	/// </summary>
	public sealed class PilgrimsTraverseRestExitFlow : IDisposable
	{
		private const uint RestExitEventObjDataId = 2014757;

		private RunContext? _ctx;
		private DateTime _nextTry = DateTime.MinValue;
		private string _lastStatus = string.Empty;
		private DateTime _lastLog = DateTime.MinValue;
		private bool _everSawExit;
		private bool _exitConfirmed;
		private bool _finished;
		private NavigationHelper? _navHelper;
		private DateTime _nextInteractTry = DateTime.MinValue;
		private readonly bool _requireValidatedConfirmation;
		private readonly PtRestExitConfirmationState _confirmation = new();
		private DateTime _flowStartedAtUtc;
		private DateTime _exitMissingSinceUtc = DateTime.MinValue;
		private const uint RestExitWarpRowId = 131612;

		public PilgrimsTraverseRestExitFlow(bool requireValidatedConfirmation = false)
		{
			_requireValidatedConfirmation = requireValidatedConfirmation;
		}

		public void Prepare(RunContext context)
		{
			_ctx = context;
			_nextTry = DateTime.MinValue;
			_lastStatus = "PT: rest exit ready";
			_lastLog = DateTime.MinValue;
			_everSawExit = false;
			_exitConfirmed = false;
			_finished = false;
			_navHelper = new NavigationHelper(context.Navigator);
			_nextInteractTry = DateTime.MinValue;
			_confirmation.Reset();
			_flowStartedAtUtc = DateTime.UtcNow;
			_exitMissingSinceUtc = DateTime.MinValue;
			SetStatus("PT: rest exit ready");
			RecordDiagnostic("rest-exit-prepared", new { validated = _requireValidatedConfirmation });
		}

		public bool IsPrepared => _ctx != null;

		public unsafe bool Update(IFramework framework)
		{
			if (_ctx == null || _finished)
				return true;

			if (DateTime.Now < _nextTry)
				return false;

			// Do not inherit a previous controller's dialog while the scene is zoning.
			if (_ctx.Duty.IsInDuty || _ctx.Duty.IsTransitioning || Service.LocalPlayer == null ||
			    Service.Condition[ConditionFlag.BetweenAreas] || Service.Condition[ConditionFlag.BetweenAreas51])
				return false;

			var nowUtc = DateTime.UtcNow;
			var hasYesno = DeepDungeonUi.TryGetSelectYesNo(out var yesno);

			if (!hasYesno &&
			    NpcInteractionGuard.FindByBaseId(DungeonCatalog.PilgrimsTraverse.NpcDataId) is { IsTargetable: true })
			{
				SetFinished("PT: rest exit complete at entry NPC");
				return true;
			}

			if (_confirmation.DepartureTimedOut(nowUtc))
				return StopWithError("PT rest exit timed out waiting for confirmed departure.");
			if (_confirmation.PromptClearTimedOut(nowUtc))
				return StopWithError("PT rest exit timed out waiting for the stale confirmation to close.");
			if ((nowUtc - _flowStartedAtUtc).TotalSeconds > 60)
				return StopWithError("PT rest exit timed out; inspect the current room and confirmation dialog.");

			if (hasYesno)
			{
				if (_requireValidatedConfirmation)
				{
					bool deletePrompt = DeepDungeonUi.IsDeleteSaveConfirmationPrompt(yesno, out _);
					bool promptReadable = DeepDungeonUi.TryGetConfirmationPromptText(yesno, out string actualPrompt, out string promptError);
					bool abandonPrompt = DeepDungeonUi.IsAbandonDutyConfirmationPrompt(yesno, out _);
					string expectedPrompt = ReadExpectedPrompt();
					var decision = _confirmation.DecidePrompt(
						deletePrompt, promptReadable, abandonPrompt, expectedPrompt, actualPrompt, nowUtc);
					if (decision is PtRestExitPromptAction.WaitForDeparture or
						PtRestExitPromptAction.WaitForPromptClear or PtRestExitPromptAction.WaitForText)
					{
						SetStatus($"PT: {decision}");
						_nextTry = DateTime.Now.AddMilliseconds(150);
						return false;
					}
					RecordDiagnostic("rest-exit-prompt-decision", new
					{
						decision = decision.ToString(), phase = _confirmation.Phase.ToString(),
						actualPrompt, expectedPrompt, promptReadable, promptError, deletePrompt, abandonPrompt,
						interactionAtUtc = _confirmation.InteractionAtUtc,
						exitObjectId = _confirmation.ExitObjectId, recoveryCount = _confirmation.RecoveryCount
					});
					if (decision == PtRestExitPromptAction.RecoverKnownPrompt)
					{
						// Cancel only a recognized old exit/abandon dialog. Never confirm
						// it or grant it ownership based on text alone.
						if (_confirmation.RecoveryCount >= PtRestExitConfirmationState.MaximumRecoveries)
							return StopWithError("PT rest exit stopped after three prompt recovery attempts.");
						if (DeepDungeonUi.TryCloseAddon("SelectYesno"))
							_confirmation.TryBeginRecovery(nowUtc, promptStillOpen: true);
						SetStatus("PT: clearing old exit confirmation before a fresh interaction");
						_nextTry = DateTime.Now.AddMilliseconds(250);
						return false;
					}
					if (decision != PtRestExitPromptAction.Confirm)
						return StopWithError($"Controlled PT rest exit stopped: {decision}; prompt={actualPrompt}; {promptError}");
				}
				else if (_confirmation.Phase == PtRestExitConfirmationPhase.AwaitingDeparture)
				{
					SetStatus("PT: waiting for confirmed rest exit");
					_nextTry = DateTime.Now.AddMilliseconds(150);
					return false;
				}

				SetStatus("PT: confirming rest exit");
				bool callbackAccepted = DeepDungeonUi.Fire(yesno, 0);
				RecordDiagnostic("rest-exit-confirmation-dispatched", new { callbackAccepted });
				if (_requireValidatedConfirmation || _everSawExit)
				{
					_exitConfirmed = _confirmation.RecordConfirmation(callbackAccepted, nowUtc);
					if (_exitConfirmed) _navHelper?.Cancel();
				}
				_nextTry = DateTime.Now.AddMilliseconds(350);
				return false;
			}

			if (_confirmation.Phase == PtRestExitConfirmationPhase.ClearingStalePrompt)
			{
				_confirmation.ObservePromptClosed();
				RecordDiagnostic("rest-exit-stale-prompt-cleared", new { recoveryCount = _confirmation.RecoveryCount });
				_nextTry = DateTime.Now.AddMilliseconds(250);
				return false;
			}

			var exitObj = FindExitObject();
			if (_confirmation.Phase == PtRestExitConfirmationPhase.AwaitingDeparture)
			{
				// A successful callback is not itself evidence that the room was left.
				// Wait for the entry NPC, or stable disappearance of the exit actor.
				if (exitObj == null)
				{
					if (_exitMissingSinceUtc == DateTime.MinValue) _exitMissingSinceUtc = nowUtc;
					if ((nowUtc - _exitMissingSinceUtc).TotalMilliseconds >= 500)
					{
						SetFinished("PT: rest room cleared");
						return true;
					}
				}
				else _exitMissingSinceUtc = DateTime.MinValue;
				SetStatus("PT: waiting for confirmed rest exit");
				_nextTry = DateTime.Now.AddMilliseconds(150);
				return false;
			}

			if (_confirmation.Phase == PtRestExitConfirmationPhase.AwaitingPrompt)
			{
				if (_confirmation.InteractionExpired(nowUtc))
				{
					if (!_confirmation.TryBeginRecovery(nowUtc, promptStillOpen: false))
						return StopWithError("PT rest exit stopped: no prompt after three fresh interactions.");
					RecordDiagnostic("rest-exit-interaction-retry", new { recoveryCount = _confirmation.RecoveryCount });
				}
				SetStatus("PT: waiting for rest exit prompt");
				_nextTry = DateTime.Now.AddMilliseconds(250);
				return false;
			}
			if (exitObj == null)
			{
				_navHelper?.Reset();

				if (_everSawExit && _exitConfirmed)
				{
					SetFinished("PT: rest room cleared");
					return true;
				}

				SetStatus(_everSawExit
					? "PT: waiting for rest exit confirmation"
					: "PT: waiting for rest exit actor");
				_nextTry = DateTime.Now.AddMilliseconds(250);
				return false;
			}

			_everSawExit = true;
			if (!exitObj.IsTargetable)
			{
				SetStatus("PT: waiting for the rest exit actor to become targetable");
				_nextTry = DateTime.Now.AddMilliseconds(250);
				return false;
			}
			var player = Service.LocalPlayer;
			if (player == null)
			{
				_nextTry = DateTime.Now.AddMilliseconds(250);
				return false;
			}

			var exitPos = exitObj.Position;
			var playerPos = player.Position;
			var navState = _navHelper?.Navigate(exitPos, playerPos, 1.2f) ?? NavigationState.Arrived;

			if (navState == NavigationState.Failed || navState == NavigationState.StuckGiveUp)
			{
				SetStatus("PT: rest exit navigation failed");
				_nextTry = DateTime.Now.AddMilliseconds(400);
				return false;
			}

			var distSq = Vector3.DistanceSquared(playerPos, exitPos);
			const float interactRange = 2.0f;
			if (distSq <= interactRange * interactRange && DateTime.Now >= _nextInteractTry)
			{
				if (TryInteract(exitObj))
				{
					_confirmation.RecordInteraction(nowUtc, exitObj.GameObjectId);
					RecordDiagnostic("rest-exit-interaction-dispatched", new { exitObjectId = exitObj.GameObjectId, exitObj.BaseId });
					SetStatus("PT: interacting with rest exit");
				}
				_nextInteractTry = DateTime.Now.AddMilliseconds(600);
			}
			_nextTry = DateTime.Now.AddMilliseconds(500);
			return false;
		}

		private unsafe bool TryInteract(IGameObject obj)
		{
			var ts = TargetSystem.Instance();
			if (ts == null)
				return false;

			var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)obj.Address;
			if (native == null)
				return false;

			if (ts->Target != native)
			{
				ts->Target = native;
			}

			ts->InteractWithObject(native);
			return true;
		}

		private IGameObject? FindExitObject()
		{
			foreach (var obj in Service.GameObjects)
			{
				if (obj == null)
					continue;

				if (obj.BaseId == RestExitEventObjDataId)
					return obj;
			}

			return null;
		}

		private void SetFinished(string status)
		{
			_finished = true;
			try { _navHelper?.Cancel(); } catch { }
			SetStatus(status);
			RecordDiagnostic("rest-exit-completed", new { status, recoveryCount = _confirmation.RecoveryCount });
		}

		private static string ReadExpectedPrompt()
		{
			try
			{
				return Service.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Warp>()?
					.GetRow(RestExitWarpRowId).Question.ExtractText() ?? string.Empty;
			}
			catch { return string.Empty; }
		}

		private bool StopWithError(string status)
		{
			if (_ctx != null)
			{
				_ctx.StatusLine = status;
				_ctx.StatusIsError = true;
			}
			try { _navHelper?.Cancel(); } catch { }
			RecordDiagnostic("rest-exit-error", new { status, phase = _confirmation.Phase.ToString() });
			return false;
		}

		private void RecordDiagnostic(string eventType, object details)
		{
			try { _ctx?.RecordDiagnostic?.Invoke(eventType, details); } catch { }
			try { Service.Log.Info($"[PTRestExit] {eventType}: {System.Text.Json.JsonSerializer.Serialize(details)}"); } catch { }
		}

		public void Dispose()
		{
			_finished = true;
			try { _navHelper?.Cancel(); } catch { }
			_navHelper = null;
			_ctx = null;
		}

		private void SetStatus(string status)
		{
			if (_ctx == null)
				return;

			_ctx.StatusLine = status;
			if (string.Equals(_lastStatus, status, StringComparison.Ordinal) && (DateTime.Now - _lastLog).TotalSeconds < 2)
				return;

			_lastStatus = status;
			_lastLog = DateTime.Now;
			try { Service.Log.Info($"[PTRestExit] {status}"); } catch { }
		}
	}
}
