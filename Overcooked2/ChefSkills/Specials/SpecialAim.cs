using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChefSkills
{
    /// <summary>
    /// Something an active special can be aimed at while its player holds the skill key: a chef, an
    /// object (a board, a sink, a pot, an item...) or a spot on the floor (where a jump lands).
    /// </summary>
    internal sealed class AimTarget
    {
        /// <summary>A chef target (followed as it moves).</summary>
        public ChefState Chef;
        /// <summary>An object target (followed as it moves); null for a fixed spot.</summary>
        public GameObject Object;
        /// <summary>A fixed spot (the floor where a jump lands), or where the object was.</summary>
        public Vector3 Position;
        /// <summary>The facing to use it with (zero: towards the target).</summary>
        public Vector3 Direction;
        /// <summary>Ring size, metres.</summary>
        public float Radius = 0.5f;
        /// <summary>Height of the ring over the object's position (its top).</summary>
        public float Lift;

        internal bool Gone
        {
            get { return Chef != null ? Chef.Chef == null : _hadObject && Object == null; }
        }

        private bool _hadObject;

        /// <summary>Where the target is now (the chef's feet, the object, the spot).</summary>
        public Vector3 Current
        {
            get
            {
                if (Chef != null && Chef.Chef != null) return Chef.Position;
                if (Object != null) return Object.transform.position;
                return Position;
            }
        }

        /// <summary>A chef to aim at.</summary>
        internal static AimTarget ForChef(ChefState chef)
        {
            return new AimTarget { Chef = chef, Position = chef.Position, Radius = 0.75f };
        }

        /// <summary>A spot on the floor, reached by using the special facing <paramref name="direction"/>.</summary>
        internal static AimTarget ForSpot(Vector3 spot, Vector3 direction)
        {
            return new AimTarget { Position = spot, Direction = direction, Radius = 0.55f };
        }

        /// <summary>An object: the ring lies on top of it, about its size.</summary>
        internal static AimTarget ForObject(GameObject target)
        {
            var aim = new AimTarget { Object = target, Position = target.transform.position, _hadObject = true };
            Bounds bounds;
            if (BoundsOf(target, out bounds))
            {
                aim.Lift = Mathf.Clamp(bounds.max.y - target.transform.position.y, 0f, 2.5f) + 0.02f;
                aim.Radius = Mathf.Clamp(Mathf.Max(bounds.extents.x, bounds.extents.z) + 0.1f, 0.35f, 0.8f);
            }
            return aim;
        }

        private static bool BoundsOf(GameObject target, out Bounds bounds)
        {
            bounds = new Bounds();
            bool any = false;
            foreach (Renderer r in target.GetComponentsInChildren<Renderer>())
            {
                if (r == null || !r.enabled || r.GetType().Name == "ParticleSystemRenderer") continue;
                if (!any)
                {
                    bounds = r.bounds;
                    any = true;
                }
                else bounds.Encapsulate(r.bounds);
            }
            return any;
        }
    }

    /// <summary>
    /// Choosing what an active is used on. For a special that works on the thing in front (the
    /// teammate you face, the board in front, the floor ahead...), holding the skill key shows
    /// every target in reach, with the one it would pick anyway (the one faced, or the nearest)
    /// chosen; the movement keys or stick move the choice from target to target, and letting go
    /// of the skill key uses the special on the chosen one. A quick tap still uses it on the
    /// one it would pick anyway.
    ///
    /// While a player aims, their chef stands still (on every machine, so the host agrees), and
    /// everyone sees which target they have chosen: a teammate about to be swapped or summoned
    /// sees it coming.
    /// </summary>
    internal static class SpecialAim
    {
        // How long other machines keep a chef standing still without hearing from its owner.
        private const float HoldSeconds = 4f;
        private const float KeepAliveSeconds = 1.5f;
        // The stick: pushed past this starts a move; back under the lower value lets it go.
        private const float PushedAt = 0.55f;
        private const float ReleasedAt = 0.3f;
        private const float FirstRepeat = 0.42f;
        private const float Repeat = 0.24f;
        // How far off the pushed direction the next target may be.
        private const float StepArc = 70f;

        /// <summary>Every machine: a chef is aiming (I = special; 0 = done). Vector1 = chosen target, Text = its chef.</summary>
        internal const ushort Topic = SpecialNet.Topic.Aim;

        /// <summary>The player's machine: one player holding the skill key.</summary>
        internal sealed class Session
        {
            public Special Special;
            public List<AimTarget> Targets;
            public int Selected;
            public float Started;
            public float SentAt;
            public bool StickHeld;
            public Vector3 StickDirection;
            public float RepeatAt;
            public float MovedAt = -10f;

            public AimTarget Chosen
            {
                get { return Selected >= 0 && Selected < Targets.Count ? Targets[Selected] : null; }
            }
        }

        /// <summary>Other machines: what a chef aiming elsewhere has chosen.</summary>
        internal sealed class Remote
        {
            public Special Special;
            public Vector3 Point;
            public ChefState Chef;
        }

        internal static void Register()
        {
            SpecialNet.Register(Topic, OnAim);
        }

        /// <summary>Is the chef aiming (it can't run or dash meanwhile)? Every machine.</summary>
        internal static bool Aiming(ChefState chef)
        {
            return chef != null && chef.AimingUntil > SpecialRuntime.Now;
        }

        // --- the player's machine -------------------------------------------------------------

        /// <summary>
        /// The skill key was just pressed for a special with targets: start choosing. False when
        /// there's nothing to choose from (the special is then used at once and the host says why
        /// it can't be).
        /// </summary>
        internal static bool Begin(ChefState state, Special active, Func<ChefState, List<AimTarget>> source)
        {
            List<AimTarget> targets;
            try
            {
                state.Using = active;
                state.UsingSlot = Mathf.Max(0, state.SlotOf(active));
                targets = source(state);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail(active + ".Targets", error);
                return false;
            }
            if (targets == null || targets.Count == 0) return false;
            float now = Time.unscaledTime;
            // The session keeps the special it was opened for: letting go fires that one even if
            // the selection changed meanwhile (switching cancels the aim anyway).
            state.Aim = new Session { Special = active, Targets = targets, Selected = 0, Started = now };
            Send(state);
            return true;
        }

        /// <summary>Every frame while choosing: <paramref name="down"/> is the skill key.</summary>
        internal static void Hold(ChefState state, bool down)
        {
            Session session = state.Aim;
            if (!state.HasActive(session.Special) || !SpecialRuntime.CanUseNow(state))
            {
                Stop(state);
                return;
            }
            Prune(session);
            if (session.Targets.Count == 0)
            {
                // Everything it could be used on went away: let the host say so.
                Stop(state);
                SpecialRuntime.Fire(state, session.Special, null);
                return;
            }
            if (!down)
            {
                AimTarget chosen = session.Chosen;
                Stop(state);
                SpecialRuntime.Fire(state, session.Special, chosen);
                return;
            }
            Steer(state, session);
            if (Time.unscaledTime - session.SentAt > KeepAliveSeconds) Send(state);
        }

        /// <summary>The player pressed SWITCH while choosing: stop without using anything (no cooldown starts).</summary>
        internal static void Cancel(ChefState state)
        {
            Stop(state);
        }

        /// <summary>Stops choosing without using it (the chef was knocked out, a menu opened...).</summary>
        internal static void Stop(ChefState state)
        {
            if (state.Aim == null) return;
            state.Aim = null;
            if (state.Id == 0)
            {
                state.AimingUntil = 0f;
                return;
            }
            SpecialNet.SendToAll(new SpecialMessage { Topic = Topic, Chef = state.Id, I = 0 });
        }

        private static void Prune(Session session)
        {
            AimTarget chosen = session.Chosen;
            bool removed = false;
            for (int i = session.Targets.Count - 1; i >= 0; i--)
            {
                AimTarget target = session.Targets[i];
                if (!target.Gone && (target.Chef == null || (target.Chef.Controls != null && !target.Chef.Controls.m_bRespawning)))
                    continue;
                session.Targets.RemoveAt(i);
                removed = true;
            }
            if (!removed) return;
            int index = chosen != null ? session.Targets.IndexOf(chosen) : -1;
            session.Selected = index >= 0 ? index : 0;
        }

        /// <summary>The movement keys or stick move the choice to the next target that way.</summary>
        private static void Steer(ChefState state, Session session)
        {
            Vector3 direction;
            float amount = Stick(state, out direction);
            float now = Time.unscaledTime;
            if (amount < ReleasedAt)
            {
                session.StickHeld = false;
                return;
            }
            if (amount < PushedAt && !session.StickHeld) return;
            bool fresh = !session.StickHeld || Vector3.Angle(direction, session.StickDirection) > 40f;
            if (!fresh && now < session.RepeatAt) return;
            session.StickHeld = true;
            session.StickDirection = direction;
            session.RepeatAt = now + (fresh ? FirstRepeat : Repeat);
            int next = Next(state, session, direction);
            if (next < 0 || next == session.Selected) return;
            session.Selected = next;
            session.MovedAt = now;
            Sound(GameOneShotAudioTag.UIHighlight);
            Send(state);
        }

        /// <summary>How far the chef's movement input is pushed (0-1), and which way in the kitchen.</summary>
        private static float Stick(ChefState state, out Vector3 direction)
        {
            direction = Vector3.zero;
            PlayerControls controls = state.Controls;
            PlayerControls.ControlSchemeData scheme = controls != null ? controls.ControlScheme : null;
            if (scheme == null || scheme.m_moveX == null || scheme.m_moveY == null || controls.Movement == null) return 0f;
            float x = scheme.m_moveX.GetValue(), y = scheme.m_moveY.GetValue();
            float amount = Mathf.Clamp01(new Vector2(x, y).magnitude);
            if (amount < 0.01f) return 0f;
            // The way the game itself turns the input into a direction in the kitchen.
            direction = PlayerControlsHelper.GetControlAxis(x, y, controls.Movement.XAxisAllignment, controls.Movement.YAxisAllignment);
            return direction.sqrMagnitude > 0.01f ? amount : 0f;
        }

        /// <summary>
        /// The next target from the chosen one in <paramref name="direction"/>: the nearest one
        /// roughly that way, closer to the line counting for more. From the chef when the chosen
        /// one has nothing beyond it (so pushing towards a target always gets there).
        /// </summary>
        private static int Next(ChefState state, Session session, Vector3 direction)
        {
            AimTarget chosen = session.Chosen;
            int best = chosen != null ? Pick(session, chosen.Current, direction, session.Selected) : -1;
            if (best < 0) best = Pick(session, state.Position, direction, session.Selected);
            return best;
        }

        private static int Pick(Session session, Vector3 from, Vector3 direction, int skip)
        {
            int best = -1;
            float bestScore = float.MaxValue;
            for (int i = 0; i < session.Targets.Count; i++)
            {
                if (i == skip) continue;
                Vector3 offset = session.Targets[i].Current - from;
                offset.y = 0f;
                float distance = offset.magnitude;
                if (distance < 0.15f) continue;
                float angle = Vector3.Angle(direction, offset);
                if (angle > StepArc) continue;
                float score = distance * (1f + angle / 30f);
                if (score < bestScore)
                {
                    best = i;
                    bestScore = score;
                }
            }
            return best;
        }

        /// <summary>Tells every machine what this chef is aiming at (and keeps it standing still there).</summary>
        private static void Send(ChefState state)
        {
            Session session = state.Aim;
            if (session == null) return;
            session.SentAt = Time.unscaledTime;
            AimTarget chosen = session.Chosen;
            if (state.Id == 0)
            {
                state.AimingUntil = SpecialRuntime.Now + HoldSeconds;
                return;
            }
            SpecialNet.SendToAll(new SpecialMessage
            {
                Topic = Topic, Chef = state.Id, I = (int)session.Special,
                Vector1 = chosen != null ? chosen.Current : state.Position,
                Text = chosen != null && chosen.Chef != null && chosen.Chef.Id != 0 ? chosen.Chef.Id.ToString() : null
            });
        }

        private static void OnAim(SpecialMessage message)
        {
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state == null) return;
            if (message.I == 0)
            {
                state.AimingUntil = 0f;
                state.RemoteAim = null;
                return;
            }
            state.AimingUntil = SpecialRuntime.Now + HoldSeconds;
            uint id;
            ChefState chef = !string.IsNullOrEmpty(message.Text) && uint.TryParse(message.Text, out id) ? SpecialRuntime.StateById(id) : null;
            state.RemoteAim = new Remote { Special = (Special)message.I, Point = message.Vector1, Chef = chef };
        }

        /// <summary>The level ended: nobody is aiming.</summary>
        internal static void Clear()
        {
            s_pool.Clear();
            s_beams.Clear();
        }

        internal static void Sound(GameOneShotAudioTag tag)
        {
            try
            {
                GameUtils.TriggerAudio(tag, LayerMask.NameToLayer("UI"));
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không phát được âm thanh: " + error.Message);
            }
        }

        // --- picking a default and nearby things (for the modules' target lists) -----------------

        /// <summary>
        /// Puts the one the special picks when not aimed first: the nearest within
        /// <paramref name="arc"/> degrees of the facing, else the nearest of all.
        /// </summary>
        internal static List<AimTarget> FacedFirst(List<AimTarget> targets, Vector3 from, Vector3 forward, float arc)
        {
            if (targets.Count < 2) return targets;
            forward.y = 0f;
            int faced = -1, nearest = -1;
            float facedDistance = float.MaxValue, nearestDistance = float.MaxValue;
            for (int i = 0; i < targets.Count; i++)
            {
                Vector3 offset = targets[i].Current - from;
                offset.y = 0f;
                float distance = offset.magnitude;
                if (distance < nearestDistance)
                {
                    nearest = i;
                    nearestDistance = distance;
                }
                bool ahead = distance < 0.3f || (forward.sqrMagnitude > 1e-4f && Vector3.Angle(forward, offset) <= arc);
                if (ahead && distance < facedDistance)
                {
                    faced = i;
                    facedDistance = distance;
                }
            }
            MoveFirst(targets, faced >= 0 ? faced : nearest);
            return targets;
        }

        internal static void MoveFirst(List<AimTarget> targets, int index)
        {
            if (index <= 0 || index >= targets.Count) return;
            AimTarget first = targets[index];
            targets.RemoveAt(index);
            targets.Insert(0, first);
        }

        /// <summary>Flat distance between two points.</summary>
        internal static float Flat(Vector3 a, Vector3 b)
        {
            a.y = b.y = 0f;
            return Vector3.Distance(a, b);
        }

        // Probe directions for jumps: the facing first, then further and further round.
        private static readonly float[] ProbeAngles = { 0f, 30f, -30f, 60f, -60f, 90f, -90f, 120f, -120f, 150f, -150f, 180f };

        /// <summary>
        /// Landing spots for a special that goes the way the chef faces (vault, blink, grapple):
        /// one per direction round the chef that has one (the facing first), spots too close to an
        /// earlier one left out.
        /// </summary>
        internal static List<AimTarget> Directions(ChefState chef, Func<Vector3, Vector3?> landing)
        {
            var targets = new List<AimTarget>();
            Vector3 forward = chef.Forward;
            forward.y = 0f;
            forward = forward.sqrMagnitude > 1e-4f ? forward.normalized : Vector3.forward;
            for (int i = 0; i < ProbeAngles.Length; i++)
            {
                Vector3 direction = Quaternion.Euler(0f, ProbeAngles[i], 0f) * forward;
                Vector3? spot = landing(direction);
                if (!spot.HasValue) continue;
                bool near = false;
                for (int j = 0; j < targets.Count && !near; j++)
                    near = Flat(targets[j].Position, spot.Value) < 0.8f;
                if (!near) targets.Add(AimTarget.ForSpot(spot.Value, direction));
            }
            return targets;
        }

        // --- drawing (every frame) ----------------------------------------------------------------

        private sealed class Ring
        {
            public GameObject Root;
            public Image Outer, Inner;
        }

        private const float UnitsPerMetre = 250f;
        // The words (SWAP?, the hint) go in the stacks over the chefs (FxLabels), renewed every frame.
        private const float LabelHold = 0.12f;
        private const float NameSize = FxLabels.LabelSize; // on one line with the other labels over that chef
        private const float HintSize = 0.18f;
        private static readonly List<Ring> s_pool = new List<Ring>();
        private static readonly List<Ring> s_beams = new List<Ring>();
        private static int s_rings, s_beamCount;

        /// <summary>Draws every chef's aim on this machine (from SpecialRuntime.Update).</summary>
        internal static void Draw(IList<ChefState> chefs)
        {
            s_rings = s_beamCount = 0;
            if (!SpecialHud.GameMenuOpen())
            {
                Camera camera = SpecialFx.GameCamera();
                float now = Time.unscaledTime;
                for (int i = 0; i < chefs.Count; i++)
                {
                    ChefState chef = chefs[i];
                    if (chef.Chef == null) continue;
                    if (chef.Aim != null) DrawSession(chef, chef.Aim, now, camera);
                    else if (chef.RemoteAim != null && !chef.Local && Aiming(chef)) DrawRemote(chef, chef.RemoteAim, now, camera);
                }
            }
            Hide(s_pool, s_rings);
            Hide(s_beams, s_beamCount);
        }

        private static void DrawSession(ChefState chef, Session session, float now, Camera camera)
        {
            Color colour = SpecialFx.ColourOf(session.Special);
            float age = now - session.Started;
            float appear = Mathf.Clamp01(age / 0.15f);
            AimTarget chosen = session.Chosen;
            for (int i = 0; i < session.Targets.Count; i++)
            {
                AimTarget target = session.Targets[i];
                if (target == chosen) continue;
                DrawRing(Base(target), target.Radius * (0.8f + 0.2f * appear), colour, 0.45f * appear, false, now);
            }
            if (chosen == null) return;
            Vector3 at = Base(chosen);
            // The chosen one: bigger, bright, breathing, and a pop when the choice moves to it.
            float moved = now - session.MovedAt;
            float pop = moved < 0.18f ? Mathf.Lerp(1.35f, 1f, moved / 0.18f) : 1f + 0.08f * Mathf.Sin(age * 7f);
            DrawRing(at, chosen.Radius * 1.2f * pop, colour, appear, true, now);
            DrawBeam(chef.Position, chosen.Current, colour, 0.55f * appear);
            if (chosen.Chef != null)
                FxLabels.Hold(chosen.Chef.Chef, Vector3.zero, "aim/" + chef.Chef.GetInstanceID(), SpecialCatalog.NameOf(session.Special) + "?", colour, LabelHold, NameSize);
            // How to use it, over the chef.
            string hint = session.Targets.Count > 1
                ? Lang.T("MOVE: CHOOSE  -  LET GO: USE", "DI CHUYỂN: CHỌN  -  THẢ RA: DÙNG")
                : Lang.T("LET GO: USE", "THẢ RA: DÙNG");
            FxLabels.Hold(chef.Chef, Vector3.zero, "aim/hint", hint, new Color(1f, 1f, 1f, appear), LabelHold, HintSize);
        }

        private static void DrawRemote(ChefState chef, Remote aim, float now, Camera camera)
        {
            Color colour = SpecialFx.ColourOf(aim.Special);
            Vector3 point = aim.Chef != null && aim.Chef.Chef != null ? aim.Chef.Position : aim.Point;
            DrawRing(point, (aim.Chef != null ? 0.75f : 0.55f) * 1.2f * (1f + 0.08f * Mathf.Sin(now * 7f)), colour, 0.9f, true, now);
            DrawBeam(chef.Position, point, colour, 0.45f);
            if (aim.Chef != null)
                FxLabels.Hold(aim.Chef.Chef, Vector3.zero, "aim/" + chef.Chef.GetInstanceID(), SpecialCatalog.NameOf(aim.Special) + "?", colour, LabelHold, NameSize);
        }

        private static Vector3 Base(AimTarget target)
        {
            return target.Current + Vector3.up * target.Lift;
        }

        private static void DrawRing(Vector3 at, float radius, Color colour, float alpha, bool chosen, float now)
        {
            Ring ring = Take(s_pool, s_rings++, false);
            Transform root = ring.Root.transform;
            root.position = at + Vector3.up * 0.05f;
            root.rotation = Quaternion.Euler(90f, chosen ? (now * 90f) % 360f : 0f, 0f);
            float size = radius * 2f * UnitsPerMetre;
            ring.Outer.rectTransform.sizeDelta = new Vector2(size, size);
            ring.Inner.rectTransform.sizeDelta = new Vector2(size, size);
            Color c = colour;
            c.a = Mathf.Clamp01(alpha);
            ring.Outer.color = c;
            c.a = (chosen ? 0.4f : 0.15f) * Mathf.Clamp01(alpha);
            ring.Inner.color = c;
        }

        private static void DrawBeam(Vector3 from, Vector3 to, Color colour, float alpha)
        {
            Vector3 flat = to - from;
            flat.y = 0f;
            if (flat.sqrMagnitude < 0.04f) return;
            Ring beam = Take(s_beams, s_beamCount++, true);
            Transform root = beam.Root.transform;
            root.position = (from + to) * 0.5f + Vector3.up * 0.06f;
            root.rotation = Quaternion.LookRotation(Vector3.down, flat.normalized);
            beam.Outer.rectTransform.sizeDelta = new Vector2(0.12f * UnitsPerMetre, flat.magnitude * UnitsPerMetre);
            Color c = colour;
            c.a = Mathf.Clamp01(alpha);
            beam.Outer.color = c;
        }

        private static void Hide(List<Ring> pool, int used)
        {
            for (int i = used; i < pool.Count; i++)
                if (pool[i].Root != null && pool[i].Root.activeSelf) pool[i].Root.SetActive(false);
        }

        private static Ring Take(List<Ring> pool, int index, bool beam)
        {
            while (pool.Count <= index) pool.Add(null);
            Ring ring = pool[index];
            if (ring == null || ring.Root == null)
            {
                ring = MakeRing(beam);
                pool[index] = ring;
            }
            if (!ring.Root.activeSelf) ring.Root.SetActive(true);
            return ring;
        }

        private static GameObject MakeCanvas(string name, int order)
        {
            var root = new GameObject(name, typeof(RectTransform));
            Canvas canvas = root.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            canvas.sortingOrder = order;
            var rect = (RectTransform)root.transform;
            rect.sizeDelta = new Vector2(100f, 100f);
            rect.localScale = Vector3.one / UnitsPerMetre;
            return root;
        }

        private static Image AddImage(Transform parent, Sprite sprite)
        {
            var go = new GameObject("Image", typeof(RectTransform));
            var rect = (RectTransform)go.transform;
            rect.SetParent(parent, false);
            rect.anchorMin = rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.sizeDelta = new Vector2(100f, 100f);
            Image image = go.AddComponent<Image>();
            image.raycastTarget = false;
            image.sprite = sprite;
            // Drawn over the kitchen, so a target behind a counter still shows.
            image.material = SpecialFx.OnTopMaterial;
            return image;
        }

        private static Ring MakeRing(bool beam)
        {
            GameObject root = MakeCanvas(beam ? "ChefSkillsAim_Beam" : "ChefSkillsAim_Ring", 110);
            var ring = new Ring { Root = root };
            if (beam)
            {
                ring.Outer = AddImage(root.transform, null);
                return ring;
            }
            ring.Inner = AddImage(root.transform, SpecialFx.Glow);
            ring.Outer = AddImage(root.transform, SpecialFx.Ring);
            return ring;
        }

        /// <summary>Destroys what is drawn (a new level is loading).</summary>
        internal static void Destroy()
        {
            foreach (Ring ring in s_pool)
                if (ring != null && ring.Root != null) Object.Destroy(ring.Root);
            foreach (Ring ring in s_beams)
                if (ring != null && ring.Root != null) Object.Destroy(ring.Root);
            Clear();
        }
    }
}
