using System;
using System.Collections.Generic;
using System.Numerics;
using global::Dalamud.Plugin.Services;
using DeepDungeon.Fsd.Dalamud.GameState;
using DeepDungeon.Fsd.Core;
using DeepDungeon.Fsd.Dalamud.Runtime;
using DeepDungeon.Fsd.Dalamud.Runtime.Navigation;

namespace DeepDungeon.Fsd.Dalamud.Runtime.Entry
{
	/// <summary>
	/// Generic entry flow for Deep Dungeons: interact NPC, open menu, select empty save slot, choose floorset by text needle, confirm duty, wait inside.
	/// Mirrors robustness and throttling from PTEntryFlow.
	/// </summary>
	public sealed class GenericEntryFlow
	{
		private RunContext? _ctx;
		private readonly DungeonData _dungeon;
		private readonly int _startFloor;
		private readonly int _preferredSlotIndex;

		private enum Step
		{
			None,
			InteractNpc,
			OpenMenu,
			DetectEmptySlot,
			ChooseSlot,
			CreateSaveSelectString,
			ConfirmCreateYesNo,
			ClearIntermediates,
			SelectFloorset,
			ConfirmDuty,
			WaitInside
		}

		private Step _step = Step.None;
		private DateTime _nextTry = DateTime.MinValue;
		private string _lastStatus = string.Empty;
		private DateTime _lastLog = DateTime.MinValue;
		private DateTime _stepStart = DateTime.MinValue;
		private DateTime _lastMenuFire = DateTime.MinValue;
		private readonly Dictionary<string, DateTime> _cooldowns = new Dictionary<string, DateTime>();
		private readonly Dictionary<string, DateTime> _seenSince = new Dictionary<string, DateTime>();
		private DateTime _lastProgress = DateTime.MinValue;
		private DateTime _postInteractUntil = DateTime.MinValue;
		private DateTime _noWindowSince = DateTime.MinValue;
		private bool _hasEnteredMenu;
		private bool _hasClickedSlot;
		private bool _noEmptySlotError;
		private DateTime _confirmSaveCloseStartedAt = DateTime.MinValue;
		private DateTime _nextConfirmSaveCloseAt = DateTime.MinValue;
		private readonly DeepDungeonEntryUiCleanup _entryUiCleanup = new();
		private bool _awaitingFloorset;
		private string _lastSelectionOptions = string.Empty;
		private NavigationHelper? _npcNavHelper;

		private static readonly TimeSpan StabilityShort = TimeSpan.FromMilliseconds(150);
		private static readonly TimeSpan StabilityNormal = TimeSpan.FromMilliseconds(250);
		private static readonly TimeSpan CooldownShort = TimeSpan.FromMilliseconds(250);
		private static readonly TimeSpan CooldownNormal = TimeSpan.FromMilliseconds(400);
		private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(6);
		private static readonly TimeSpan PostInteractGrace = TimeSpan.FromMilliseconds(1800);
		private static readonly TimeSpan NoWindowReinteractDelay = TimeSpan.FromMilliseconds(2500);
		private static readonly TimeSpan UiCloseRetryInterval = TimeSpan.FromMilliseconds(300);
		private static readonly TimeSpan EntryUiCloseTimeout = TimeSpan.FromSeconds(3);

		public string CurrentStep => _step.ToString();

		public GenericEntryFlow(DungeonData dungeon, int startFloor, int preferredSlotIndex)
		{
			_dungeon = dungeon;
			_startFloor = startFloor;
			_preferredSlotIndex = preferredSlotIndex <= 0 ? 0 : 1;
		}

		public void Prepare(RunContext context)
		{
			_ctx = context;
			_step = Step.InteractNpc;
			_ctx.StatusLine = $"{_dungeon.Name}: starting entry";
			_stepStart = DateTime.Now;
			_cooldowns.Clear();
			_seenSince.Clear();
			_lastProgress = DateTime.Now;
			_nextTry = DateTime.MinValue;
			_postInteractUntil = DateTime.MinValue;
			_noWindowSince = DateTime.MinValue;
			_hasEnteredMenu = false;
			_hasClickedSlot = false;
			_noEmptySlotError = false;
			_confirmSaveCloseStartedAt = DateTime.MinValue;
			_nextConfirmSaveCloseAt = DateTime.MinValue;
			_entryUiCleanup.Reset();
			_awaitingFloorset = false;
			_lastSelectionOptions = string.Empty;
			_npcNavHelper = new NavigationHelper(context.Navigator);
		}

		public unsafe bool Update(IFramework framework)
		{
			if (_ctx == null) return false;
			
			// If we already detected no empty slots, close any open UI and fail
			if (_noEmptySlotError)
			{
				_npcNavHelper?.Cancel();
				GameState.DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData");
				GameState.DeepDungeonUi.TryCloseAddon("DeepDungeonMenu");
				return true; // Complete with error (StatusIsError was already set)
			}
			
			// If already inside the expected duty, we're done
			if (_ctx.Duty.IsInDuty && (_dungeon.DutyDungeonId == 0 || _ctx.Duty.DungeonId == _dungeon.DutyDungeonId))
			{
				SetStatus($"{_dungeon.Name}: Entered");
				return true;
			}

			if (DateTime.Now < _nextTry) return false;

			// Snapshot current UI presence
			var hasYesno = GameState.DeepDungeonUi.TryGetSelectYesNo(out var yesno);
			var hasConfirm = GameState.DeepDungeonUi.TryGetAddon("ContentsFinderConfirm", out var conf);
			var hasSelect = GameState.DeepDungeonUi.TryGetSelectString(out var sel);
			var hasSave = GameState.DeepDungeonUi.TryGetAddon("DeepDungeonSaveData", out var save);
			var hasMenu = GameState.DeepDungeonUi.TryGetAddon("DeepDungeonMenu", out var menu);
			var hasTalk = GameState.DeepDungeonUi.TryGetTalk(out var talk);

			MarkPresence("yesno", hasYesno);
			MarkPresence("confirm", hasConfirm);
			MarkPresence("select", hasSelect);
			MarkPresence("save", hasSave);
			MarkPresence("menu", hasMenu);
			MarkPresence("talk", hasTalk);
			var hasAnyUi = hasYesno || hasConfirm || hasSelect || hasSave || hasMenu || hasTalk;
			UpdateNoWindow(hasAnyUi);
			if (hasAnyUi)
				_npcNavHelper?.Cancel();

			// 1) Confirm Yes/No (0)
			if (hasYesno && SeenStable("yesno", StabilityShort) && !IsOnCooldown("yesno"))
			{
				SetStatus($"{_dungeon.Name}: confirming yes/no");
				GameState.DeepDungeonUi.Fire(yesno, 0);
				if (_hasClickedSlot) _awaitingFloorset = true;
				SetCooldown("yesno", CooldownNormal);
				_lastProgress = DateTime.Now;
				_nextTry = DateTime.Now.AddMilliseconds(300);
				_step = Step.ClearIntermediates;
				return false;
			}

			// 2) Confirm Duty (8)
			if (hasConfirm && SeenStable("confirm", StabilityShort) && !IsOnCooldown("confirm"))
			{
				if (hasSave)
				{
					SetStatus($"{_dungeon.Name}: closing save slot panel before Commence");
					RequestConfirmSaveClose();
					_nextTry = DateTime.Now.AddMilliseconds(150);
					return false;
				}
				
				SetStatus($"{_dungeon.Name}: clicking Commence button");
				GameState.DeepDungeonUi.ClickCommenceButton();
				_confirmSaveCloseStartedAt = DateTime.MinValue;
				_nextConfirmSaveCloseAt = DateTime.MinValue;
				SetCooldown("confirm", CooldownNormal);
				_lastProgress = DateTime.Now;
				_nextTry = DateTime.Now.AddMilliseconds(400);
				_step = Step.WaitInside;
				return false;
			}

			// 2.5) Advance Talk/EventTalk (0)
			if (hasTalk && SeenStable("talk", StabilityShort) && !IsOnCooldown("talk-advance"))
			{
				SetStatus($"{_dungeon.Name}: advancing dialog");
				GameState.DeepDungeonUi.Fire(talk, 0);
				SetCooldown("talk-advance", CooldownNormal);
				_lastProgress = DateTime.Now;
				_nextTry = DateTime.Now.AddMilliseconds(250);
				_step = Step.ClearIntermediates;
				return false;
			}

			// Recognized PT checkpoint menus never fall through to the generic option-zero path.
			if (hasSelect && SeenStable("select", StabilityNormal))
			{
				if (_dungeon.DutyDungeonId == 4)
				{
					if (!DeepDungeonUi.TryReadSelectStringOptions(out var options))
					{
						SetStatus($"{_dungeon.Name}: 等待讀取入口選項；尚未派發選擇。");
						return false;
					}
					string optionKey = string.Join(" | ", options);
					if (optionKey != _lastSelectionOptions)
					{
						_lastSelectionOptions = optionKey;
						RecordEntry("entry-selection-options", new { startFloor = _startFloor, options,
							stage = _step.ToString(), _hasClickedSlot, _awaitingFloorset });
					}
					var result = PilgrimsTraverseCheckpointPolicy.SelectOption(options, _startFloor, out int index);
					if (result == CheckpointMenuResult.NotFloorset && _awaitingFloorset)
					{
						_ctx.StatusIsError = true;
						SetStatus($"{_dungeon.Name}: 等待第 {_startFloor} 聖路入口時出現無法識別的選項；停止選擇。選項：{optionKey}");
						RecordEntry("entry-checkpoint-unrecognized", new { startFloor = _startFloor, options });
						_noEmptySlotError = true;
						return false;
					}
					if (result != CheckpointMenuResult.NotFloorset)
					{
						if (result != CheckpointMenuResult.Selected)
						{
							_ctx.StatusIsError = true;
							SetStatus($"{_dungeon.Name}: 無法唯一選擇第 {_startFloor} 層入口（可能尚未解鎖），停止入場。選項：{string.Join(" / ", options)}");
							_noEmptySlotError = true;
							return false;
						}
						if (IsOnCooldown("floorset")) return false;
						RecordEntry("entry-checkpoint-selected", new { startFloor = _startFloor, index, text = options[index], options });
						SelectFloorset(index);
						return false;
					}
				}
				else if (_dungeon.DutyDungeonId == 2)
                {
                    if (!DeepDungeonUi.TryReadSelectStringOptions(out var options)) return false;
                    var result = HohLocalPolicy.SelectStartOption(options, _startFloor, out int index);
                    if (result != CheckpointMenuResult.NotFloorset)
                    {
                        if (result != CheckpointMenuResult.Selected)
                        { _ctx.StatusIsError = true; _noEmptySlotError = true; SetStatus("天宮起始樓層尚未解鎖或不唯一；停止入場。"); return false; }
                        if (!IsOnCooldown("floorset"))
                        {
                            RecordEntry("hoh-entry-start-selected", new { startFloor = _startFloor, index, options });
                            SelectFloorset(index);
                        }
                        return false;
                    }
                    if (_awaitingFloorset)
                    {
                        _ctx.StatusIsError = true; _noEmptySlotError = true;
                        RecordEntry("hoh-entry-start-unrecognized", new { startFloor = _startFloor, options });
                        SetStatus("天宮起點選單無法識別；停止入場。" ); return false;
                    }
                }
			}

			// 4) Other SelectString �?advance/close with 0
			if (hasSelect && SeenStable("select", StabilityNormal) && !IsOnCooldown("select-advance"))
			{
				SetStatus($"{_dungeon.Name}: advancing non-floorset SelectString");
				GameState.DeepDungeonUi.Fire(sel, 0);
				SetCooldown("select-advance", CooldownNormal);
				_lastProgress = DateTime.Now;
				_nextTry = DateTime.Now.AddMilliseconds(300);
				_step = Step.ClearIntermediates;
				return false;
			}

			// 5) DeepDungeonSaveData -> choose preferred empty slot (via SaveSlotManager)
			// Guard: only click slot once, then wait for next UI step
			if (hasSave && SeenStable("save", StabilityNormal) && !IsOnCooldown("save-slot") && !_hasClickedSlot)
			{
				if (_ctx.SaveSlots.TrySelectPreferredEmpty(_preferredSlotIndex, out var chosen, out var saveError))
				{
					SetStatus($"{_dungeon.Name}: selected empty save slot {chosen + 1}");
					_hasClickedSlot = true;
					SetCooldown("save-slot", CooldownNormal);
					_lastProgress = DateTime.Now;
					_nextTry = DateTime.Now.AddMilliseconds(300);
					_step = Step.CreateSaveSelectString;
					return false;
				}
				else
				{
					if (_ctx != null) _ctx.StatusIsError = true;
					SetStatus($"{_dungeon.Name}: {saveError}");
					_noEmptySlotError = true;
					return false;
				}
			}

			// 6) DeepDungeonMenu -> Enter via Agent (robust - not affected by menu order)
			// Guard: only fire Enter once, don't re-enter if menu reappears during dialog flow
			if (hasMenu && SeenStable("menu", StabilityShort) && !IsOnCooldown("menu") && !_hasEnteredMenu)
			{
				if (DateTime.Now - _lastMenuFire >= TimeSpan.FromMilliseconds(400))
				{
					SetStatus($"{_dungeon.Name}: entering DeepDungeon via Agent");
					GameState.DeepDungeonUi.EnterDeepDungeonViaAgent();
					_hasEnteredMenu = true; // Mark that we've entered, don't re-enter
					_lastMenuFire = DateTime.Now;
					SetCooldown("menu", CooldownNormal);
					_lastProgress = DateTime.Now;
					_nextTry = DateTime.Now.AddMilliseconds(250);
					_step = Step.OpenMenu;
					return false;
				}
			}

			// Watchdog: re-interact with NPC if stale
			if (!IsOnCooldown("npc") && (DateTime.Now - _lastProgress) > WatchdogTimeout)
			{
				SetStatus($"{_dungeon.Name}: watchdog re-interact");
				InteractWithDungeonNpc();
				SetCooldown("npc", TimeSpan.FromMilliseconds(800));
				_lastProgress = DateTime.Now;
				_nextTry = DateTime.Now.AddMilliseconds(400);
				_step = Step.InteractNpc;
				return false;
			}

			// Light probe: attempt to open menu if nothing visible
			if (!hasMenu && !hasSave && !hasSelect && !hasConfirm && !hasYesno && !IsOnCooldown("npc-probe"))
			{
				var inGrace = DateTime.Now < _postInteractUntil;
				var noWindowLongEnough = _noWindowSince != DateTime.MinValue && (DateTime.Now - _noWindowSince) >= NoWindowReinteractDelay;
				if (!inGrace && noWindowLongEnough)
				{
					SetStatus($"{_dungeon.Name}: probing NPC for menu");
					InteractWithDungeonNpc();
					SetCooldown("npc-probe", TimeSpan.FromMilliseconds(900));
					_lastProgress = DateTime.Now;
					_nextTry = DateTime.Now.AddMilliseconds(350);
					_step = Step.InteractNpc;
					return false;
				}
			}

			_nextTry = DateTime.Now.AddMilliseconds(150);
			return false;

			void SelectFloorset(int index)
			{
				_awaitingFloorset = false;
				SetStatus($"{_dungeon.Name}: selecting exact floorset {_startFloor}");
				DeepDungeonUi.Fire(sel, index);
				SetCooldown("floorset", CooldownNormal);
				_lastProgress = DateTime.Now;
				_nextTry = DateTime.Now.AddMilliseconds(350);
				_step = Step.ConfirmDuty;
			}

			void RecordEntry(string eventType, object data)
			{
				try { _ctx?.RecordDiagnostic?.Invoke(eventType, data); } catch { }
			}

			void SetStatus(string s)
			{
				if (_ctx == null) return;
				_ctx.StatusLine = s;
				if (!string.Equals(_lastStatus, s, StringComparison.Ordinal) || (DateTime.Now - _lastLog).TotalSeconds >= 2)
				{
					_lastStatus = s;
					_lastLog = DateTime.Now;
					try { Service.Log.Info($"[GenericEntryFlow] {s}"); } catch { }
				}
			}
			void MarkPresence(string key, bool present)
			{
				if (present)
				{
					if (!_seenSince.ContainsKey(key)) _seenSince[key] = DateTime.Now;
				}
				else
				{
					if (_seenSince.ContainsKey(key)) _seenSince.Remove(key);
				}
			}
			bool SeenStable(string key, TimeSpan minStable)
			{
				if (_seenSince.TryGetValue(key, out var t)) return (DateTime.Now - t) >= minStable;
				return false;
			}
			bool IsOnCooldown(string key)
			{
				if (_cooldowns.TryGetValue(key, out var until)) return DateTime.Now < until;
				return false;
			}
			void SetCooldown(string key, TimeSpan duration)
			{
				_cooldowns[key] = DateTime.Now.Add(duration);
			}
			void UpdateNoWindow(bool hasAnyUiNow)
			{
				if (hasAnyUiNow)
				{
					_noWindowSince = DateTime.MinValue;
				}
				else
				{
					if (_noWindowSince == DateTime.MinValue) _noWindowSince = DateTime.Now;
				}
			}
			void RequestConfirmSaveClose()
			{
				var now = DateTime.Now;
				if (_confirmSaveCloseStartedAt == DateTime.MinValue)
					_confirmSaveCloseStartedAt = now;

				if (now >= _nextConfirmSaveCloseAt)
				{
					GameState.DeepDungeonUi.TryCloseAddon("DeepDungeonSaveData");
					_nextConfirmSaveCloseAt = now.Add(UiCloseRetryInterval);
				}

				_lastProgress = now;
				if (now - _confirmSaveCloseStartedAt <= EntryUiCloseTimeout)
					return;

				if (_ctx != null)
					_ctx.StatusIsError = true;
				SetStatus($"{_dungeon.Name}: failed to close save slot panel before Commence");
			}
			void InteractWithDungeonNpc()
			{
				if (_dungeon.NpcDataId == 0)
				{
					SetStatus($"{_dungeon.Name}: NPC id unset");
					return;
				}
				var npc = NpcInteractionGuard.FindByBaseId(_dungeon.NpcDataId);
				if (npc == null)
				{
					SetStatus($"{_dungeon.Name}: NPC not found, waiting for load...");
					return;
				}

				var player = Service.LocalPlayer;
				if (player == null)
				{
					SetStatus($"{_dungeon.Name}: waiting for player");
					return;
				}

				var distance = Vector3.Distance(player.Position, npc.Position);
				if (distance > NpcInteractionGuard.MaxInteractDistance)
				{
					if (!moveHelper.VNav.NavmeshReady())
                    {
                        SetStatus($"{_dungeon.Name}: waiting for entry navmesh");
                        return;
                    }
					var navState = _npcNavHelper?.Navigate(npc.Position, player.Position, NpcInteractionGuard.MaxInteractDistance - 0.4f) ?? NavigationState.Failed;
					SetStatus(navState is NavigationState.Failed or NavigationState.StuckGiveUp
						? $"{_dungeon.Name}: NPC navigation failed ({distance:F1}m)"
						: $"{_dungeon.Name}: moving to NPC ({distance:F1}m)");
					return;
				}

				_npcNavHelper?.Cancel();
				if (!NpcInteractionGuard.TryInteract(_dungeon.NpcDataId, _dungeon.Name, out var status))
				{
					SetStatus(status);
					return;
				}
				SetStatus(status);
				_postInteractUntil = DateTime.Now.Add(PostInteractGrace);
			}
		}

		public bool CleanupAfterDutyEntry()
		{
			if (_ctx == null) return true;
			var result = _entryUiCleanup.Update(out string status, out _);
			if (result != EntryUiCleanupResult.Ready) _ctx.StatusLine = status;
			if (result == EntryUiCleanupResult.Failed) _ctx.StatusIsError = true;
			return result != EntryUiCleanupResult.Waiting;
		}

		public void Reset()
		{
			try { _npcNavHelper?.Cancel(); } catch { }
		}
	}
}
