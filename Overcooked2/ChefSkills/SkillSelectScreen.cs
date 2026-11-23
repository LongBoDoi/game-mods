using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ChefSkills
{
    /// <summary>
    /// The skill-select step of the level intro. Each local player gets a card listing every
    /// skill: UP/DOWN picks a skill, LEFT lowers it (freeing a point), RIGHT raises it (spending
    /// one, or going over), SELECT picks a special or a key, LOCK IN (X on a pad, Enter on the
    /// keyboard) locks the build in only when it adds up to exactly zero, BACK
    /// unlocks it. So every locked-in build adds up to zero. Online, every other player gets a
    /// card too, showing their build and whether they've locked in (see <see cref="SkillSync"/>). There is no
    /// time limit: the screen closes once every player has locked in, or when a player holds the
    /// game's Skip button for a second, the way a cutscene is skipped (the host's skip ends it for
    /// everyone).
    ///
    /// The screen is not a BepInEx/IMGUI overlay: <see cref="SkillSelectView"/> builds it from the
    /// game's own UI art and fonts (see that class).
    /// </summary>
    internal static class SkillSelectScreen
    {
        private const float ArtWaitSeconds = 5f;
        private const float ArtPollSeconds = 0.25f;
        private const float ReadyHoldSeconds = 0.5f;

        // Skipping uses the game's own Skip button, held like skipping a cutscene.
        private const float SkipHoldSeconds = 1f;

        private static SkillSelectView _view;
        private static SkillSync _sync;
        private static List<Slot> _slots;
        private static bool _open;

        internal static bool IsOpen
        {
            get { return _open; }
        }

        /// <summary>Runs the skill-select screen, then the game's own intro (Ready... Go!).</summary>
        internal static IEnumerator Wrap(IEnumerator original)
        {
            Plugin.Skills.Clear();
            LevelState.Reset();
            SkillSync.NewLevel();
            DashSync.Reset();
            SpecialRuntime.NewLevel();

            if (Plugin.Enabled.Value)
            {
                // Freeze the kitchen while players choose. The game's intro pauses the same layer
                // with its own key, so the two pauses don't interfere.
                TimeManager time = GameUtils.RequestManager<TimeManager>();
                object pauseKey = new object();
                if (time != null) time.SetPaused(TimeManager.PauseLayer.Main, true, pauseKey);

                IEnumerator screen = Run();
                while (true)
                {
                    bool more;
                    try
                    {
                        more = screen.MoveNext();
                    }
                    catch (Exception error)
                    {
                        Plugin.Log.LogError("Màn chọn kỹ năng bị lỗi, bắt đầu màn với kỹ năng mặc định: " + error);
                        Plugin.Skills.Clear();
                        Close();
                        more = false;
                    }
                    if (!more) break;
                    yield return screen.Current;
                }

                // Loadouts are set: the specials start working with the level.
                try
                {
                    SpecialRuntime.StartLevel();
                }
                catch (Exception error)
                {
                    Plugin.Log.LogError("Không bật được kỹ năng đặc biệt cho màn này: " + error);
                }

                // Let the game's intro take its own pause before releasing ours, so the kitchen
                // never runs for a frame in between.
                bool hasMore;
                try
                {
                    hasMore = original.MoveNext();
                }
                finally
                {
                    if (time != null) time.SetPaused(TimeManager.PauseLayer.Main, false, pauseKey);
                }
                if (!hasMore) yield break;
                yield return original.Current;
            }

            while (original.MoveNext())
                yield return original.Current;
        }

        private sealed class Slot
        {
            public PlayerInputLookup.Player Player;
            // Which player on this PC this is (0 = first, in player order): picks their DISPLAY setting.
            public int Rank;
            public string Title;
            public ChefColourData Colour;
            public int ColourIndex;
            public uint[] Chefs;
            public int[] Levels;
            public int[] Specials;
            public int Selected;
            public bool Ready;
            public float DeniedAt = -10f;
            public string DeniedReason;
            public ILogicalButton Up;
            public ILogicalButton Down;
            public ILogicalButton Left;
            public ILogicalButton Right;
            public ILogicalButton Select;
            public ILogicalButton Cancel;
            public ILogicalButton Skip;
            public ILogicalButton Hide;
            // Locks the build in (X on a pad, Enter on the keyboard): separate from SELECT, which
            // only picks, so locking in never opens a picker or starts setting a key by accident.
            public ILogicalButton LockIn;
            public bool SkipArmed;
            public float SkipHeld;
            // Waiting for this player to press a new skill key (the KEY row), and what came of the last try.
            public SpecialInput.Capture Capture;
            // The special picker, while it is open for this player.
            public SkillPicker Picker;
            public string Notice;
            public float NoticeAt = -10f;

            public int FreePoints
            {
                get { return SkillStats.FreePoints(Levels, Specials); }
            }
        }

        /// <summary>
        /// How far (0..1) the longest current hold of the Skip button is. A hold only counts once
        /// the button has been seen released, so one held through the loading screen doesn't skip.
        /// </summary>
        private static float SkipProgress(List<Slot> slots)
        {
            float progress = 0f;
            foreach (Slot slot in slots)
            {
                if (!slot.Skip.IsDown())
                {
                    slot.SkipArmed = true;
                    slot.SkipHeld = 0f;
                    continue;
                }
                if (!slot.SkipArmed) continue;
                slot.SkipHeld += Time.unscaledDeltaTime;
                progress = Mathf.Max(progress, slot.SkipHeld / SkipHoldSeconds);
            }
            return progress;
        }

        private static IEnumerator Run()
        {
            // Online, everyone must have Chef Skills, or the level is played the normal way on
            // every machine: no screen, no skills.
            List<string> missing = SkillSync.PlayersWithoutMod();
            if (missing.Count > 0)
            {
                Plugin.Log.LogInfo("Không phải ai trong phòng cũng cài Chef Skills (thiếu: " +
                    string.Join(", ", missing.ToArray()) + "); màn này chơi bình thường, không dùng kỹ năng.");
                SkillSync.IgnoreLevel();
                yield break;
            }

            Dictionary<PlayerInputLookup.Player, List<uint>> chefs = FindLocalPlayers();
            if (chefs.Count == 0)
            {
                Plugin.Log.LogWarning("Không tìm thấy người chơi nào trên máy này; bỏ qua màn chọn kỹ năng.");
                yield break;
            }
            var players = new List<PlayerInputLookup.Player>(chefs.Keys);
            players.Sort();

            // The dialog art comes from the in-game menu scene, which the kitchen loads
            // additively; give it a moment. The view still works (plainer) without it.
            GameArt art = GameArt.Load();
            float waited = 0f;
            float sinceLoad = 0f;
            while (!art.HasDialogArt && waited < ArtWaitSeconds)
            {
                yield return null;
                waited += Time.unscaledDeltaTime;
                sinceLoad += Time.unscaledDeltaTime;
                if (sinceLoad >= ArtPollSeconds)
                {
                    art = GameArt.Load();
                    sinceLoad = 0f;
                }
            }
            if (!art.HasDialogArt)
                Plugin.Log.LogWarning("Chưa tải được hình giao diện của game; dùng màn chọn kỹ năng đơn giản.");

            // Online, the other machines' players get cards too, and nobody starts until everyone
            // is locked in (see SkillSync). Offline this is null and only local players take part.
            _sync = SkillSync.Begin();
            _view = SkillSelectView.Build(art, players[0],
                _sync != null && _sync.IsHost ? Lang.T("HOLD: SKIP FOR ALL", "GIỮ: BỎ QUA CHO CẢ PHÒNG") : Lang.T("HOLD: SKIP", "GIỮ: BỎ QUA"));
            _open = true;

            var slots = new List<Slot>();
            foreach (PlayerInputLookup.Player player in players)
            {
                ChefColourData colour = GameArt.ColourOf(player);
                var slot = new Slot
                {
                    Player = player,
                    Title = _sync != null && players.Count == 1 ? Lang.T("YOU", "BẠN") : Lang.T("PLAYER ", "NGƯỜI CHƠI ") + ((int)player + 1),
                    Colour = colour,
                    ColourIndex = GameArt.ColourIndex(colour),
                    Chefs = chefs[player].ToArray(),
                    Levels = Plugin.Skills.LastPicked(player),
                    Specials = Plugin.Skills.LastPickedSpecials(player),
                    Up = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UIUp, player),
                    Down = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UIDown, player),
                    Left = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UILeft, player),
                    Right = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UIRight, player),
                    Select = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UISelectNotStart, player),
                    Cancel = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UICancel, player),
                    Skip = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UISkip, player),
                    Hide = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UIChangeMode, player),
                    // Each player's own: X on a whole controller, Enter on the keyboard, and a free
                    // button on half of a shared controller, never the same as their SELECT.
                    LockIn = SpecialInput.LockInButton(player),
                    Rank = slots.Count,
                };
                Plugin.Log.LogInfo("Người chơi " + ((int)player + 1) + ": " + SpecialInput.Describe(player));
                slot.Hide.ClaimPressEvent();
                // Ignore presses left over from the loading screen.
                slot.Up.ClaimPressEvent();
                slot.Down.ClaimPressEvent();
                slot.Left.ClaimPressEvent();
                slot.Right.ClaimPressEvent();
                slot.Select.ClaimPressEvent();
                slot.Cancel.ClaimPressEvent();
                slot.LockIn.ClaimPressEvent();
                slots.Add(slot);
            }
            _slots = slots;

            Plugin.Log.LogInfo(string.Format("Mở màn chọn kỹ năng cho {0} người chơi trên máy này{1}.",
                slots.Count, _sync != null ? " (chơi trực tuyến: chờ mọi người trong phòng)" : ""));

            // The other machines' cards as last read (for "one per kitchen" specials).
            List<SkillSync.RemoteCard> lastRemote = null;

            // No time limit: the screen stays until every player has locked in (or someone holds Skip).
            while (true)
            {
                float skipProgress = SkipProgress(slots);
                _view.ShowSkipProgress(skipProgress);
                if (skipProgress >= 1f)
                {
                    foreach (Slot slot in slots) NoChanges(slot);
                    if (_sync != null) _sync.Finish(Entries(slots, true), true);
                    Plugin.Log.LogInfo(_sync != null && _sync.IsHost
                        ? "Chủ phòng đã bỏ qua màn chọn kỹ năng cho cả phòng."
                        : "Đã bỏ qua màn chọn kỹ năng.");
                    // Skipping only clears this level's skills, not the build offered next time.
                    Close();
                    yield break;
                }
                if (!_view.IsAlive) break;

                // Once the host's level has started, a locked build here can't be unlocked again.
                bool canUnlock = _sync == null || !_sync.HostStarted;
                if (HandleHide(slots))
                {
                    // Hidden to look at the kitchen: nothing on the cards changes meanwhile.
                    foreach (Slot slot in slots) ClaimAll(slot);
                }
                else
                {
                    HandleMouse(slots);
                    foreach (Slot slot in slots) HandleInput(slot, canUnlock, TakenByOthers(slot, slots, lastRemote));
                }

                bool everyoneReady = true;
                foreach (Slot slot in slots) everyoneReady &= slot.Ready;

                List<SkillSync.RemoteCard> remote = null;
                if (_sync != null)
                {
                    bool othersReady;
                    _sync.Publish(Entries(slots, false));
                    remote = _sync.Poll(out othersReady);
                    lastRemote = remote;
                    everyoneReady &= othersReady;
                    if (_sync.MissingMod)
                    {
                        // Someone without the mod is in the game after all: nobody gets skills.
                        foreach (Slot slot in slots) NoChanges(slot);
                        _sync.Finish(Entries(slots, true), true);
                        Plugin.Log.LogInfo("Có người chơi không cài Chef Skills; màn này chơi bình thường, không dùng kỹ năng.");
                        Close();
                        yield break;
                    }
                    if (_sync.HostSkipped)
                    {
                        foreach (Slot slot in slots) NoChanges(slot);
                        _sync.Finish(Entries(slots, true), false);
                        Plugin.Log.LogInfo("Chủ phòng đã bỏ qua màn chọn kỹ năng; mọi người dùng kỹ năng mặc định.");
                        Close();
                        yield break;
                    }
                }

                _view.Render(Cards(slots, remote), everyoneReady, _sync != null && _sync.WaitingForHost);
                RenderPickers(slots, remote);

                if (everyoneReady)
                {
                    if (_sync != null) _sync.Finish(Entries(slots, false), false);
                    // Keep animating briefly so everyone sees the last READY! land.
                    float hold = 0f;
                    while (hold < ReadyHoldSeconds && _view.IsAlive)
                    {
                        yield return null;
                        hold += Time.unscaledDeltaTime;
                        _view.Render(Cards(slots, remote), true, false);
                    }
                    break;
                }
                yield return null;
            }

            foreach (Slot slot in slots)
            {
                Plugin.Skills.Set(slot.Player, slot.Levels, slot.Specials);
                Plugin.Log.LogInfo(string.Format("Người chơi {0}: {1}; {2}", (int)slot.Player + 1,
                    SkillStats.Summary(slot.Levels), SpecialCatalog.Summary(SpecialCatalog.Enforce(slot.Specials))));
            }
            Close();
        }

        /// <summary>This machine's builds as sent to the other machines.</summary>
        private static List<SkillSync.Entry> Entries(List<Slot> slots, bool allReady)
        {
            var entries = new List<SkillSync.Entry>();
            foreach (Slot slot in slots)
                entries.Add(new SkillSync.Entry
                {
                    Slot = (int)slot.Player, Ready = allReady || slot.Ready, Colour = slot.ColourIndex,
                    Levels = (int[])slot.Levels.Clone(), Chefs = slot.Chefs, Specials = (int[])slot.Specials.Clone()
                });
            return entries;
        }

        /// <summary>No skill changes and no specials (skipped, or someone lacks the mod).</summary>
        private static void NoChanges(Slot slot)
        {
            slot.Levels = SkillStats.Empty();
            slot.Specials = SpecialCatalog.Empty();
        }

        /// <summary>
        /// "One per kitchen" specials already locked in by another player (here or on another
        /// machine): this slot can't lock in with them.
        /// </summary>
        private static HashSet<Special> TakenByOthers(Slot self, List<Slot> slots, List<SkillSync.RemoteCard> remote)
        {
            var taken = new HashSet<Special>();
            foreach (Slot slot in slots)
                if (slot != self && slot.Ready) AddTeamWide(taken, slot.Specials);
            if (remote != null)
                foreach (SkillSync.RemoteCard card in remote)
                    if (card.State == SkillSync.CardState.Ready) AddTeamWide(taken, card.Specials);
            return taken;
        }

        private static void AddTeamWide(HashSet<Special> taken, int[] specials)
        {
            if (specials == null) return;
            foreach (int pick in specials)
            {
                SpecialInfo info = SpecialCatalog.Info((Special)pick);
                if (info != null && info.TeamWide) taken.Add(info.Id);
            }
        }

        /// <summary>This machine's cards first, then everyone else's in the session's order.</summary>
        private static List<SkillSelectView.CardInfo> Cards(List<Slot> slots, List<SkillSync.RemoteCard> remote)
        {
            var cards = new List<SkillSelectView.CardInfo>();
            foreach (Slot slot in slots)
                cards.Add(new SkillSelectView.CardInfo
                {
                    Key = "L" + (int)slot.Player, Title = slot.Title, IsLocal = true, Player = slot.Player,
                    Colour = slot.Colour, Levels = slot.Levels, Selected = slot.Selected,
                    FreePoints = slot.FreePoints, DeniedAt = slot.DeniedAt, DeniedReason = slot.DeniedReason,
                    State = slot.Ready ? SkillSync.CardState.Ready : SkillSync.CardState.Choosing,
                    Specials = slot.Specials, KeyName = SpecialInput.NameFor(slot.Player),
                    DetailsKeyName = SpecialInput.DetailsNameFor(slot.Player) ?? Lang.T("NONE", "KHÔNG CÓ"),
                    BarMode = SkillBarSettings.Name(SkillBarSettings.Get(slot.Rank)), Keyboard = SpecialInput.IsKeyboard(slot.Player),
                    Capturing = slot.Capture != null, CapturingDetails = slot.Capture != null && slot.Capture.Details, Notice = slot.Notice, NoticeAt = slot.NoticeAt
                });
            if (remote != null)
                foreach (SkillSync.RemoteCard card in remote)
                    cards.Add(new SkillSelectView.CardInfo
                    {
                        Key = "R" + card.Key, Title = card.Name, IsLocal = false,
                        Colour = GameArt.ColourAt(card.Colour), Levels = card.Levels, Selected = -1,
                        FreePoints = SkillStats.FreePoints(card.Levels, card.Specials), DeniedAt = -10f,
                        State = card.State, Specials = card.Specials
                    });
            return cards;
        }

        /// <summary>
        /// HIDE (the game's change-mode button: Y on a pad, Ctrl on the keyboard; or Tab) hides the
        /// screen so the players can look at the kitchen, and shows it again; SELECT, LOCK IN or BACK also
        /// bring it back. True while it is hidden.
        /// </summary>
        private static bool HandleHide(List<Slot> slots)
        {
            bool capturing = false;
            foreach (Slot slot in slots) capturing |= slot.Capture != null || slot.Picker != null;
            if (capturing) return false;
            bool toggle = false;
            foreach (Slot slot in slots)
                if (slot.Hide.JustPressed())
                {
                    slot.Hide.ClaimPressEvent();
                    toggle = true;
                }
            if (Input.GetKeyDown(KeyCode.Tab)) toggle = true;
            if (_view.Hidden)
                foreach (Slot slot in slots)
                    // JustPressed claims the press it reports, so neither press does anything else.
                    if (slot.Select.JustPressed() || slot.Cancel.JustPressed() || slot.LockIn.JustPressed())
                    {
                        _view.SetHidden(false);
                        PlaySound(GameOneShotAudioTag.UIHighlight);
                        return false;
                    }
            if (toggle)
            {
                _view.SetHidden(!_view.Hidden);
                PlaySound(GameOneShotAudioTag.UIHighlight);
            }
            return _view.Hidden;
        }

        /// <summary>Throws away this player's pending presses (they meant nothing just now).</summary>
        private static void ClaimAll(Slot slot)
        {
            Discard(slot.Up);
            Discard(slot.Down);
            Discard(slot.Left);
            Discard(slot.Right);
            Discard(slot.Select);
            Discard(slot.Cancel);
            Discard(slot.Hide);
            Discard(slot.LockIn);
        }

        /// <summary>
        /// Throws away a pending press by reading it. A bare ClaimPressEvent stays in force until the
        /// game next sees the button up, so claiming every frame without reading the button would
        /// swallow the next real press (that is what made the hidden screen impossible to bring back).
        /// </summary>
        private static void Discard(ILogicalButton button)
        {
            if (button != null) button.JustPressed();
        }

        private static void HandleInput(Slot slot, bool canUnlock, HashSet<Special> taken)
        {
            if (slot.Capture != null)
            {
                // Setting a new skill or details key: every press goes to that until it's done.
                string result;
                bool done = slot.Capture.Poll(out result);
                ClaimAll(slot);
                if (!done) return;
                bool saved = slot.Capture.Saved;
                slot.Capture = null;
                slot.Notice = result;
                slot.NoticeAt = Time.unscaledTime;
                PlaySound(saved ? GameOneShotAudioTag.UISelect : GameOneShotAudioTag.UIBack);
                return;
            }
            if (slot.Picker != null)
            {
                // The special picker is open: every press goes to it.
                Special picked = Special.None;
                SkillPicker.Outcome outcome;
                try
                {
                    outcome = slot.Picker.HandleInput(slot.Up, slot.Down, slot.Left, slot.Right,
                        slot.Select, slot.Cancel, slot.Specials, taken, out picked);
                }
                catch (Exception error)
                {
                    Plugin.Log.LogError("Bảng chọn kỹ năng bị lỗi: " + error);
                    outcome = SkillPicker.Outcome.Closed;
                }
                ClaimAll(slot);
                switch (outcome)
                {
                    case SkillPicker.Outcome.Moved:
                        PlaySound(GameOneShotAudioTag.UIHighlight);
                        break;
                    case SkillPicker.Outcome.Refused:
                        PlaySound(GameOneShotAudioTag.UIBack);
                        break;
                    case SkillPicker.Outcome.Picked:
                        slot.Specials[(int)slot.Picker.Slot] = (int)picked;
                        ClosePicker(slot);
                        PlaySound(GameOneShotAudioTag.UISelect);
                        break;
                    case SkillPicker.Outcome.Closed:
                        ClosePicker(slot);
                        PlaySound(GameOneShotAudioTag.UIBack);
                        break;
                }
                return;
            }
            if (!slot.Ready && IsSpecialRow(slot.Selected) && slot.Select.JustPressed())
            {
                slot.Select.ClaimPressEvent();
                OpenPicker(slot, false);
                return;
            }
            bool keyRow = slot.Selected == SkillSelectView.KeyRow || slot.Selected == SkillSelectView.DetailsRow;
            if (!slot.Ready && keyRow && slot.Select.JustPressed())
            {
                slot.Select.ClaimPressEvent();
                slot.Capture = new SpecialInput.Capture(slot.Player, slot.Selected == SkillSelectView.DetailsRow);
                PlaySound(GameOneShotAudioTag.UISelect);
                return;
            }
            if (!slot.Ready && slot.Selected == SkillSelectView.BarRow && slot.Select.JustPressed())
            {
                StepBar(slot, 1);
                return;
            }
            // SELECT on a skill row does nothing: locking in has its own button.
            Discard(slot.Select);
            if (slot.LockIn.JustPressed())
            {
                if (!slot.Ready)
                {
                    // A build can only be locked in when it adds up to exactly zero: no point left
                    // to spend and none overspent. And a "one per kitchen" special can't be
                    // locked in once another player has it.
                    Special clash = Special.None;
                    foreach (int pick in slot.Specials)
                        if (taken.Contains((Special)pick)) clash = (Special)pick;
                    if (clash != Special.None)
                        Deny(slot, SpecialCatalog.NameOf(clash) + Lang.T(" IS TAKEN: ONE PER KITCHEN", " ĐÃ CÓ NGƯỜI CHỌN: MỖI BẾP MỘT"));
                    else if (slot.FreePoints == 0)
                    {
                        PlaySound(GameOneShotAudioTag.UISelect);
                        slot.Ready = true;
                    }
                    else Deny(slot);
                }
            }
            if (slot.Cancel.JustPressed())
            {
                slot.Cancel.ClaimPressEvent();
                if (slot.Ready && canUnlock)
                {
                    PlaySound(GameOneShotAudioTag.UIBack);
                    slot.Ready = false;
                }
            }
            if (slot.Ready) return;

            int count = SkillSelectView.RowCount;
            if (slot.Selected >= count) slot.Selected = count - 1;
            int move = 0;
            if (slot.Up.JustPressed()) { slot.Up.ClaimPressEvent(); move = -1; }
            if (slot.Down.JustPressed()) { slot.Down.ClaimPressEvent(); move = 1; }
            if (move != 0)
            {
                slot.Selected = (slot.Selected + move + count) % count;
                PlaySound(GameOneShotAudioTag.UIHighlight);
            }

            if (slot.Selected == SkillSelectView.BarRow)
            {
                // DISPLAY: Left/Right step through on screen / over head / hidden.
                if (slot.Left.JustPressed()) StepBar(slot, -1);
                if (slot.Right.JustPressed()) StepBar(slot, 1);
                return;
            }
            if (slot.Selected >= SkillStats.Count)
            {
                // Special and key rows: SELECT opens the picker / sets the key; Left/Right do nothing.
                Discard(slot.Left);
                Discard(slot.Right);
                return;
            }

            int max = SkillStats.MaxLevel;
            int index = slot.Selected;
            if (slot.Left.JustPressed())
            {
                slot.Left.ClaimPressEvent();
                if (slot.Levels[index] > -max)
                {
                    slot.Levels[index]--;
                    PlaySound(GameOneShotAudioTag.UIHighlight);
                }
                else Deny(slot);
            }
            if (slot.Right.JustPressed())
            {
                slot.Right.ClaimPressEvent();
                // Raising costs a point. It's allowed with none free (the build goes over), but
                // then it can't be locked in until something else is lowered to pay for it.
                if (slot.Levels[index] < max)
                {
                    slot.Levels[index]++;
                    PlaySound(GameOneShotAudioTag.UIHighlight);
                }
                else Deny(slot);
            }
        }

        /// <summary>Changes where this player's skill bar shows in a level (saved for next time).</summary>
        private static void StepBar(Slot slot, int step)
        {
            SkillBarSettings.Step(slot.Rank, step);
            PlaySound(GameOneShotAudioTag.UIHighlight);
        }

        private static bool IsSpecialRow(int row)
        {
            return SpecialCatalog.Enabled && row >= SkillStats.Count && row < SkillStats.Count + SpecialCatalog.SlotCount;
        }

        /// <summary>Opens the special picker for the slot's highlighted ACTIVE / PASSIVE / QUIRK row.</summary>
        private static void OpenPicker(Slot slot, bool byMouse)
        {
            if (_view == null || _view.Overlay == null || !IsSpecialRow(slot.Selected)) return;
            var which = (SpecialSlot)(slot.Selected - SkillStats.Count);
            string owner = _slots != null && _slots.Count > 1 ? slot.Title : null;
            try
            {
                // With several players here, each picker keeps to its own player's part of the
                // screen, so everyone else can still see their card and carry on.
                bool shared = _slots != null && _slots.Count > 1;
                slot.Picker = SkillPicker.Open(_view.Overlay, _view.Art, slot.Player, owner, which, (Special)slot.Specials[(int)which], byMouse, shared);
                if (_slots != null) PlacePickers(_slots);
                PlaySound(GameOneShotAudioTag.UISelect);
            }
            catch (Exception error)
            {
                // A broken picker must not end the whole skill screen: close it and say why.
                Plugin.Log.LogError("Không mở được bảng chọn kỹ năng: " + error);
                SkillPicker.CloseAll();
                foreach (Slot other in _slots ?? new List<Slot>()) other.Picker = null;
                Deny(slot, Lang.T("CAN'T OPEN THE LIST (SEE LOG)", "KHÔNG MỞ ĐƯỢC DANH SÁCH (XEM LOG)"));
            }
        }

        private static void ClosePicker(Slot slot)
        {
            if (slot.Picker != null) slot.Picker.Close();
            slot.Picker = null;
        }

        /// <summary>
        /// A click on a special row of one of this machine's cards opens the picker for it (when
        /// that player has none open, the click isn't on someone's open picker, and the screen
        /// isn't hidden).
        /// </summary>
        private static void HandleMouse(List<Slot> slots)
        {
            if (_view == null || _view.Hidden || !Input.GetMouseButtonDown(0)) return;
            if (SkillPicker.AnyAt(Input.mousePosition)) return;
            string key;
            int row;
            if (!_view.SpecialRowAt(Input.mousePosition, out key, out row)) return;
            foreach (Slot slot in slots)
            {
                if ("L" + (int)slot.Player != key || slot.Ready || slot.Picker != null || slot.Capture != null) continue;
                slot.Selected = row;
                OpenPicker(slot, true);
                return;
            }
        }

        // Half the screen's width in the canvas's reference units, less a margin.
        private const float PickerScreenHalf = 920f;

        /// <summary>
        /// With several players on this machine, the screen is split into one lane per player
        /// (split halfway between neighbouring cards), and each open picker stays in its player's
        /// lane over their own card, leaving the other players' cards in view.
        /// </summary>
        private static void PlacePickers(List<Slot> slots)
        {
            if (_view == null || slots.Count < 2) return;
            var placed = new List<KeyValuePair<float, Slot>>();
            foreach (Slot slot in slots)
            {
                float x;
                if (_view.CardX("L" + (int)slot.Player, out x)) placed.Add(new KeyValuePair<float, Slot>(x, slot));
            }
            if (placed.Count < 2) return;
            placed.Sort(delegate(KeyValuePair<float, Slot> a, KeyValuePair<float, Slot> b) { return a.Key.CompareTo(b.Key); });
            for (int i = 0; i < placed.Count; i++)
            {
                Slot slot = placed[i].Value;
                if (slot.Picker == null) continue;
                float left = i == 0 ? -PickerScreenHalf : (placed[i - 1].Key + placed[i].Key) / 2f;
                float right = i == placed.Count - 1 ? PickerScreenHalf : (placed[i].Key + placed[i + 1].Key) / 2f;
                slot.Picker.SetLane(left, right);
            }
        }

        /// <summary>Draws the open pickers (over the cards).</summary>
        private static void RenderPickers(List<Slot> slots, List<SkillSync.RemoteCard> remote)
        {
            PlacePickers(slots);
            foreach (Slot slot in slots)
            {
                if (slot.Picker == null) continue;
                try
                {
                    slot.Picker.Render(slot.Specials, TakenByOthers(slot, slots, remote));
                }
                catch (Exception error)
                {
                    // A broken picker closes; the skill screen carries on.
                    Plugin.Log.LogError("Bảng chọn kỹ năng bị lỗi: " + error);
                    ClosePicker(slot);
                }
            }
        }

        private static void Deny(Slot slot)
        {
            Deny(slot, null);
        }

        private static void Deny(Slot slot, string reason)
        {
            slot.DeniedAt = Time.unscaledTime;
            slot.DeniedReason = reason;
            PlaySound(GameOneShotAudioTag.UIBack);
        }

        private static void PlaySound(GameOneShotAudioTag tag)
        {
            try
            {
                GameUtils.TriggerAudio(tag, LayerMask.NameToLayer("UI"));
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không phát được âm thanh UI: " + error.Message);
            }
        }

        private static void Close()
        {
            // However the screen ended (including an error or the level being torn down), tell
            // the other machines this one is done, so nobody waits for its players.
            if (_sync != null && !_sync.Finished && _slots != null) _sync.Finish(Entries(_slots, true), false);
            _sync = null;
            SkillPicker.CloseAll();
            _slots = null;
            // Fades out on its own while the game's intro starts.
            if (_view != null) _view.Close();
            _view = null;
            _open = false;
        }

        /// <summary>
        /// Players with a chef on this machine (one card even if a player controls two chefs), each
        /// with the network entity IDs of their chefs.
        /// </summary>
        private static Dictionary<PlayerInputLookup.Player, List<uint>> FindLocalPlayers()
        {
            var players = new Dictionary<PlayerInputLookup.Player, List<uint>>();
            foreach (PlayerIDProvider provider in Object.FindObjectsOfType<PlayerIDProvider>())
            {
                if (!provider.IsLocallyControlled()) continue;
                if (provider.GetComponent<PlayerControls>() == null) continue;
                PlayerInputLookup.Player id = provider.GetID();
                List<uint> chefs;
                if (!players.TryGetValue(id, out chefs))
                {
                    chefs = new List<uint>();
                    players[id] = chefs;
                }
                uint entity = SkillManager.EntityId(provider.gameObject);
                if (entity != 0 && !chefs.Contains(entity)) chefs.Add(entity);
            }
            return players;
        }
    }
}
