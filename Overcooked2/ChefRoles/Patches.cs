using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace ChefRoles
{
    /// <summary>Per-level bookkeeping shared by the gameplay patches.</summary>
    internal static class LevelState
    {
        // Pot/pan (ServerCookingHandler GameObject instance ID) -> role of the chef who last filled it.
        internal static readonly Dictionary<int, ChefRole> PotRoles = new Dictionary<int, ChefRole>();

        // Workable item instance ID -> fractional chop strokes carried over between chops.
        internal static readonly Dictionary<int, float> ChopCredit = new Dictionary<int, float>();

        // The chef whose placement is being handled right now on the server (null otherwise).
        internal static GameObject CurrentPlacer;

        internal static void Reset()
        {
            PotRoles.Clear();
            ChopCredit.Clear();
            CurrentPlacer = null;
        }
    }

    /// <summary>
    /// Shows the role-select screen as the first step of the level intro, i.e. right after the
    /// loading screen (and after any intro cutscene), before "Ready... Go!". Run() is an iterator,
    /// so instead of rewriting its IL we wrap the enumerator it returns.
    /// </summary>
    [HarmonyPatch(typeof(LevelIntroFlowroutine), "Run")]
    internal static class LevelIntroPatch
    {
        private static void Postfix(ref IEnumerator __result)
        {
            __result = RoleSelectScreen.Wrap(__result);
        }
    }

    /// <summary>Chop speed: a fractional stroke counter per item, so 1.5x chops count 1 or 2.</summary>
    [HarmonyPatch(typeof(ServerWorkableItem), "DoWork", new Type[] { typeof(ServerAttachStation), typeof(GameObject) })]
    internal static class ChopPatch
    {
        private static bool _reentered;

        private static bool Prefix(ServerWorkableItem __instance, ServerAttachStation _station, GameObject _worker)
        {
            if (_reentered) return true;
            float multiplier = Plugin.Roles.Multiplier(_worker, RoleStat.Chop);
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

    /// <summary>Cook speed: the pot/pan cooks at the rate of the chef who last filled or placed it.</summary>
    [HarmonyPatch(typeof(ServerCookingHandler), "Cook")]
    internal static class CookPatch
    {
        private static void Prefix(ServerCookingHandler __instance, ref float _cookingDeltatTime)
        {
            if (!Plugin.Enabled.Value) return;
            ChefRole role;
            if (LevelState.PotRoles.TryGetValue(__instance.gameObject.GetInstanceID(), out role))
                _cookingDeltatTime *= RoleStats.Get(role, RoleStat.Cook);
        }
    }

    /// <summary>
    /// Every server-side "chef puts down / puts in" action goes through PlaceHeldItem_Server. We
    /// remember the chef for the duration of the call (dish delivery reads it), and afterwards record
    /// which pot/pan that chef just filled or placed.
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

            ChefRole role = Plugin.Roles.RoleOf(_control.gameObject);
            MarkPot(__state, role);  // carried a pot/pan and put it down (e.g. on the stove)
            MarkPot(_target, role);  // put food into a pot/pan (directly or sitting on a station)
        }

        [HarmonyFinalizer]
        private static void Finalizer()
        {
            LevelState.CurrentPlacer = null;
        }

        private static void MarkPot(GameObject obj, ChefRole role)
        {
            if (obj == null) return;
            ServerCookingHandler handler = obj.GetComponent<ServerCookingHandler>();
            if (handler == null)
            {
                ServerAttachStation station = obj.GetComponent<ServerAttachStation>();
                if (station != null && station.HasItem())
                {
                    GameObject item = station.InspectItem();
                    if (item != null) handler = item.GetComponent<ServerCookingHandler>();
                }
            }
            if (handler != null) LevelState.PotRoles[handler.gameObject.GetInstanceID()] = role;
        }
    }

    /// <summary>
    /// Serve speed: when a chef delivers a dish by hand, the dirty plate for that delivery comes back
    /// faster or slower. Thrown or conveyor-delivered dishes use the normal time.
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
            float multiplier = Plugin.Roles.Multiplier(LevelState.CurrentPlacer, RoleStat.Serve);
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

    /// <summary>
    /// Run speed: movement is simulated on the machine that controls the chef, so this works for
    /// every player who has the mod, including online clients.
    /// </summary>
    [HarmonyPatch(typeof(ClientPlayerControlsImpl_Default), "Update_Movement")]
    internal static class RunPatch
    {
        private static readonly FieldInfo ControlsField =
            AccessTools.Field(typeof(ClientPlayerControlsImpl_Default), "m_controls");

        private static void Prefix(ClientPlayerControlsImpl_Default __instance, out float __state)
        {
            __state = -1f;
            if (ControlsField == null) return;
            PlayerControls controls = ControlsField.GetValue(__instance) as PlayerControls;
            if (controls == null || controls.Movement == null) return;
            float multiplier = Plugin.Roles.Multiplier(controls.gameObject, RoleStat.Run);
            if (Mathf.Approximately(multiplier, 1f)) return;

            __state = controls.Movement.RunSpeed;
            controls.Movement.RunSpeed = __state * multiplier;
        }

        private static void Postfix(ClientPlayerControlsImpl_Default __instance, float __state)
        {
            if (__state < 0f) return;
            PlayerControls controls = ControlsField.GetValue(__instance) as PlayerControls;
            if (controls != null && controls.Movement != null) controls.Movement.RunSpeed = __state;
        }
    }
}
