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
    /// one, or going over), SELECT locks the build in only when it adds up to exactly zero, BACK
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
            public string Title;
            public ChefColourData Colour;
            public int ColourIndex;
            public uint[] Chefs;
            public int[] Levels;
            public int Selected;
            public bool Ready;
            public float DeniedAt = -10f;
            public ILogicalButton Up;
            public ILogicalButton Down;
            public ILogicalButton Left;
            public ILogicalButton Right;
            public ILogicalButton Select;
            public ILogicalButton Cancel;
            public ILogicalButton Skip;
            public bool SkipArmed;
            public float SkipHeld;

            public int FreePoints
            {
                get { return SkillStats.FreePoints(Levels); }
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
                _sync != null && _sync.IsHost ? "HOLD: SKIP FOR ALL" : "HOLD: SKIP");
            _open = true;

            var slots = new List<Slot>();
            foreach (PlayerInputLookup.Player player in players)
            {
                ChefColourData colour = GameArt.ColourOf(player);
                var slot = new Slot
                {
                    Player = player,
                    Title = _sync != null && players.Count == 1 ? "YOU" : "PLAYER " + ((int)player + 1),
                    Colour = colour,
                    ColourIndex = GameArt.ColourIndex(colour),
                    Chefs = chefs[player].ToArray(),
                    Levels = Plugin.Skills.LastPicked(player),
                    Up = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UIUp, player),
                    Down = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UIDown, player),
                    Left = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UILeft, player),
                    Right = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UIRight, player),
                    Select = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UISelectNotStart, player),
                    Cancel = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UICancel, player),
                    Skip = PlayerInputLookup.GetButton(PlayerInputLookup.LogicalButtonID.UISkip, player),
                };
                // Ignore presses left over from the loading screen.
                slot.Up.ClaimPressEvent();
                slot.Down.ClaimPressEvent();
                slot.Left.ClaimPressEvent();
                slot.Right.ClaimPressEvent();
                slot.Select.ClaimPressEvent();
                slot.Cancel.ClaimPressEvent();
                slots.Add(slot);
            }
            _slots = slots;

            Plugin.Log.LogInfo(string.Format("Mở màn chọn kỹ năng cho {0} người chơi trên máy này{1}.",
                slots.Count, _sync != null ? " (chơi trực tuyến: chờ mọi người trong phòng)" : ""));

            // No time limit: the screen stays until every player has locked in (or someone holds Skip).
            while (true)
            {
                float skipProgress = SkipProgress(slots);
                _view.ShowSkipProgress(skipProgress);
                if (skipProgress >= 1f)
                {
                    foreach (Slot slot in slots) slot.Levels = SkillStats.Empty();
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
                foreach (Slot slot in slots) HandleInput(slot, canUnlock);

                bool everyoneReady = true;
                foreach (Slot slot in slots) everyoneReady &= slot.Ready;

                List<SkillSync.RemoteCard> remote = null;
                if (_sync != null)
                {
                    bool othersReady;
                    _sync.Publish(Entries(slots, false));
                    remote = _sync.Poll(out othersReady);
                    everyoneReady &= othersReady;
                    if (_sync.MissingMod)
                    {
                        // Someone without the mod is in the game after all: nobody gets skills.
                        foreach (Slot slot in slots) slot.Levels = SkillStats.Empty();
                        _sync.Finish(Entries(slots, true), true);
                        Plugin.Log.LogInfo("Có người chơi không cài Chef Skills; màn này chơi bình thường, không dùng kỹ năng.");
                        Close();
                        yield break;
                    }
                    if (_sync.HostSkipped)
                    {
                        foreach (Slot slot in slots) slot.Levels = SkillStats.Empty();
                        _sync.Finish(Entries(slots, true), false);
                        Plugin.Log.LogInfo("Chủ phòng đã bỏ qua màn chọn kỹ năng; mọi người dùng kỹ năng mặc định.");
                        Close();
                        yield break;
                    }
                }

                _view.Render(Cards(slots, remote), everyoneReady, _sync != null && _sync.WaitingForHost);

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
                Plugin.Skills.Set(slot.Player, slot.Levels);
                Plugin.Log.LogInfo(string.Format("Người chơi {0}: {1}", (int)slot.Player + 1, SkillStats.Summary(slot.Levels)));
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
                    Levels = (int[])slot.Levels.Clone(), Chefs = slot.Chefs
                });
            return entries;
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
                    FreePoints = slot.FreePoints, DeniedAt = slot.DeniedAt,
                    State = slot.Ready ? SkillSync.CardState.Ready : SkillSync.CardState.Choosing
                });
            if (remote != null)
                foreach (SkillSync.RemoteCard card in remote)
                    cards.Add(new SkillSelectView.CardInfo
                    {
                        Key = "R" + card.Key, Title = card.Name, IsLocal = false,
                        Colour = GameArt.ColourAt(card.Colour), Levels = card.Levels, Selected = -1,
                        FreePoints = SkillStats.FreePoints(card.Levels), DeniedAt = -10f,
                        State = card.State
                    });
            return cards;
        }

        private static void HandleInput(Slot slot, bool canUnlock)
        {
            if (slot.Select.JustPressed())
            {
                slot.Select.ClaimPressEvent();
                if (!slot.Ready)
                {
                    // A build can only be locked in when it adds up to exactly zero: no point left
                    // to spend and none overspent.
                    if (slot.FreePoints == 0)
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

            int count = SkillStats.Count;
            int move = 0;
            if (slot.Up.JustPressed()) { slot.Up.ClaimPressEvent(); move = -1; }
            if (slot.Down.JustPressed()) { slot.Down.ClaimPressEvent(); move = 1; }
            if (move != 0)
            {
                slot.Selected = (slot.Selected + move + count) % count;
                PlaySound(GameOneShotAudioTag.UIHighlight);
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

        private static void Deny(Slot slot)
        {
            slot.DeniedAt = Time.unscaledTime;
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
            _slots = null;
            if (_view != null) _view.Destroy();
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
