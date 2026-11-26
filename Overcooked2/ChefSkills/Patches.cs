using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ChefSkills
{
    /// <summary>Per-level bookkeeping shared by the gameplay patches.</summary>
    internal static class LevelState
    {
        // Pot/pan/mixing bowl (GameObject instance ID) -> the chef who last filled or placed it.
        internal static readonly Dictionary<int, GameObject> ContainerOwners = new Dictionary<int, GameObject>();

        // Chef -> fractional chop strokes carried over between chops, kept per chef (not per
        // item) so the leftover of a finished item counts on the next one: one table for the
        // host's real progress and one for the progress bar every machine draws. Both see the
        // same chops, so they stay in step.
        internal static readonly Dictionary<GameObject, float> ChopCredit = new Dictionary<GameObject, float>();
        internal static readonly Dictionary<GameObject, float> ClientChopCredit = new Dictionary<GameObject, float>();

        // Above zero while a special adds strokes of its own (ECHO KNIFE, LUCKY CHARM, DASH-CHOP,
        // MULTITASK): those count one for one, and neither use nor add CHOPPING credit.
        internal static int RawStrokes;

        // The chef whose placement is being handled right now on the server (null otherwise).
        internal static GameObject CurrentPlacer;

        internal static void Reset()
        {
            ContainerOwners.Clear();
            ChopCredit.Clear();
            ClientChopCredit.Clear();
            RawStrokes = 0;
            CurrentPlacer = null;
            StationFx.Reset();
        }

        /// <summary>
        /// How many chop strokes this chop is worth: the chopper's multiplier added to what was
        /// left over from their last chop (on any item), e.g. 1.36 counts 1, then 1, then 2...
        /// </summary>
        internal static int Strokes(Dictionary<GameObject, float> credits, GameObject chef, float multiplier)
        {
            if (chef == null) return Mathf.Max(0, Mathf.RoundToInt(multiplier));
            float credit;
            credits.TryGetValue(chef, out credit);
            credit += multiplier;
            int strokes = (int)credit;
            credits[chef] = credit - strokes;
            return strokes;
        }

        /// <summary>
        /// Host: a chop that a special turned into something else (ZEN, LUCKY KNIFE finishing the
        /// item). Every machine's progress bar still counted it, so the host's credit counts it too.
        /// </summary>
        internal static void SkipChop(GameObject chef)
        {
            if (chef == null) return;
            float multiplier = Plugin.Skills.Multiplier(chef, Skill.Chopping);
            if (!Mathf.Approximately(multiplier, 1f)) Strokes(ChopCredit, chef, multiplier);
        }

        internal static GameObject OwnerOf(GameObject container)
        {
            GameObject chef;
            return container != null && ContainerOwners.TryGetValue(container.GetInstanceID(), out chef) ? chef : null;
        }

        /// <summary>
        /// Time to add to a pot or bowl this frame: COOKING scales it until the food is done, then
        /// SLOW BURN stretches the time it takes to burn (or over-mix).
        /// </summary>
        internal static float Scale(GameObject container, float deltaTime, float progress, float doneAt, bool spoiled)
        {
            if (!Plugin.Enabled.Value) return deltaTime;
            GameObject chef = OwnerOf(container);
            if (chef == null) return deltaTime;
            Skill skill = progress < doneAt ? Skill.Cooking : Skill.SlowBurn;
            float multiplier = Plugin.Skills.Multiplier(chef, skill);
            if (deltaTime > 0f && !spoiled && !Mathf.Approximately(multiplier, 1f))
            {
                try
                {
                    StationFx.Cooking(container, chef, skill, multiplier);
                }
                catch (Exception error)
                {
                    Plugin.Log.LogDebug("StationFx (cooking): " + error.Message);
                }
            }
            return skill == Skill.Cooking ? deltaTime * multiplier : deltaTime / multiplier;
        }
    }

    /// <summary>
    /// Shows the skill-select screen as the first step of the level intro, i.e. right after the
    /// loading screen (and after any intro cutscene), before "Ready... Go!". Run() is an iterator,
    /// so instead of rewriting its IL we wrap the enumerator it returns.
    /// </summary>
    [HarmonyPatch(typeof(LevelIntroFlowroutine), "Run")]
    internal static class LevelIntroPatch
    {
        private static void Postfix(ref IEnumerator __result)
        {
            __result = SkillSelectScreen.Wrap(__result);
        }
    }

    /// <summary>
    /// While the skill screen is open, the kitchen behind it can't be used: the pause menu doesn't
    /// open. A Start / Esc press then is thrown away, so it doesn't open the menu the moment the
    /// screen closes either. A pause the game makes for itself (its network or system layer) just
    /// waits until the screen has closed.
    /// </summary>
    [HarmonyPatch(typeof(PauseMenuManager), "UpdateWhileClosed")]
    internal static class SkillScreenPausePatch
    {
        private static readonly FieldInfo StartButtonsField =
            AccessTools.Field(typeof(PauseMenuManager), "m_startButtons");

        private static bool Prefix(PauseMenuManager __instance)
        {
            if (!SkillSelectScreen.IsOpen) return true;
            ILogicalButton[] buttons = StartButtonsField != null ? StartButtonsField.GetValue(__instance) as ILogicalButton[] : null;
            if (buttons != null)
                foreach (ILogicalButton button in buttons)
                    if (button != null) button.JustPressed();
            return false;
        }
    }

    /// <summary>No emote wheel opens while the skill screen is open.</summary>
    [HarmonyPatch(typeof(EmoteWheel), "CanShow")]
    internal static class SkillScreenEmotePatch
    {
        private static void Postfix(ref bool __result)
        {
            if (SkillSelectScreen.IsOpen) __result = false;
        }
    }

    /// <summary>
    /// CHOPPING on the host, which decides when the item is chopped: a fractional stroke counter
    /// per chef, so 1.36x chops count 1 or 2 and the leftover carries on to the next item.
    /// </summary>
    [HarmonyPatch(typeof(ServerWorkableItem), "DoWork", new Type[] { typeof(ServerAttachStation), typeof(GameObject) })]
    internal static class ChopPatch
    {
        private static bool _reentered;

        private static bool Prefix(ServerWorkableItem __instance, ServerAttachStation _station, GameObject _worker)
        {
            if (_reentered || LevelState.RawStrokes > 0) return true;
            float multiplier = Plugin.Skills.Multiplier(_worker, Skill.Chopping);
            if (Mathf.Approximately(multiplier, 1f)) return true;

            int strokes = LevelState.Strokes(LevelState.ChopCredit, _worker, multiplier);

            _reentered = true;
            try
            {
                for (int i = 0; i < strokes; i++)
                {
                    if (__instance == null || __instance.HasFinished()) break;
                    __instance.DoWork(_station, _worker);
                }
            }
            finally
            {
                _reentered = false;
            }
            return false;
        }
    }

    /// <summary>
    /// CHOPPING on every machine's progress bar. Each machine (the host included) counts the
    /// chops it sees and fills the bar itself, and the host only says when the item is done, so
    /// without this the bar ran at normal speed while the item finished sooner or later.
    /// </summary>
    [HarmonyPatch(typeof(ClientWorkableItem), "DoWork", new Type[] { typeof(ClientAttachStation), typeof(GameObject) })]
    internal static class ClientChopPatch
    {
        private static bool _reentered;

        private static bool Prefix(ClientWorkableItem __instance, ClientAttachStation _station, GameObject _worker)
        {
            if (_reentered || LevelState.RawStrokes > 0) return true;
            float multiplier = Plugin.Skills.Multiplier(_worker, Skill.Chopping);
            if (Mathf.Approximately(multiplier, 1f)) return true;

            int strokes = LevelState.Strokes(LevelState.ClientChopCredit, _worker, multiplier);

            _reentered = true;
            try
            {
                for (int i = 0; i < strokes; i++)
                {
                    if (__instance == null || __instance.HasFinished()) break;
                    __instance.DoWork(_station, _worker);
                }
            }
            finally
            {
                _reentered = false;
            }
            // Every machine sees every chop, so each shows it on the board itself.
            try
            {
                if (_station != null) StationFx.Chopped(_station.gameObject, _worker, multiplier, strokes);
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("StationFx (chopping): " + error.Message);
            }
            return false;
        }
    }

    /// <summary>COOKING and SLOW BURN for pots, pans and trays on the heat.</summary>
    [HarmonyPatch(typeof(ServerCookingHandler), "Cook")]
    internal static class CookPatch
    {
        private static void Prefix(ServerCookingHandler __instance, ref float _cookingDeltatTime)
        {
            _cookingDeltatTime = LevelState.Scale(__instance.gameObject, _cookingDeltatTime,
                __instance.GetCookingProgress(), __instance.AccessCookingTime, __instance.IsBurning());
        }
    }

    /// <summary>COOKING and SLOW BURN for mixing bowls in a mixer.</summary>
    [HarmonyPatch(typeof(ServerMixingHandler), "Mix")]
    internal static class MixPatch
    {
        private static void Prefix(ServerMixingHandler __instance, ref float _deltaTime)
        {
            _deltaTime = LevelState.Scale(__instance.gameObject, _deltaTime,
                __instance.GetMixingProgress(), __instance.AccessMixingTime, __instance.IsOverMixed());
        }
    }

    /// <summary>
    /// Every server-side "chef puts down / puts in" action goes through PlaceHeldItem_Server. We
    /// remember the chef for the duration of the call (dish delivery reads it), and afterwards record
    /// which pot, pan or mixing bowl that chef just filled or placed.
    /// </summary>
    [HarmonyPatch(typeof(PlayerControlsHelper), "PlaceHeldItem_Server")]
    internal static class PlacementPatch
    {
        private static void Prefix(PlayerControls _control, out GameObject __state)
        {
            __state = null;
            if (_control == null) return;
            LevelState.CurrentPlacer = _control.gameObject;
            ICarrier carrier = _control.gameObject.RequestInterface<ICarrier>();
            if (carrier != null) __state = carrier.InspectCarriedItem();
        }

        private static void Postfix(PlayerControls _control, GameObject _target, GameObject __state)
        {
            LevelState.CurrentPlacer = null;
            if (_control == null || !Plugin.Enabled.Value) return;

            ICarrier carrier = _control.gameObject.RequestInterface<ICarrier>();
            GameObject heldAfter = carrier != null ? carrier.InspectCarriedItem() : null;
            if (heldAfter == __state) return; // the placement didn't happen

            Credit(_control.gameObject, __state, _target);
        }

        /// <summary>
        /// A chef put <paramref name="placed"/> onto or into <paramref name="target"/> (by hand, or a
        /// Strong Arm toss that came down there): whose pot, pan or bowl it now is.
        /// </summary>
        internal static void Credit(GameObject chef, GameObject placed, GameObject target)
        {
            if (chef == null || !Plugin.Enabled.Value) return;
            MarkContainer(placed, chef);  // carried a pot/bowl and put it down (e.g. on the stove)
            MarkContainer(target, chef);  // put food into a pot/bowl (directly or sitting on a station)
        }

        /// <summary>Food thrown into a pot, pan or bowl: the thrower filled it.</summary>
        internal static void CreditCatch(GameObject container, GameObject thrower)
        {
            if (thrower == null || !Plugin.Enabled.Value) return;
            MarkContainer(container, thrower);
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            LevelState.CurrentPlacer = null;
        }

        private static void MarkContainer(GameObject obj, GameObject chef)
        {
            if (obj == null) return;
            GameObject container = Container(obj);
            if (container == null)
            {
                ServerAttachStation station = obj.GetComponent<ServerAttachStation>();
                if (station != null && station.HasItem()) container = Container(station.InspectItem());
            }
            if (container != null) LevelState.ContainerOwners[container.GetInstanceID()] = chef;
        }

        private static GameObject Container(GameObject obj)
        {
            if (obj == null) return null;
            if (obj.GetComponent<ServerCookingHandler>() != null) return obj;
            if (obj.GetComponent<ServerMixingHandler>() != null) return obj;
            return null;
        }
    }

    /// <summary>
    /// COOKING and SLOW BURN for food thrown into a pot, pan or bowl (on a station or in someone's
    /// hands): the thrower filled it, exactly as if they had put the food in by hand.
    /// </summary>
    [HarmonyPatch(typeof(ServerIngredientCatcher), "HandleCatch")]
    internal static class CatchFillPatch
    {
        private static void Prefix(ServerIngredientCatcher __instance, ICatchable _object)
        {
            try
            {
                if (__instance == null || _object == null) return;
                GameObject food = _object.AccessGameObject();
                IThrowable throwable = food != null ? food.RequestInterface<IThrowable>() : null;
                MonoBehaviour thrower = throwable != null ? throwable.GetThrower() as MonoBehaviour : null;
                if (thrower != null) PlacementPatch.CreditCatch(__instance.gameObject, thrower.gameObject);
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("CatchFill: " + error.Message);
            }
        }
    }

    /// <summary>
    /// SERVING: when a chef hands a dish in at the pass (or a Strong Arm toss lands on it), the
    /// dirty plate for that delivery comes back sooner or later. Conveyor-delivered dishes use
    /// the normal time.
    /// </summary>
    [HarmonyPatch(typeof(PlateReturnController), "FoodDelivered")]
    internal static class ServePatch
    {
        private static readonly FieldInfo ConfigField =
            AccessTools.Field(typeof(PlateReturnController), "m_plateReturnControllerDesc");

        private static void Prefix(PlateReturnController __instance, out float __state)
        {
            __state = -1f;
            if (ConfigField == null || LevelState.CurrentPlacer == null) return;
            float multiplier = Plugin.Skills.Multiplier(LevelState.CurrentPlacer, Skill.Serving);
            if (Mathf.Approximately(multiplier, 1f)) return;

            var config = (PlateReturnController.PlateReturnControllerConfig)ConfigField.GetValue(__instance);
            __state = config.m_plateReturnTime;
            config.m_plateReturnTime = __state / multiplier;
            ConfigField.SetValue(__instance, config);
        }

        private static void Postfix(PlateReturnController __instance, float __state, PlatingStepData _plateType, ServerPlateStation _station)
        {
            if (__state < 0f) return;
            var config = (PlateReturnController.PlateReturnControllerConfig)ConfigField.GetValue(__instance);
            float sooner = config.m_plateReturnTime;
            config.m_plateReturnTime = __state;
            ConfigField.SetValue(__instance, config);

            // Show it at the plate return (or the pass, when the plate goes to one chosen later).
            try
            {
                ServerPlateReturnStation back = _station != null ? _station.GetReturnStation(_plateType) : null;
                GameObject where = back != null ? back.gameObject : _station != null ? _station.gameObject : null;
                StationFx.Served(where, LevelState.CurrentPlacer,
                    Plugin.Skills.Multiplier(LevelState.CurrentPlacer, Skill.Serving), sooner);
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("StationFx (serving): " + error.Message);
            }
        }
    }

    /// <summary>
    /// WASHING on the host, which decides when a plate is clean: the sink's cleaning timer
    /// advances at the washing chefs' rate (their average, when two wash together).
    /// </summary>
    [HarmonyPatch(typeof(ServerWashingStation), "UpdateSynchronising")]
    internal static class WashPatch
    {
        private static readonly FieldInfo TimerField = AccessTools.Field(typeof(ServerWashingStation), "m_cleaningTimer");
        private static readonly FieldInfo WasherField = AccessTools.Field(typeof(ServerWashingStation), "m_interactor");
        private static readonly FieldInfo InteractableField = AccessTools.Field(typeof(ServerWashingStation), "m_interactable");
        private static readonly FieldInfo WashersField = AccessTools.Field(typeof(ServerInteractable), "m_interacters");

        private static void Prefix(ServerWashingStation __instance, out float __state)
        {
            __state = TimerField != null ? (float)TimerField.GetValue(__instance) : 0f;
        }

        private static void Postfix(ServerWashingStation __instance, float __state)
        {
            if (TimerField == null) return;
            float after = (float)TimerField.GetValue(__instance);
            // Only a frame that washed (not one that finished a plate and reset the timer).
            if (after <= __state) return;
            object sink = InteractableField != null ? InteractableField.GetValue(__instance) : null;
            List<GameObject> washers = sink != null && WashersField != null ? WashersField.GetValue(sink) as List<GameObject> : null;
            GameObject last = WasherField != null ? WasherField.GetValue(__instance) as GameObject : null;
            float multiplier = Plugin.Skills.AverageMultiplier(washers, last, Skill.Washing);
            if (Mathf.Approximately(multiplier, 1f)) return;
            TimerField.SetValue(__instance, __state + (after - __state) * multiplier);
        }
    }

    /// <summary>
    /// WASHING on every machine's progress bar. Each machine runs the sink's timer itself
    /// between the host's messages, so it has to run at the same rate as the host's.
    /// </summary>
    [HarmonyPatch(typeof(ClientWashingStation), "UpdateSynchronising")]
    internal static class ClientWashPatch
    {
        private static readonly FieldInfo TimerField = AccessTools.Field(typeof(ClientWashingStation), "m_cleaningTimer");
        private static readonly FieldInfo InteractableField = AccessTools.Field(typeof(ClientWashingStation), "m_interactable");
        private static readonly FieldInfo WashersField = AccessTools.Field(typeof(ClientInteractable), "m_interacters");
        private static readonly FieldInfo UiField = AccessTools.Field(typeof(ClientWashingStation), "m_progressUI");
        private static readonly FieldInfo StationField = AccessTools.Field(typeof(ClientWashingStation), "m_washingStation");

        private static void Prefix(ClientWashingStation __instance, out float __state)
        {
            __state = TimerField != null ? (float)TimerField.GetValue(__instance) : 0f;
        }

        private static void Postfix(ClientWashingStation __instance, float __state)
        {
            if (TimerField == null || InteractableField == null || WashersField == null) return;
            float after = (float)TimerField.GetValue(__instance);
            if (after <= __state) return;
            object sink = InteractableField.GetValue(__instance);
            List<GameObject> washers = sink != null ? WashersField.GetValue(sink) as List<GameObject> : null;
            float multiplier = Plugin.Skills.AverageMultiplier(washers, null, Skill.Washing);
            if (Mathf.Approximately(multiplier, 1f)) return;
            float timer = __state + (after - __state) * multiplier;
            TimerField.SetValue(__instance, timer);

            // The game already drew this frame's bar from the unscaled timer; redraw it.
            var ui = UiField != null ? UiField.GetValue(__instance) as ProgressUIController : null;
            var station = StationField != null ? StationField.GetValue(__instance) as WashingStation : null;
            if (ui != null && station != null && station.m_cleanPlateTime > 0f)
                ui.SetProgress(Mathf.Clamp01(timer / station.m_cleanPlateTime));

            // Every machine runs this, so each shows it on the sink itself.
            try
            {
                StationFx.Washing(__instance.gameObject, washers, multiplier);
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("StationFx (washing): " + error.Message);
            }
        }
    }

    /// <summary>THROWING: the thrower's throw force for this one throw.</summary>
    [HarmonyPatch(typeof(ServerAttachmentThrower), "ThrowItem")]
    internal static class ThrowPatch
    {
        private static readonly FieldInfo ThrowerField = AccessTools.Field(typeof(ServerAttachmentThrower), "m_thrower");
        private static AttachmentThrower _changed;
        private static float _force;

        private static void Prefix(ServerAttachmentThrower __instance)
        {
            if (ThrowerField == null) return;
            float multiplier = Plugin.Skills.Multiplier(__instance.gameObject, Skill.Throwing);
            if (Mathf.Approximately(multiplier, 1f)) return;
            var thrower = ThrowerField.GetValue(__instance) as AttachmentThrower;
            if (thrower == null) return;
            _changed = thrower;
            _force = thrower.m_throwForce;
            thrower.m_throwForce = _force * multiplier;
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            if (_changed != null) _changed.m_throwForce = _force;
            _changed = null;
        }
    }

    /// <summary>FIREFIGHTING: an extinguisher puts fire out at the rate of the chef holding it.</summary>
    [HarmonyPatch(typeof(ServerFireExtinguishSpray), "UpdateSynchronising")]
    internal static class FirefightPatch
    {
        private static readonly FieldInfo SprayField =
            AccessTools.Field(typeof(ServerFireExtinguishSpray), "m_FireExtinguishSpray");
        private static FireExtinguishSpray _changed;
        private static float _time;

        private static void Prefix(ServerFireExtinguishSpray __instance)
        {
            if (SprayField == null) return;
            float multiplier = Plugin.Skills.Multiplier(__instance.Carrier, Skill.Firefighting);
            if (Mathf.Approximately(multiplier, 1f)) return;
            var spray = SprayField.GetValue(__instance) as FireExtinguishSpray;
            if (spray == null) return;
            _changed = spray;
            _time = spray.m_exinguishTime;
            spray.m_exinguishTime = _time / multiplier;
        }

        private static readonly MethodInfo InSprayMethod =
            AccessTools.Method(typeof(ServerSprayingUtensil), "IsInSpray", new Type[] { typeof(Transform) });
        private static readonly object[] Args = new object[1];

        /// <summary>Shows it on the stations this spray is putting out (the host sends it to everyone).</summary>
        private static void Postfix(ServerFireExtinguishSpray __instance)
        {
            if (_changed == null || InSprayMethod == null || !__instance.IsSpraying()) return;
            try
            {
                GameObject chef = __instance.Carrier;
                float multiplier = Plugin.Skills.Multiplier(chef, Skill.Firefighting);
                foreach (ServerFlammable burning in ServerFlammable.GetAllOnFire())
                {
                    if (burning == null) continue;
                    Args[0] = burning.transform;
                    if ((bool)InSprayMethod.Invoke(__instance, Args))
                        StationFx.Firefighting(burning.gameObject, chef, multiplier);
                }
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("StationFx (firefighting): " + error.Message);
            }
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            if (_changed != null) _changed.m_exinguishTime = _time;
            _changed = null;
        }
    }

    /// <summary>
    /// RUNNING and DASHING. Online, the host simulates every chef's movement from its player's
    /// inputs and is the authority on where it is, while the player's own machine predicts the
    /// same movement and gets corrected towards the host's position. So both have to move the
    /// chef at the same speed: this applies to every chef on every machine, using that chef's
    /// player's levels. (Applying it only on the player's own machine made the host keep pulling
    /// a faster chef back.)
    ///
    /// It also keeps dashes in step (see <see cref="DashSync"/>): on a player's own machine it
    /// notes each dash its chef starts, and on the host a chef from another machine dashes
    /// whenever that machine says it did, without the host's own recharge or bump checks.
    /// </summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "Update_Movement")]
    internal static class MovementPatch
    {
        private static readonly FieldInfo ControlsField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_controls");
        private static readonly FieldInfo DashTimerField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_dashTimer");
        private static readonly FieldInfo ImpactTimerField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_impactTimer");

        // What the prefix changed, put back by the finalizer (even if the game's method throws, so
        // the speeds can never compound). Movement updates one chef at a time on the main thread.
        private static PlayerControls.MovementData _changed;
        private static float _run, _dashSpeed, _dashCooldown, _maxSpeed;

        // The chef being moved is on this machine (so its dashes are reported to the host).
        private static GameObject _local;

        private static void Prefix(ClientPlayerControlsImpl_Default __instance, out float __state)
        {
            __state = DashTimerField != null ? (float)DashTimerField.GetValue(__instance) : 0f;
            _local = null;
            if (ControlsField == null) return;
            PlayerControls controls = ControlsField.GetValue(__instance) as PlayerControls;
            if (controls == null || controls.Movement == null) return;
            GameObject chef = controls.gameObject;

            bool followDashes = false;
            if (DashSync.Active)
            {
                PlayerIDProvider provider = chef.GetComponent<PlayerIDProvider>();
                bool local = provider != null && provider.IsLocallyControlled();
                // Only the host has a ServerInputReceiver on its chefs.
                bool host = chef.GetComponent<ServerInputReceiver>() != null;
                if (local && !host)
                {
                    DashSync.Moved(chef);
                    _local = chef;
                }
                else if (!local && host)
                {
                    // Host, moving another machine's chef: that machine already decided.
                    followDashes = true;
                }
            }

            // Special skills and their buffs change movement the same way on every machine.
            float run = Plugin.Skills.Multiplier(chef, Skill.Running) * SpecialRuntime.RunMultiplier(chef);
            float dashSkill = Plugin.Skills.Multiplier(chef, Skill.Dashing);
            float dash = dashSkill * SpecialRuntime.DashMultiplier(chef);
            float recharge = SpecialRuntime.DashCooldownMultiplier(chef);
            bool canDash = SpecialRuntime.CanDash(chef);
            if (!followDashes && Mathf.Approximately(run, 1f) && Mathf.Approximately(dash, 1f) &&
                Mathf.Approximately(recharge, 1f) && canDash) return;

            PlayerControls.MovementData movement = controls.Movement;
            _changed = movement;
            _run = movement.RunSpeed;
            _dashSpeed = movement.DashSpeed;
            _dashCooldown = movement.DashCooldown;
            _maxSpeed = movement.MaxSpeed;
            movement.RunSpeed *= run;
            movement.DashSpeed *= dash;
            // Only the DASHING skill shortens the recharge; specials that dash further don't.
            movement.DashCooldown = movement.DashCooldown / dashSkill * recharge;
            // A dash the specials forbid (Heavy Boots, frozen): it never recharges in time.
            if (!canDash) movement.DashCooldown = 1e6f;
            // The game caps speed; lift the cap by as much as the chef got faster.
            movement.MaxSpeed *= Mathf.Max(1f, Mathf.Max(run, dash));

            if (followDashes)
            {
                movement.DashCooldown = 0f;
                // A bump only the host knows about mustn't stop a dash the player's machine made.
                ILogicalButton button = controls.ControlScheme != null ? controls.ControlScheme.m_dashButton : null;
                if (ImpactTimerField != null && button != null && button.HasUnclaimedPressEvent() &&
                    (float)ImpactTimerField.GetValue(__instance) >= 0f)
                    ImpactTimerField.SetValue(__instance, -1f);
            }
        }

        private static void Postfix(ClientPlayerControlsImpl_Default __instance, float __state)
        {
            if (_local == null || DashTimerField == null) return;
            // The dash timer only ever goes up when a dash starts.
            if ((float)DashTimerField.GetValue(__instance) > __state) DashSync.Dashed(_local);
            _local = null;
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            if (_changed == null) return;
            _changed.RunSpeed = _run;
            _changed.DashSpeed = _dashSpeed;
            _changed.DashCooldown = _dashCooldown;
            _changed.MaxSpeed = _maxSpeed;
            _changed = null;
            _local = null;
        }
    }
}
