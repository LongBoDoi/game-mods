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

        // Workable item instance ID -> fractional chop strokes carried over between chops.
        internal static readonly Dictionary<int, float> ChopCredit = new Dictionary<int, float>();

        // The chef whose placement is being handled right now on the server (null otherwise).
        internal static GameObject CurrentPlacer;

        internal static void Reset()
        {
            ContainerOwners.Clear();
            ChopCredit.Clear();
            CurrentPlacer = null;
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
        internal static float Scale(GameObject container, float deltaTime, float progress, float doneAt)
        {
            if (!Plugin.Enabled.Value) return deltaTime;
            GameObject chef = OwnerOf(container);
            if (chef == null) return deltaTime;
            if (progress < doneAt) return deltaTime * Plugin.Skills.Multiplier(chef, Skill.Cooking);
            return deltaTime / Plugin.Skills.Multiplier(chef, Skill.SlowBurn);
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

    /// <summary>CHOPPING: a fractional stroke counter per item, so 1.36x chops count 1 or 2.</summary>
    [HarmonyPatch(typeof(ServerWorkableItem), "DoWork", new Type[] { typeof(ServerAttachStation), typeof(GameObject) })]
    internal static class ChopPatch
    {
        private static bool _reentered;

        private static bool Prefix(ServerWorkableItem __instance, ServerAttachStation _station, GameObject _worker)
        {
            if (_reentered) return true;
            float multiplier = Plugin.Skills.Multiplier(_worker, Skill.Chopping);
            if (Mathf.Approximately(multiplier, 1f)) return true;

            int key = __instance.GetInstanceID();
            float credit;
            LevelState.ChopCredit.TryGetValue(key, out credit);
            credit += multiplier;
            int strokes = (int)credit;
            LevelState.ChopCredit[key] = credit - strokes;

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

    /// <summary>COOKING and SLOW BURN for pots, pans and trays on the heat.</summary>
    [HarmonyPatch(typeof(ServerCookingHandler), "Cook")]
    internal static class CookPatch
    {
        private static void Prefix(ServerCookingHandler __instance, ref float _cookingDeltatTime)
        {
            _cookingDeltatTime = LevelState.Scale(__instance.gameObject, _cookingDeltatTime,
                __instance.GetCookingProgress(), __instance.AccessCookingTime);
        }
    }

    /// <summary>COOKING and SLOW BURN for mixing bowls in a mixer.</summary>
    [HarmonyPatch(typeof(ServerMixingHandler), "Mix")]
    internal static class MixPatch
    {
        private static void Prefix(ServerMixingHandler __instance, ref float _deltaTime)
        {
            _deltaTime = LevelState.Scale(__instance.gameObject, _deltaTime,
                __instance.GetMixingProgress(), __instance.AccessMixingTime);
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

            MarkContainer(__state, _control.gameObject);  // carried a pot/bowl and put it down (e.g. on the stove)
            MarkContainer(_target, _control.gameObject);  // put food into a pot/bowl (directly or sitting on a station)
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
    /// SERVING: when a chef hands a dish in at the pass, the dirty plate for that delivery comes
    /// back sooner or later. Thrown or conveyor-delivered dishes use the normal time.
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

        private static void Postfix(PlateReturnController __instance, float __state)
        {
            if (__state < 0f) return;
            var config = (PlateReturnController.PlateReturnControllerConfig)ConfigField.GetValue(__instance);
            config.m_plateReturnTime = __state;
            ConfigField.SetValue(__instance, config);
        }
    }

    /// <summary>WASHING: the sink's cleaning timer advances at the washing chef's rate.</summary>
    [HarmonyPatch(typeof(ServerWashingStation), "UpdateSynchronising")]
    internal static class WashPatch
    {
        private static readonly FieldInfo TimerField = AccessTools.Field(typeof(ServerWashingStation), "m_cleaningTimer");
        private static readonly FieldInfo WasherField = AccessTools.Field(typeof(ServerWashingStation), "m_interactor");

        private static void Prefix(ServerWashingStation __instance, out float __state)
        {
            __state = TimerField != null ? (float)TimerField.GetValue(__instance) : 0f;
        }

        private static void Postfix(ServerWashingStation __instance, float __state)
        {
            if (TimerField == null || WasherField == null) return;
            float after = (float)TimerField.GetValue(__instance);
            // Only a frame that washed (not one that finished a plate and reset the timer).
            if (after <= __state) return;
            float multiplier = Plugin.Skills.Multiplier(WasherField.GetValue(__instance) as GameObject, Skill.Washing);
            if (Mathf.Approximately(multiplier, 1f)) return;
            TimerField.SetValue(__instance, __state + (after - __state) * multiplier);
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

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            if (_changed != null) _changed.m_exinguishTime = _time;
            _changed = null;
        }
    }

    /// <summary>
    /// RUNNING and DASHING: movement is simulated on the machine that controls the chef, so these
    /// work for every player who has the mod, including online clients, and never touch another
    /// machine's chef.
    /// </summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "Update_Movement")]
    internal static class MovementPatch
    {
        private static readonly FieldInfo ControlsField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_controls");

        // What the prefix changed, put back by the finalizer (even if the game's method throws, so
        // the speeds can never compound). Movement updates one chef at a time on the main thread.
        private static PlayerControls.MovementData _changed;
        private static float _run, _dashSpeed, _dashCooldown, _maxSpeed;

        private static void Prefix(ClientPlayerControlsImpl_Default __instance)
        {
            if (ControlsField == null) return;
            PlayerControls controls = ControlsField.GetValue(__instance) as PlayerControls;
            if (controls == null || controls.Movement == null) return;
            float run = Plugin.Skills.Multiplier(controls.gameObject, Skill.Running, true);
            float dash = Plugin.Skills.Multiplier(controls.gameObject, Skill.Dashing, true);
            if (Mathf.Approximately(run, 1f) && Mathf.Approximately(dash, 1f)) return;

            PlayerControls.MovementData movement = controls.Movement;
            _changed = movement;
            _run = movement.RunSpeed;
            _dashSpeed = movement.DashSpeed;
            _dashCooldown = movement.DashCooldown;
            _maxSpeed = movement.MaxSpeed;
            movement.RunSpeed *= run;
            movement.DashSpeed *= dash;
            movement.DashCooldown /= dash;
            // The game caps speed; lift the cap by as much as the chef got faster.
            movement.MaxSpeed *= Mathf.Max(1f, Mathf.Max(run, dash));
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
        }
    }
}
