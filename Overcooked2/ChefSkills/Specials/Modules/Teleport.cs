using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Team17.Online.Multiplayer.Messaging;
using UnityEngine;

namespace ChefSkills.Specials
{
    /// <summary>
    /// Moving a chef somewhere else, safely, on the host and on the chef's own machine.
    ///
    /// The host is authoritative: <see cref="ServerMove"/> puts the chef's transform and rigidbody
    /// there, and its position goes out to everyone with the chef's usual sync. A remote player's
    /// machine would only follow once that arrives (its correction snaps it when it is more than
    /// 2.5 m off), so the player's own machine also moves its chef at once (<see cref="PredictMove"/>)
    /// through its PositionRecorder, which keeps the jump out of the lag compensation, and holds its
    /// correction back until the host reports the chef at the new place (or gives up), so the chef
    /// isn't pulled back while the host hasn't moved it yet.
    ///
    /// When the owner predicts, it sends the host the exact spot it picked (the host's view of a
    /// remote chef lags a little); the host checks it against its own pick and uses it if close and
    /// free (<see cref="Resolve"/>).
    /// </summary>
    internal static class Warp
    {
        private static readonly FieldInfo RecorderField =
            AccessTools.Field(typeof(ClientChefSynchroniser), "m_PositionRecorder");
        private static readonly FieldInfo StampField =
            AccessTools.Field(typeof(ClientChefSynchroniser), "m_ReceivedTimeStamp");
        private static readonly MethodInfo LocalParentMethod =
            AccessTools.Method(typeof(ClientChefSynchroniser), "GetLocalCurrentParent");

        // How long the owner holds its correction after a predicted move, at most.
        private const float WindowSeconds = 1.2f;
        // How long the host waits for the owner's own pick before using its own.
        private const float ProposalWaitSeconds = 0.4f;

        private sealed class Window
        {
            public Vector3 From, Target;
            public float Until;
        }

        private sealed class Pending
        {
            public ChefState Chef;
            public Special Special;
            public Vector3 HostTarget;
            public float Trust;
            public float Until;
            public Func<Vector3, bool> Valid;
            public Action<Vector3> Apply;
        }

        private sealed class Hop
        {
            public Transform Root, Model;
            public Vector3 Offset;
            public float Start, Duration, Height;
        }

        // Owner machine: chef GameObject instance ID -> its correction is held.
        private static readonly Dictionary<int, Window> s_windows = new Dictionary<int, Window>();
        // Host: chef entity ID -> a move waiting for the owner's pick.
        private static readonly Dictionary<uint, Pending> s_pending = new Dictionary<uint, Pending>();
        private static readonly List<Hop> s_hops = new List<Hop>();

        internal static readonly ushort TopicProposal = (ushort)((int)Special.Vault * 10 + 0);

        private static bool s_masks;
        internal static int GroundMask, BlockMask, KillMask, WorktopLayer = -1;

        internal static void InitMasks()
        {
            if (s_masks) return;
            s_masks = true;
            GroundMask = LayerMask.GetMask("Ground", "SlopedGround");
            // What blocks a chef: the game's own capsule check (CapsuleCollisionHelper), but other
            // chefs block too.
            int exclude = 0;
            string[] names = { "KillPlane", "Ground", "SlopedGround", "Ignore Raycast", "PlayerTriggerZone",
                "Attachments", "HeldAttachments", "PushedObjectBounds", "AttachedBackpack", "UI" };
            for (int i = 0; i < names.Length; i++)
            {
                int layer = LayerMask.NameToLayer(names[i]);
                if (layer >= 0) exclude |= 1 << layer;
            }
            BlockMask = ~exclude;
            int kill = LayerMask.NameToLayer("KillPlane");
            KillMask = kill >= 0 ? 1 << kill : 0;
            WorktopLayer = LayerMask.NameToLayer("Worktops");
        }

        internal static void Clear()
        {
            s_windows.Clear();
            s_pending.Clear();
            for (int i = 0; i < s_hops.Count; i++)
                if (s_hops[i].Model != null) s_hops[i].Model.localPosition = Vector3.zero;
            s_hops.Clear();
        }

        // --- geometry --------------------------------------------------------------------------

        /// <summary>Flat facing of a chef.</summary>
        internal static Vector3 Flat(Vector3 v)
        {
            v.y = 0f;
            return v.sqrMagnitude > 1e-4f ? v.normalized : Vector3.forward;
        }

        /// <summary>Floor under <paramref name="p"/> within <paramref name="tolerance"/> of height <paramref name="refY"/>.</summary>
        internal static bool Floor(Vector3 p, float refY, float tolerance, out Vector3 floor)
        {
            InitMasks();
            floor = p;
            RaycastHit hit;
            if (!Physics.Raycast(new Vector3(p.x, refY + 1.5f, p.z), Vector3.down, out hit, 1.5f + tolerance + 0.5f,
                    GroundMask, QueryTriggerInteraction.Ignore))
                return false;
            if (Mathf.Abs(hit.point.y - refY) > tolerance) return false;
            floor = hit.point;
            return true;
        }

        private static void Capsule(ChefState chef, Vector3 feet, out Vector3 bottom, out Vector3 top, out float radius)
        {
            float height = 1.4f, centre = 0.7f;
            radius = 0.35f;
            CapsuleCollider capsule = chef != null && chef.Chef != null ? chef.Chef.GetComponent<CapsuleCollider>() : null;
            if (capsule != null)
            {
                float scale = chef.Chef.transform.lossyScale.y;
                radius = capsule.radius * Mathf.Max(chef.Chef.transform.lossyScale.x, 0.01f);
                height = capsule.height * scale;
                centre = capsule.center.y * scale;
            }
            radius *= 0.95f;
            float half = Mathf.Max(0f, height * 0.5f - radius);
            // Lifted a little so the floor's edge never counts.
            bottom = feet + Vector3.up * Mathf.Max(centre - half, radius + 0.05f);
            top = feet + Vector3.up * Mathf.Max(centre + half, radius + 0.06f);
        }

        /// <summary>The chef could stand at <paramref name="feet"/> (nothing solid, no kill volume).</summary>
        internal static bool Free(ChefState chef, Vector3 feet, GameObject alsoIgnore)
        {
            InitMasks();
            Vector3 bottom, top;
            float radius;
            Capsule(chef, feet, out bottom, out top, out radius);
            Collider[] hits = Physics.OverlapCapsule(bottom, top, radius, BlockMask, QueryTriggerInteraction.Ignore);
            Transform self = chef != null && chef.Chef != null ? chef.Chef.transform : null;
            Transform other = alsoIgnore != null ? alsoIgnore.transform : null;
            for (int i = 0; i < hits.Length; i++)
            {
                Transform t = hits[i].transform;
                if (self != null && t.IsChildOf(self)) continue;
                if (other != null && t.IsChildOf(other)) continue;
                return false;
            }
            if (KillMask != 0 && Physics.CheckCapsule(bottom - Vector3.up * 0.3f, top, radius, KillMask, QueryTriggerInteraction.Collide))
                return false;
            return true;
        }

        /// <summary>A counter: a worktop, or anything items can be put on.</summary>
        internal static bool IsCounter(Collider collider)
        {
            if (collider == null) return false;
            if (WorktopLayer >= 0 && collider.gameObject.layer == WorktopLayer) return true;
            return collider.GetComponentInParent<AttachStation>() != null;
        }

        /// <summary>A free spot next to <paramref name="centre"/> for <paramref name="mover"/>.</summary>
        internal static bool SpotNear(Vector3 centre, Vector3 forward, ChefState mover, out Vector3 spot)
        {
            forward = Flat(forward);
            Vector3 right = new Vector3(forward.z, 0f, -forward.x);
            Vector3[] offsets =
            {
                forward, right, -right, -forward,
                (forward + right) * 0.8f, (forward - right) * 0.8f, (-forward + right) * 0.8f, (-forward - right) * 0.8f,
                forward * 1.6f, right * 1.6f, -right * 1.6f, -forward * 1.6f
            };
            for (int i = 0; i < offsets.Length; i++)
            {
                Vector3 floor;
                if (Floor(centre + offsets[i] * 1.0f, centre.y, 0.6f, out floor) && Free(mover, floor, null))
                {
                    spot = floor;
                    return true;
                }
            }
            spot = centre;
            return false;
        }

        // --- who can be moved ------------------------------------------------------------------

        internal static bool Movable(ChefState chef)
        {
            if (chef == null || chef.Chef == null || chef.Controls == null) return false;
            PlayerControls controls = chef.Controls;
            if (!controls.enabled || controls.m_bRespawning || controls.IsSuppressed()) return false;
            if (controls.GetCurrentlyInteracting() != null) return false;
            // On the host a chef is only kinematic while something else moves it (cannon, portal).
            if (SpecialNet.IsServer)
            {
                Rigidbody body = chef.Chef.GetComponent<Rigidbody>();
                if (body == null || body.isKinematic) return false;
            }
            return true;
        }

        // --- moving ----------------------------------------------------------------------------

        /// <summary>Host: put the chef at <paramref name="feet"/>.</summary>
        internal static void ServerMove(ChefState chef, Vector3 feet, bool keepRun, float hop)
        {
            if (chef == null || chef.Chef == null) return;
            Transform root = chef.Chef.transform;
            Vector3 from = root.position;
            Rigidbody body = chef.Chef.GetComponent<Rigidbody>();
            Vector3 velocity = body != null ? body.velocity : Vector3.zero;
            root.position = feet;
            if (body != null)
            {
                body.position = feet;
                body.velocity = keepRun ? new Vector3(velocity.x, 0f, velocity.z) : Vector3.zero;
            }
            GroundCast ground = chef.Chef.GetComponent<GroundCast>();
            if (ground != null) ground.ForceUpdateNow();
            if (hop > 0f) StartHop(chef, from, feet, hop);
        }

        /// <summary>Owner machine (not the host): move its own chef now, ahead of the host.</summary>
        internal static void PredictMove(ChefState chef, Vector3 feet, bool keepRun, float hop)
        {
            if (chef == null || chef.Chef == null) return;
            Transform root = chef.Chef.transform;
            Vector3 from = root.position;
            Rigidbody body = chef.Chef.GetComponent<Rigidbody>();
            Vector3 velocity = body != null ? body.velocity : Vector3.zero;
            ClientChefSynchroniser sync = chef.Chef.GetComponent<ClientChefSynchroniser>();
            PositionRecorder recorder = sync != null && RecorderField != null ? RecorderField.GetValue(sync) as PositionRecorder : null;
            if (recorder != null)
            {
                Transform parent = LocalParentMethod != null ? LocalParentMethod.Invoke(sync, null) as Transform : null;
                // Moves the transform without the jump counting as movement in the lag compensation.
                recorder.Teleport(feet, parent);
            }
            else root.position = feet;
            if (body != null)
            {
                body.position = feet;
                body.velocity = keepRun ? new Vector3(velocity.x, 0f, velocity.z) : Vector3.zero;
            }
            GroundCast ground = chef.Chef.GetComponent<GroundCast>();
            if (ground != null) ground.ForceUpdateNow();
            if (sync != null)
                s_windows[chef.Chef.GetInstanceID()] = new Window { From = from, Target = feet, Until = SpecialRuntime.Now + WindowSeconds };
            if (hop > 0f) StartHop(chef, from, feet, hop);
        }

        /// <summary>Owner machine: the host refused, so let the correction take the chef back.</summary>
        internal static void CancelPrediction(ChefState chef)
        {
            if (chef != null && chef.Chef != null) s_windows.Remove(chef.Chef.GetInstanceID());
        }

        /// <summary>
        /// Owner machine, from ClientChefSynchroniser.RunCorrection: true while a predicted move
        /// isn't confirmed by the host yet (the correction is skipped, but its movement history is
        /// still recorded so the lag compensation stays right afterwards).
        /// </summary>
        internal static bool HoldCorrection(ClientChefSynchroniser sync)
        {
            if (s_windows.Count == 0) return false;
            int key = sync.gameObject.GetInstanceID();
            Window window;
            if (!s_windows.TryGetValue(key, out window)) return false;
            Vector3 server = sync.GetServerGlobalPosition();
            float toTarget = Vector3.Distance(server, window.Target);
            float jump = Vector3.Distance(window.From, window.Target);
            if (SpecialRuntime.Now > window.Until || toTarget < Mathf.Min(1.2f, jump * 0.5f))
            {
                s_windows.Remove(key);
                return false;
            }
            PositionRecorder recorder = RecorderField != null ? RecorderField.GetValue(sync) as PositionRecorder : null;
            if (recorder != null && StampField != null)
            {
                Transform parent = LocalParentMethod != null ? LocalParentMethod.Invoke(sync, null) as Transform : null;
                recorder.TakeSample(TimeManager.GetFixedDeltaTime(sync.gameObject), parent, (float)StampField.GetValue(sync));
            }
            return true;
        }

        // --- agreeing on the spot --------------------------------------------------------------

        /// <summary>
        /// Host: do <paramref name="apply"/> with the spot to use. For the host's own chefs that is
        /// <paramref name="hostTarget"/> at once; for a remote chef it waits a moment for the spot
        /// its owner predicted, and uses that one if within <paramref name="trust"/> metres of the
        /// host's and <paramref name="valid"/> (null = any).
        /// </summary>
        internal static void Resolve(ChefState chef, Special special, Vector3 hostTarget, float trust,
            Func<Vector3, bool> valid, Action<Vector3> apply)
        {
            if (chef.Local || chef.Id == 0 || !SpecialNet.Online)
            {
                apply(hostTarget);
                return;
            }
            Pending old;
            if (s_pending.TryGetValue(chef.Id, out old)) Run(old, old.HostTarget);
            s_pending[chef.Id] = new Pending
            {
                Chef = chef, Special = special, HostTarget = hostTarget, Trust = trust, Valid = valid, Apply = apply,
                Until = SpecialRuntime.Now + ProposalWaitSeconds
            };
        }

        /// <summary>Owner machine: tell the host the spot it picked (or that it found none).</summary>
        internal static void Propose(ChefState chef, Special special, bool found, Vector3 target)
        {
            if (SpecialNet.IsServer) return;
            SpecialNet.SendToHost(new SpecialMessage
            {
                Topic = TopicProposal, Chef = chef.Id, I = (int)special, Vector1 = target, D = found ? 1f : 0f
            });
        }

        internal static void OnProposal(SpecialMessage message)
        {
            if (!SpecialNet.IsServer) return;
            Pending pending;
            if (!s_pending.TryGetValue(message.Chef, out pending) || (int)pending.Special != message.I) return;
            s_pending.Remove(message.Chef);
            Vector3 target = pending.HostTarget;
            Vector3 proposed = message.Vector1;
            if (message.D > 0.5f && Vector3.Distance(proposed, pending.HostTarget) <= pending.Trust &&
                (pending.Valid == null || pending.Valid(proposed)))
                target = proposed;
            Run(pending, target);
        }

        private static void Run(Pending pending, Vector3 target)
        {
            try
            {
                pending.Apply(target);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail(pending.Special + ".Apply", error);
            }
        }

        // --- a little visual hop ---------------------------------------------------------------

        private static void StartHop(ChefState chef, Vector3 from, Vector3 to, float height)
        {
            Transform model = chef.Chef.transform.Find("Chef");
            if (model == null) return;
            for (int i = s_hops.Count - 1; i >= 0; i--)
                if (s_hops[i].Model == model) s_hops.RemoveAt(i);
            s_hops.Add(new Hop
            {
                Root = chef.Chef.transform, Model = model, Offset = from - to, Start = SpecialRuntime.Now,
                Duration = 0.3f, Height = height
            });
        }

        /// <summary>Every frame, every machine.</summary>
        internal static void Update()
        {
            float now = SpecialRuntime.Now;
            for (int i = s_hops.Count - 1; i >= 0; i--)
            {
                Hop hop = s_hops[i];
                if (hop.Model == null || hop.Root == null)
                {
                    s_hops.RemoveAt(i);
                    continue;
                }
                float t = (now - hop.Start) / hop.Duration;
                if (t >= 1f)
                {
                    hop.Model.localPosition = Vector3.zero;
                    s_hops.RemoveAt(i);
                    continue;
                }
                // Only the model moves (the chef itself is already there): from the old spot to
                // the new one along an arc.
                hop.Model.position = hop.Root.position + hop.Offset * (1f - t) + Vector3.up * (hop.Height * 4f * t * (1f - t));
            }

            if (s_pending.Count > 0 && SpecialNet.IsServer)
            {
                List<uint> due = null;
                foreach (KeyValuePair<uint, Pending> pair in s_pending)
                    if (now >= pair.Value.Until)
                    {
                        if (due == null) due = new List<uint>();
                        due.Add(pair.Key);
                    }
                if (due != null)
                    foreach (uint id in due)
                    {
                        Pending pending = s_pending[id];
                        s_pending.Remove(id);
                        Run(pending, pending.HostTarget);
                    }
            }

            if (s_windows.Count > 0)
            {
                List<int> stale = null;
                foreach (KeyValuePair<int, Window> pair in s_windows)
                    if (now > pair.Value.Until + 1f)
                    {
                        if (stale == null) stale = new List<int>();
                        stale.Add(pair.Key);
                    }
                if (stale != null)
                    foreach (int key in stale) s_windows.Remove(key);
            }
        }
    }

    /// <summary>
    /// Specials that move chefs around: Vault, Blink, Recall, Swap, Summon, Grapple (actives),
    /// Quick Respawn, Safety Net, Hover, Medic, Portal Pro, Cannoneer, Driver, Lifeguard
    /// (passives) and Fragile (quirk).
    /// </summary>
    internal sealed class TeleportSpecials : ISpecialModule
    {
        private const float BlinkDistance = 3f;
        private const float GrappleRange = 6f;
        private const float RecallSeconds = 20f;
        private const float SafetyNetCooldown = 30f;
        private const float LifeguardRange = 4.5f;
        private const float HoverRecharge = 10f;
        private const float HoverSeconds = 0.6f;
        private const float QuickRespawnTime = 1.5f;
        private const float MedicRespawnTime = 1f;
        internal const float DriverSpeed = 1.5f;
        // The host's view of a remote chef lags its owner's a little: search a bit further there.
        private const float LagMargin = 0.6f;

        private static readonly ushort TopicRecall = (ushort)((int)Special.Recall * 10 + 0);
        private static readonly ushort TopicSafetyNet = (ushort)((int)Special.SafetyNet * 10 + 0);

        private const int RecallPlanted = 1, RecallReturned = 2, RecallMoved = 4;

        private const string FlagKey = "Recall.Flag", FlagUntilKey = "Recall.Until", FlagModeKey = "Recall.Mode";
        private const string HoverUntilKey = "Hover.Until", HoverReadyKey = "Hover.Ready";
        private const string NetReadyKey = "SafetyNet.Ready", CaughtKey = "Teleport.Caught";

        private struct SafePoint
        {
            public Transform Parent;
            public Vector3 Local, World;
            public float Time;
            public bool Set;

            public Vector3 Position
            {
                get { return Parent != null ? Parent.TransformPoint(Local) : World; }
            }
        }

        private sealed class SpawnOverride
        {
            public PlayerRespawnBehaviour Behaviour;
            public Vector3 Location, Spot;
            public Transform Parent;
            public float Until;
        }

        // Host: where each chef last stood safely (latest sample, and the one before it).
        private static readonly Dictionary<uint, SafePoint> s_latest = new Dictionary<uint, SafePoint>();
        private static readonly Dictionary<uint, SafePoint> s_safe = new Dictionary<uint, SafePoint>();
        private static float s_nextSafeSample;

        // Every machine: respawn times as the level had them, respawns sent to a medic, spawn points moved.
        private static readonly Dictionary<int, float> s_baseRespawn = new Dictionary<int, float>();
        private static readonly Dictionary<int, uint> s_medicFor = new Dictionary<int, uint>();
        private static readonly List<SpawnOverride> s_overrides = new List<SpawnOverride>();

        private static float s_nextStatus;

        public void Register()
        {
            SpecialRuntime.RegisterActive(Special.Vault, new ActiveHandler { Execute = VaultExecute, Predict = VaultPredict });
            SpecialRuntime.RegisterActive(Special.Blink, new ActiveHandler { Execute = BlinkExecute, Predict = BlinkPredict });
            SpecialRuntime.RegisterActive(Special.Grapple, new ActiveHandler { Execute = GrappleExecute, Predict = GrapplePredict });
            SpecialRuntime.RegisterActive(Special.Recall, new ActiveHandler
            {
                Execute = RecallExecute, Predict = RecallPredict, OnActivated = RecallActivated
            });
            SpecialRuntime.RegisterActive(Special.Swap, new ActiveHandler { Execute = SwapExecute });
            SpecialRuntime.RegisterActive(Special.Summon, new ActiveHandler { Execute = SummonExecute });

            SpecialNet.Register(Warp.TopicProposal, Warp.OnProposal);
            SpecialNet.Register(TopicRecall, OnRecallMessage);
            SpecialNet.Register(TopicSafetyNet, OnSafetyNetMessage);
            SpecialNet.Register(SpecialNet.Topic.Denied, OnDenied);

            SpecialRuntime.Tick += OnTick;
            SpecialRuntime.LevelEnded += OnLevelEnded;
            SpecialRuntime.LevelStarted += OnLevelEnded;
        }

        private static void OnLevelEnded()
        {
            Warp.Clear();
            s_latest.Clear();
            s_safe.Clear();
            s_baseRespawn.Clear();
            s_medicFor.Clear();
            s_overrides.Clear();
            s_nextSafeSample = 0f;
            CannonState.Clear();
            PortalState.Clear();
        }

        private static void OnDenied(SpecialMessage message)
        {
            ChefState state = SpecialRuntime.StateById(message.Chef);
            if (state == null || !state.Local) return;
            Special special = (Special)message.I;
            if (special == Special.Vault || special == Special.Blink || special == Special.Grapple || special == Special.Recall)
                Warp.CancelPrediction(state);
        }

        private static void OnTick(float dt)
        {
            Warp.Update();
            float now = SpecialRuntime.Now;

            // Recall flags run out on every machine.
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (chef.Data.ContainsKey(FlagUntilKey) && now > chef.Get(FlagUntilKey, 0f))
                {
                    chef.Data.Remove(FlagUntilKey);
                    chef.Data.Remove(FlagKey);
                }
            }

            for (int i = s_overrides.Count - 1; i >= 0; i--)
                if (now > s_overrides[i].Until)
                {
                    RestoreSpawn(s_overrides[i]);
                    s_overrides.RemoveAt(i);
                }

            if (SpecialNet.IsServer && now >= s_nextSafeSample &&
                (SpecialRuntime.AnyHas(Special.SafetyNet) || SpecialRuntime.AnyHas(Special.Lifeguard)))
            {
                s_nextSafeSample = now + 0.1f;
                SampleSafePoints(now);
            }

            if (Time.unscaledTime >= s_nextStatus)
            {
                s_nextStatus = Time.unscaledTime + 0.25f;
                UpdateStatus(now);
            }
        }

        /// <summary>The HUD line under a local chef's active: flag, hover and safety net timers.</summary>
        private static void UpdateStatus(float now)
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (!chef.Local) continue;
                string text = null;
                if (chef.Has(Special.Recall) && chef.Data.ContainsKey(FlagUntilKey))
                    text = Join(text, "Flag " + Mathf.CeilToInt(chef.Get(FlagUntilKey, 0f) - now) + "s");
                if (chef.Has(Special.Hover) && now < chef.Get(HoverReadyKey, 0f))
                    text = Join(text, "Hover " + Mathf.CeilToInt(chef.Get(HoverReadyKey, 0f) - now) + "s");
                if (chef.Has(Special.SafetyNet) && now < chef.Get(NetReadyKey, 0f))
                    text = Join(text, "Net " + Mathf.CeilToInt(chef.Get(NetReadyKey, 0f) - now) + "s");
                chef.SetStatus("Teleport", text);
            }
        }

        private static string Join(string a, string b)
        {
            return string.IsNullOrEmpty(a) ? b : a + "  " + b;
        }

        // --- Vault -----------------------------------------------------------------------------

        /// <summary>The free floor beyond the counter the chef faces.</summary>
        private static bool FindVault(ChefState chef, Vector3 position, Vector3 forward, float margin, out Vector3 landing)
        {
            Warp.InitMasks();
            landing = position;
            forward = Warp.Flat(forward);
            RaycastHit hit;
            if (!Physics.Raycast(position + Vector3.up * 0.5f, forward, out hit, 1.3f + margin, Warp.BlockMask, QueryTriggerInteraction.Ignore))
                return false;
            if (hit.collider.transform.IsChildOf(chef.Chef.transform) || !Warp.IsCounter(hit.collider)) return false;
            // Something low enough to hop over, with nothing above it on the way.
            float top = hit.collider.bounds.max.y;
            if (top - position.y > 1.6f) return false;
            float arc = Mathf.Max(top, position.y + 0.5f) + 0.35f;
            for (float d = hit.distance + 0.45f; d <= hit.distance + 3.2f; d += 0.15f)
            {
                Vector3 floor;
                if (!Warp.Floor(position + forward * d, position.y, 0.6f, out floor) || !Warp.Free(chef, floor, null)) continue;
                Vector3 high = new Vector3(position.x, arc, position.z);
                if (Physics.Raycast(high, forward, d, Warp.BlockMask & ~PlayersMask(), QueryTriggerInteraction.Ignore)) return false;
                landing = floor;
                return true;
            }
            return false;
        }

        private static int s_players = -2;

        private static int PlayersMask()
        {
            if (s_players == -2)
            {
                int layer = LayerMask.NameToLayer("Players");
                s_players = layer >= 0 ? 1 << layer : 0;
            }
            return s_players;
        }

        private static bool VaultExecute(ChefState chef)
        {
            if (!Warp.Movable(chef)) return false;
            Vector3 landing = chef.Position;
            if (!FindVault(chef, chef.Position, chef.Forward, chef.Local ? 0f : LagMargin, out landing)) return false;
            Warp.Resolve(chef, Special.Vault, landing, 2.5f, delegate(Vector3 p) { return Warp.Free(chef, p, null); },
                delegate(Vector3 p) { Warp.ServerMove(chef, p, false, 0.9f); });
            return true;
        }

        private static void VaultPredict(ChefState chef)
        {
            Vector3 landing = chef.Position;
            bool found = Warp.Movable(chef) && FindVault(chef, chef.Position, chef.Forward, 0f, out landing);
            if (!found) landing = chef.Position;
            else Warp.PredictMove(chef, landing, false, 0.9f);
            Warp.Propose(chef, Special.Vault, found, landing);
        }

        // --- Blink -----------------------------------------------------------------------------

        /// <summary>As far as 3 m ahead as the chef can go: stops before anything solid or an edge.</summary>
        private static bool FindBlink(ChefState chef, Vector3 position, Vector3 forward, float minimum, out Vector3 landing)
        {
            forward = Warp.Flat(forward);
            landing = position;
            float reached = 0f;
            float refY = position.y;
            for (float d = 0.2f; d <= BlinkDistance + 0.01f; d += 0.2f)
            {
                Vector3 floor;
                if (!Warp.Floor(position + forward * d, refY, 0.45f, out floor) || !Warp.Free(chef, floor, null)) break;
                landing = floor;
                refY = floor.y;
                reached = d;
            }
            return reached >= minimum;
        }

        private static bool BlinkExecute(ChefState chef)
        {
            if (!Warp.Movable(chef)) return false;
            Vector3 landing = chef.Position;
            if (!FindBlink(chef, chef.Position, chef.Forward, chef.Local ? 0.6f : 0.4f, out landing)) return false;
            Warp.Resolve(chef, Special.Blink, landing, 2f, delegate(Vector3 p) { return Warp.Free(chef, p, null); },
                delegate(Vector3 p) { Warp.ServerMove(chef, p, true, 0f); });
            return true;
        }

        private static void BlinkPredict(ChefState chef)
        {
            Vector3 landing = chef.Position;
            bool found = Warp.Movable(chef) && FindBlink(chef, chef.Position, chef.Forward, 0.6f, out landing);
            if (!found) landing = chef.Position;
            else Warp.PredictMove(chef, landing, true, 0f);
            Warp.Propose(chef, Special.Blink, found, landing);
        }

        // --- Grapple ---------------------------------------------------------------------------

        /// <summary>The floor just in front of the counter the chef faces, up to 6 m away.</summary>
        private static bool FindGrapple(ChefState chef, Vector3 position, Vector3 forward, float margin, out Vector3 landing)
        {
            Warp.InitMasks();
            landing = position;
            forward = Warp.Flat(forward);
            RaycastHit hit;
            if (!Physics.Raycast(position + Vector3.up * 0.5f, forward, out hit, GrappleRange + margin, Warp.BlockMask & ~PlayersMask(),
                    QueryTriggerInteraction.Ignore))
                return false;
            if (hit.collider.transform.IsChildOf(chef.Chef.transform) || !Warp.IsCounter(hit.collider) || hit.distance < 1.3f)
                return false;
            // Back off from the counter until there's floor to stand on (gaps in between are flown over).
            for (float d = hit.distance - 0.45f; d >= 0.9f; d -= 0.15f)
            {
                Vector3 floor;
                if (Warp.Floor(position + forward * d, position.y, 1f, out floor) && Warp.Free(chef, floor, null))
                {
                    landing = floor;
                    return true;
                }
            }
            return false;
        }

        private static bool GrappleExecute(ChefState chef)
        {
            if (!Warp.Movable(chef)) return false;
            Vector3 landing = chef.Position;
            if (!FindGrapple(chef, chef.Position, chef.Forward, chef.Local ? 0f : LagMargin, out landing)) return false;
            Warp.Resolve(chef, Special.Grapple, landing, 2.5f, delegate(Vector3 p) { return Warp.Free(chef, p, null); },
                delegate(Vector3 p) { Warp.ServerMove(chef, p, false, 0.35f); });
            return true;
        }

        private static void GrapplePredict(ChefState chef)
        {
            Vector3 landing = chef.Position;
            bool found = Warp.Movable(chef) && FindGrapple(chef, chef.Position, chef.Forward, 0f, out landing);
            if (!found) landing = chef.Position;
            else Warp.PredictMove(chef, landing, false, 0.35f);
            Warp.Propose(chef, Special.Grapple, found, landing);
        }

        // --- Recall ----------------------------------------------------------------------------
        // The host decides plant or return and tells everyone first (TopicRecall), so that when the
        // activation is confirmed every machine knows which it was: a plant puts the cooldown back
        // to 0.5 s, so the real cooldown only starts with the return.

        private static bool HasFlag(ChefState chef, float spare)
        {
            return chef.Data.ContainsKey(FlagKey) && chef.Get(FlagUntilKey, 0f) > SpecialRuntime.Now + spare;
        }

        private static bool RecallExecute(ChefState chef)
        {
            if (!Warp.Movable(chef)) return false;
            if (HasFlag(chef, 0f))
            {
                Vector3 flag = chef.Get(FlagKey, chef.Position);
                SendRecall(chef, RecallReturned, flag);
                Warp.ServerMove(chef, flag, false, 0f);
                return true;
            }
            Vector3 here = chef.Position;
            SendRecall(chef, RecallPlanted, here);
            // The owner's own position is more exact than the host's view of it.
            Warp.Resolve(chef, Special.Recall, here, 2.5f, null, delegate(Vector3 p)
            {
                if ((p - here).sqrMagnitude > 1e-4f) SendRecall(chef, RecallMoved, p);
            });
            return true;
        }

        private static void RecallPredict(ChefState chef)
        {
            // Only predict a return that the host will surely still see as one.
            if (HasFlag(chef, 0.4f))
            {
                if (Warp.Movable(chef)) Warp.PredictMove(chef, chef.Get(FlagKey, chef.Position), false, 0f);
                return;
            }
            Warp.Propose(chef, Special.Recall, true, chef.Position);
        }

        private static void SendRecall(ChefState chef, int what, Vector3 position)
        {
            SpecialNet.SendToAll(new SpecialMessage { Topic = TopicRecall, Chef = chef.Id, I = what, Vector1 = position });
        }

        private static void OnRecallMessage(SpecialMessage message)
        {
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            if (chef == null) return;
            switch (message.I)
            {
                case RecallPlanted:
                    chef.Set(FlagKey, message.Vector1);
                    chef.Set(FlagUntilKey, SpecialRuntime.Now + RecallSeconds);
                    chef.Set(FlagModeKey, RecallPlanted);
                    break;
                case RecallMoved:
                    if (chef.Data.ContainsKey(FlagKey)) chef.Set(FlagKey, message.Vector1);
                    break;
                case RecallReturned:
                    chef.Data.Remove(FlagKey);
                    chef.Data.Remove(FlagUntilKey);
                    chef.Set(FlagModeKey, RecallReturned);
                    break;
            }
        }

        private static void RecallActivated(ChefState chef)
        {
            if (chef.Get(FlagModeKey, 0) != RecallPlanted) return;
            // Planting is free: only a short pause so a double tap doesn't return at once.
            chef.CooldownUntil = SpecialRuntime.Now + 0.5f;
            chef.CooldownTotal = 0.5f;
            chef.Set(FlagModeKey, 0);
        }

        // --- Swap and Summon (host only; the moved chefs' owners are snapped by their correction) ---

        private static ChefState PickTeammate(ChefState chef)
        {
            ChefState faced = null, nearest = null;
            float facedDistance = float.MaxValue, nearestDistance = float.MaxValue;
            Vector3 forward = Warp.Flat(chef.Forward);
            foreach (ChefState other in SpecialRuntime.Teammates(chef))
            {
                if (!Warp.Movable(other)) continue;
                Vector3 offset = other.Position - chef.Position;
                offset.y = 0f;
                float distance = offset.magnitude;
                if (distance < nearestDistance)
                {
                    nearest = other;
                    nearestDistance = distance;
                }
                if (distance > 0.01f && Vector3.Angle(forward, offset) <= 35f && distance < facedDistance)
                {
                    faced = other;
                    facedDistance = distance;
                }
            }
            return faced ?? nearest;
        }

        private static bool SwapExecute(ChefState chef)
        {
            if (!Warp.Movable(chef)) return false;
            ChefState mate = PickTeammate(chef);
            if (mate == null) return false;
            Vector3 mine = chef.Position, theirs = mate.Position;
            if ((mine - theirs).sqrMagnitude < 0.25f) return false;
            Warp.ServerMove(chef, theirs, false, 0f);
            Warp.ServerMove(mate, mine, false, 0f);
            return true;
        }

        private static bool SummonExecute(ChefState chef)
        {
            if (!Warp.Movable(chef)) return false;
            ChefState mate = PickTeammate(chef);
            if (mate == null) return false;
            Vector3 offset = mate.Position - chef.Position;
            offset.y = 0f;
            if (offset.magnitude < 1.6f) return false;
            Vector3 spot;
            if (!Warp.SpotNear(chef.Position, chef.Forward, mate, out spot)) return false;
            Warp.ServerMove(mate, spot, false, 0f);
            return true;
        }

        // --- Safety Net and Lifeguard (host) ----------------------------------------------------

        private static void SampleSafePoints(float now)
        {
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
            {
                ChefState chef = chefs[i];
                if (chef.Id == 0 || chef.Controls == null || chef.Chef == null) continue;
                PlayerControls controls = chef.Controls;
                if (!controls.enabled || controls.m_bRespawning) continue;
                Collider ground = controls.GroundCollider;
                if (ground == null) continue;
                Vector3 position = chef.Position;
                SafePoint latest;
                // Keep the point from a moment ago: the latest one may be right at the edge.
                if (s_latest.TryGetValue(chef.Id, out latest) && latest.Set && now - latest.Time >= 0.3f)
                    s_safe[chef.Id] = latest;
                if (!latest.Set || now - latest.Time >= 0.3f)
                {
                    Transform parent = ground.transform;
                    s_latest[chef.Id] = new SafePoint
                    {
                        Parent = parent, Local = parent.InverseTransformPoint(position), World = position, Time = now, Set = true
                    };
                    if (!s_safe.ContainsKey(chef.Id)) s_safe[chef.Id] = s_latest[chef.Id];
                }
            }
        }

        private static readonly FieldInfo RespawnColliderField =
            AccessTools.Field(typeof(ServerRespawnCollider), "m_RespawnCollider");

        /// <summary>Host: a chef fell into a pit or water. True if Safety Net or a Lifeguard caught it.</summary>
        internal static bool TryCatchFall(ServerRespawnCollider volume, GameObject fallen)
        {
            PlayerControls controls = fallen.GetComponent<PlayerControls>();
            if (controls == null || controls.m_bRespawning) return false;
            RespawnCollider.RespawnType type = volume.Type;
            if (type != RespawnCollider.RespawnType.FallDeath && type != RespawnCollider.RespawnType.Drowning) return false;
            RespawnCollider settings = RespawnColliderField != null ? RespawnColliderField.GetValue(volume) as RespawnCollider : null;
            if (settings == null || (settings.m_respawnFilter.value & (1 << fallen.layer)) == 0) return false;
            ChefState chef = SpecialRuntime.StateOf(fallen);
            if (chef == null) return false;
            float now = SpecialRuntime.Now;
            // Never twice in a row: a spot that drops the chef again lets it respawn normally.
            if (now - chef.Get(CaughtKey, -100f) < 2f) return false;
            SafePoint safe, latest;
            if (!s_safe.TryGetValue(chef.Id, out safe) || !safe.Set) return false;
            Vector3 back = safe.Position;
            // The last grounded point is nearer where the chef stepped off; use it while it is
            // still floor (it stops there, the fall's speed is dropped).
            Vector3 floor;
            if (s_latest.TryGetValue(chef.Id, out latest) && latest.Set &&
                Warp.Floor(latest.Position, latest.Position.y, 0.3f, out floor) && Warp.Free(chef, floor, null))
                back = floor;

            bool net = chef.Has(Special.SafetyNet) && now >= chef.Get(NetReadyKey, 0f);
            bool guarded = false;
            if (!net)
            {
                foreach (ChefState mate in SpecialRuntime.Teammates(chef))
                {
                    if (!mate.Has(Special.Lifeguard) || mate.Controls == null || mate.Controls.m_bRespawning) continue;
                    Vector3 a = mate.Position, b = chef.Position, c = back;
                    a.y = b.y = c.y = 0f;
                    if (Vector3.Distance(a, b) <= LifeguardRange || Vector3.Distance(a, c) <= LifeguardRange)
                    {
                        guarded = true;
                        break;
                    }
                }
            }
            if (!net && !guarded) return false;

            chef.Set(CaughtKey, now);
            Warp.ServerMove(chef, back, false, 0f);
            if (net)
            {
                chef.Set(NetReadyKey, now + SafetyNetCooldown);
                SpecialNet.SendToAll(new SpecialMessage { Topic = TopicSafetyNet, Chef = chef.Id, A = SafetyNetCooldown });
            }
            return true;
        }

        private static void OnSafetyNetMessage(SpecialMessage message)
        {
            ChefState chef = SpecialRuntime.StateById(message.Chef);
            if (chef != null && !SpecialNet.IsServer) chef.Set(NetReadyKey, SpecialRuntime.Now + message.A);
        }

        // --- respawn time and place (every machine runs the respawn itself) ---------------------

        private static readonly FieldInfo BehaviourField =
            AccessTools.Field(typeof(ClientPlayerRespawnBehaviour), "m_PlayerRespawnBehaviour");

        /// <summary>A chef starts respawning on this machine: set how long it takes.</summary>
        internal static void OnRespawnBegin(ClientPlayerRespawnBehaviour client)
        {
            PlayerRespawnBehaviour behaviour = BehaviourField != null ? BehaviourField.GetValue(client) as PlayerRespawnBehaviour : null;
            if (behaviour == null) return;
            int key = behaviour.GetInstanceID();
            for (int i = s_overrides.Count - 1; i >= 0; i--)
                if (s_overrides[i].Behaviour == behaviour)
                {
                    RestoreSpawn(s_overrides[i]);
                    s_overrides.RemoveAt(i);
                }
            float baseTime;
            if (!s_baseRespawn.TryGetValue(key, out baseTime))
            {
                baseTime = behaviour.m_respawnTime;
                s_baseRespawn[key] = baseTime;
            }
            s_medicFor.Remove(key);
            float time = baseTime;
            ChefState chef = SpecialRuntime.StateOf(client.gameObject);
            if (chef != null)
            {
                if (chef.Has(Special.QuickRespawn)) time = Mathf.Min(time, QuickRespawnTime);
                ChefState medic = FindMedic(chef);
                if (medic != null)
                {
                    time = Mathf.Min(time, MedicRespawnTime);
                    s_medicFor[key] = medic.Id;
                }
                if (chef.Has(Special.Fragile)) time *= 2f;
            }
            behaviour.m_respawnTime = time;
        }

        private static ChefState FindMedic(ChefState chef)
        {
            ChefState best = null;
            foreach (ChefState mate in SpecialRuntime.Teammates(chef))
            {
                if (!mate.Has(Special.Medic) || mate.Controls == null || mate.Controls.m_bRespawning) continue;
                // The same pick on every machine.
                if (best == null || mate.Id < best.Id) best = mate;
            }
            return best;
        }

        /// <summary>A chef is about to reappear on this machine: next to its medic, if it has one.</summary>
        internal static void OnSpawn(ClientPlayerRespawnBehaviour client)
        {
            PlayerRespawnBehaviour behaviour = BehaviourField != null ? BehaviourField.GetValue(client) as PlayerRespawnBehaviour : null;
            if (behaviour == null) return;
            int key = behaviour.GetInstanceID();
            uint medicId;
            if (!s_medicFor.TryGetValue(key, out medicId)) return;
            s_medicFor.Remove(key);
            ChefState medic = SpecialRuntime.StateById(medicId);
            ChefState chef = SpecialRuntime.StateOf(client.gameObject);
            if (medic == null || medic.Chef == null || medic.Controls == null || medic.Controls.m_bRespawning) return;
            Vector3 spot;
            // World axes rather than the medic's facing, so every machine picks the same spot.
            if (!Warp.SpotNear(medic.Position, Vector3.forward, chef, out spot)) return;
            s_overrides.Add(new SpawnOverride
            {
                Behaviour = behaviour, Location = behaviour.m_startLocation, Parent = behaviour.m_startParent, Spot = spot,
                Until = SpecialRuntime.Now + 4f
            });
            // The spawn routine reads these right after this; put back a few seconds later.
            behaviour.m_startParent = null;
            behaviour.m_startLocation = spot;
        }

        private static void RestoreSpawn(SpawnOverride entry)
        {
            PlayerRespawnBehaviour behaviour = entry.Behaviour;
            // Unless the level moved the respawn point meanwhile.
            if (behaviour == null || behaviour.m_startParent != null || behaviour.m_startLocation != entry.Spot) return;
            behaviour.m_startParent = entry.Parent;
            behaviour.m_startLocation = entry.Location;
        }

        // --- Hover (every machine that simulates the chef: the host, and the owner) -------------

        /// <summary>From the movement update: is the chef hovering this frame?</summary>
        internal static bool UpdateHover(PlayerControls controls, bool dashing)
        {
            ChefState chef = SpecialRuntime.StateOf(controls.gameObject);
            if (chef == null || !chef.Has(Special.Hover)) return false;
            Rigidbody body = controls.GetComponent<Rigidbody>();
            if (body == null || body.isKinematic) return false;
            float now = SpecialRuntime.Now;
            float until = chef.Get(HoverUntilKey, 0f);
            if (controls.m_bRespawning || controls.GroundCollider != null)
            {
                if (until > 0f) chef.Set(HoverUntilKey, 0f);
                return false;
            }
            if (now < until) return true;
            if (until > 0f)
            {
                // Ran out over the gap: falls as usual.
                chef.Set(HoverUntilKey, 0f);
                return false;
            }
            if (!dashing || now < chef.Get(HoverReadyKey, 0f)) return false;
            // Only over a real gap, not a seam or a step.
            Warp.InitMasks();
            if (Physics.Raycast(controls.transform.position + Vector3.up * 0.2f, Vector3.down, 1.2f, Warp.GroundMask,
                    QueryTriggerInteraction.Ignore))
                return false;
            chef.Set(HoverUntilKey, now + HoverSeconds);
            chef.Set(HoverReadyKey, now + HoverRecharge);
            return true;
        }

        /// <summary>After the movement update: no falling, and keep going across once the dash ends.</summary>
        internal static void HoverCarry(PlayerControls controls, bool dashing)
        {
            Rigidbody body = controls.GetComponent<Rigidbody>();
            if (body == null) return;
            Vector3 velocity = body.velocity;
            if (velocity.y < 0f) velocity.y = 0f;
            if (!dashing && controls.Movement != null)
            {
                Vector3 forward = Warp.Flat(controls.transform.forward);
                float along = velocity.x * forward.x + velocity.z * forward.z;
                float minimum = controls.Movement.RunSpeed;
                if (along < minimum)
                {
                    velocity.x += forward.x * (minimum - along);
                    velocity.z += forward.z * (minimum - along);
                }
            }
            body.velocity = velocity;
        }

        // --- Driver (host: vehicles only move on the host) -------------------------------------

        internal static bool IsDriver(PlayerControls.ControlSchemeData scheme)
        {
            if (scheme == null) return false;
            IList<ChefState> chefs = SpecialRuntime.Chefs;
            for (int i = 0; i < chefs.Count; i++)
                if (chefs[i].Controls != null && chefs[i].Controls.ControlScheme == scheme) return chefs[i].Has(Special.Driver);
            return false;
        }
    }

    /// <summary>Owner machine: no correction while a predicted move waits for the host.</summary>
    [HarmonyPatch(typeof(ClientChefSynchroniser), "RunCorrection")]
    internal static class WarpCorrectionPatch
    {
        private static bool Prefix(ClientChefSynchroniser __instance)
        {
            try
            {
                if (!SpecialRuntime.InLevel) return true;
                return !Warp.HoldCorrection(__instance);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Teleport.RunCorrection", error);
                return true;
            }
        }
    }

    /// <summary>Host: Safety Net and Lifeguard catch a chef falling into a pit or water.</summary>
    [HarmonyPatch(typeof(ServerRespawnCollider), "ObjectAdded")]
    internal static class SafetyNetPatch
    {
        private static bool Prefix(ServerRespawnCollider __instance, GameObject _gameObject)
        {
            try
            {
                if (!SpecialRuntime.InLevel || _gameObject == null) return true;
                if (!SpecialRuntime.AnyHas(Special.SafetyNet) && !SpecialRuntime.AnyHas(Special.Lifeguard)) return true;
                return !TeleportSpecials.TryCatchFall(__instance, _gameObject);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("SafetyNet.ObjectAdded", error);
                return true;
            }
        }
    }

    /// <summary>Every machine: Quick Respawn, Fragile and Medic change the respawn time.</summary>
    [HarmonyPatch(typeof(ClientPlayerRespawnBehaviour), "ApplyServerEvent")]
    internal static class RespawnTimePatch
    {
        private static void Prefix(ClientPlayerRespawnBehaviour __instance, Serialisable serialisable)
        {
            try
            {
                if (!SpecialRuntime.InLevel) return;
                RespawnMessage message = serialisable as RespawnMessage;
                if (message == null || message.m_Phase != RespawnMessage.Phase.Begin || __instance.IsRespawning) return;
                TeleportSpecials.OnRespawnBegin(__instance);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("QuickRespawn.ApplyServerEvent", error);
            }
        }
    }

    /// <summary>Every machine: a chef with a Medic teammate reappears next to the medic.</summary>
    [HarmonyPatch(typeof(ClientPlayerRespawnBehaviour), "RunSpawnAtSpawnPoint")]
    internal static class MedicSpawnPatch
    {
        private static void Prefix(ClientPlayerRespawnBehaviour __instance)
        {
            try
            {
                if (!SpecialRuntime.InLevel || !SpecialRuntime.AnyHas(Special.Medic)) return;
                TeleportSpecials.OnSpawn(__instance);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Medic.RunSpawnAtSpawnPoint", error);
            }
        }
    }

    /// <summary>Hover: decides each movement update whether the chef is carried over a gap.</summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "Update_Movement")]
    internal static class HoverMovePatch
    {
        private static readonly FieldInfo ControlsField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_controls");
        private static readonly FieldInfo DashTimerField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_dashTimer");

        // The movement update running now is hovering (one chef at a time, main thread).
        internal static ClientPlayerControlsImpl_Default Hovering;
        private static PlayerControls _controls;

        private static void Prefix(ClientPlayerControlsImpl_Default __instance)
        {
            Hovering = null;
            _controls = null;
            try
            {
                if (!SpecialRuntime.InLevel || ControlsField == null || DashTimerField == null) return;
                // Every chef, every frame: skip the reflection unless someone can hover.
                if (!SpecialRuntime.AnyHas(Special.Hover)) return;
                PlayerControls controls = ControlsField.GetValue(__instance) as PlayerControls;
                if (controls == null) return;
                bool dashing = (float)DashTimerField.GetValue(__instance) > 0f;
                if (TeleportSpecials.UpdateHover(controls, dashing))
                {
                    Hovering = __instance;
                    _controls = controls;
                }
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Hover.Update_Movement", error);
            }
        }

        private static void Postfix(ClientPlayerControlsImpl_Default __instance)
        {
            if (Hovering != __instance || _controls == null) return;
            try
            {
                TeleportSpecials.HoverCarry(_controls, (float)DashTimerField.GetValue(__instance) > 0f);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Hover.Carry", error);
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            Hovering = null;
            _controls = null;
        }
    }

    /// <summary>Hover: no gravity while carried over the gap.</summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "ApplyGravityForce")]
    internal static class HoverGravityPatch
    {
        private static bool Prefix(ClientPlayerControlsImpl_Default __instance)
        {
            // Only ever set for a Hover chef in a level (HoverMovePatch).
            return HoverMovePatch.Hovering == null || HoverMovePatch.Hovering != __instance;
        }
    }

    /// <summary>Portal Pro: which chef each portal exit is bringing out (host).</summary>
    internal static class PortalState
    {
        internal static readonly Dictionary<int, GameObject> Arriving = new Dictionary<int, GameObject>();

        internal static void Clear()
        {
            Arriving.Clear();
        }
    }

    [HarmonyPatch(typeof(ServerTeleportalPlayerReceiver), "TeleportRoutine")]
    internal static class PortalArrivePatch
    {
        private static void Prefix(ServerTeleportalPlayerReceiver __instance, ITeleportable _object)
        {
            try
            {
                if (!SpecialRuntime.InLevel || !SpecialRuntime.AnyHas(Special.PortalPro)) return;
                Component component = _object as Component;
                if (component == null) return;
                PortalState.Arriving[__instance.GetInstanceID()] = component.gameObject;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("PortalPro.TeleportRoutine", error);
            }
        }
    }

    /// <summary>Portal Pro: out of the portal, a 2 s burst of speed with dashes that don't recharge.</summary>
    [HarmonyPatch(typeof(ServerTeleportalPlayerReceiver), "OnAnimationFinished")]
    internal static class PortalExitPatch
    {
        private static void Postfix(ServerTeleportalPlayerReceiver __instance, string _animName)
        {
            try
            {
                if (!SpecialRuntime.InLevel || _animName != "Receive") return;
                GameObject chefObject;
                int key = __instance.GetInstanceID();
                if (!PortalState.Arriving.TryGetValue(key, out chefObject)) return;
                PortalState.Arriving.Remove(key);
                ChefState chef = SpecialRuntime.StateOf(chefObject);
                if (chef == null || !chef.Has(Special.PortalPro)) return;
                SpecialRuntime.BroadcastBuff(chef, BuffId.NoDashCooldown, 2f, 1f, chef);
                SpecialRuntime.BroadcastBuff(chef, BuffId.RunSpeed, 2f, 0.4f, chef);
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("PortalPro.OnAnimationFinished", error);
            }
        }
    }

    /// <summary>Cannoneer: cannons the host fires early, whose late launch trigger is then ignored.</summary>
    internal static class CannonState
    {
        internal static readonly Dictionary<int, float> Swallow = new Dictionary<int, float>();
        internal static bool Firing;

        internal static void Clear()
        {
            Swallow.Clear();
            Firing = false;
        }
    }

    /// <summary>
    /// Cannoneer (host): when the button is pressed with a Cannoneer chef loaded and the cannon
    /// ready, it fires at once instead of after the fuse.
    /// </summary>
    [HarmonyPatch(typeof(ServerCannon), "ButtonInteractCallback")]
    internal static class CannonFirePatch
    {
        private static readonly FieldInfo LoadedField = AccessTools.Field(typeof(ServerCannon), "m_loadedObject");
        private static readonly FieldInfo FlyingField = AccessTools.Field(typeof(ServerCannon), "m_flying");
        private static readonly FieldInfo ReadyField = AccessTools.Field(typeof(ServerCannon), "m_readyToLaunch");
        private static readonly FieldInfo CannonField = AccessTools.Field(typeof(ServerCannon), "m_cannon");

        private static void Postfix(ServerCannon __instance)
        {
            try
            {
                if (!SpecialRuntime.InLevel || LoadedField == null || FlyingField == null || ReadyField == null || CannonField == null) return;
                GameObject loaded = LoadedField.GetValue(__instance) as GameObject;
                if (loaded == null || !SpecialRuntime.Has(loaded, Special.Cannoneer)) return;
                if ((bool)FlyingField.GetValue(__instance) || !(bool)ReadyField.GetValue(__instance)) return;
                Cannon cannon = CannonField.GetValue(__instance) as Cannon;
                if (cannon == null || string.IsNullOrEmpty(cannon.m_launchTrigger)) return;
                CannonState.Firing = true;
                try
                {
                    __instance.OnTrigger(cannon.m_launchTrigger);
                }
                finally
                {
                    CannonState.Firing = false;
                }
                if ((bool)FlyingField.GetValue(__instance))
                    CannonState.Swallow[__instance.GetInstanceID()] = SpecialRuntime.Now + 4f;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Cannoneer.ButtonInteractCallback", error);
            }
        }
    }

    /// <summary>Cannoneer (host): the fuse's own launch trigger, arriving later, is dropped once.</summary>
    [HarmonyPatch(typeof(ServerCannon), "OnTrigger")]
    internal static class CannonTriggerPatch
    {
        private static readonly FieldInfo CannonField = AccessTools.Field(typeof(ServerCannon), "m_cannon");

        private static bool Prefix(ServerCannon __instance, string _trigger)
        {
            try
            {
                if (!SpecialRuntime.InLevel || CannonState.Firing || CannonState.Swallow.Count == 0 || CannonField == null) return true;
                int key = __instance.GetInstanceID();
                float until;
                if (!CannonState.Swallow.TryGetValue(key, out until)) return true;
                Cannon cannon = CannonField.GetValue(__instance) as Cannon;
                if (cannon == null || _trigger != cannon.m_launchTrigger) return true;
                CannonState.Swallow.Remove(key);
                return SpecialRuntime.Now > until;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Cannoneer.OnTrigger", error);
                return true;
            }
        }
    }

    /// <summary>Driver (host): carts and platforms steered by a Driver move 50% faster.</summary>
    [HarmonyPatch(typeof(ServerPilotMovement), "Update_Movement")]
    internal static class DriverPatch
    {
        private static readonly FieldInfo SchemeField = AccessTools.Field(typeof(ServerPilotMovement), "m_controlScheme");
        private static readonly FieldInfo PilotField = AccessTools.Field(typeof(ServerPilotMovement), "m_pilotMovement");

        private static PilotMovement _changed;
        private static float _speed;

        private static void Prefix(ServerPilotMovement __instance)
        {
            _changed = null;
            try
            {
                if (!SpecialRuntime.InLevel || SchemeField == null || PilotField == null) return;
                if (!SpecialRuntime.AnyHas(Special.Driver)) return;
                var scheme = SchemeField.GetValue(__instance) as PlayerControls.ControlSchemeData;
                if (!TeleportSpecials.IsDriver(scheme)) return;
                PilotMovement pilot = PilotField.GetValue(__instance) as PilotMovement;
                if (pilot == null) return;
                _changed = pilot;
                _speed = pilot.MoveSpeed;
                pilot.MoveSpeed *= TeleportSpecials.DriverSpeed;
            }
            catch (Exception error)
            {
                SpecialRuntime.Fail("Driver.Update_Movement", error);
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            if (_changed == null) return;
            _changed.MoveSpeed = _speed;
            _changed = null;
        }
    }
}
