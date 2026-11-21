using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Mono.Cecil;
using Mono.Cecil.Cil;
using UnityEngine;

namespace LocalCoop
{
    // A guest window never has focus (it must not take the keyboard from Player 1), but the game
    // checks Application.isFocused in ~40 places before reading input, and UI Toolkit skips UI
    // input altogether while the app is unfocused. For guests, the answer becomes "yes".
    //
    // Application.isFocused is a native call with no managed body. HarmonyX can usually detour
    // it directly; that is tried first and verified. If it does not take, every method in the
    // game and in UI Toolkit that reads it is found with Cecil and rewritten to ask us instead.
    internal static class FocusPatch
    {
        private static bool s_hit;

        public static bool IsFocused() => true;

        public static void Install(Harmony harmony)
        {
            MethodInfo getter = AccessTools.PropertyGetter(typeof(Application), nameof(Application.isFocused));
            try
            {
                harmony.Patch(getter, prefix: new HarmonyMethod(typeof(FocusPatch), nameof(DirectPrefix)));
                if (ProbeDirectPatch())
                {
                    Plugin.Log.LogInfo("Guest window counts as focused (direct patch).");
                    return;
                }
                harmony.Unpatch(getter, HarmonyPatchType.Prefix, harmony.Id);
                Plugin.Log.LogInfo("Direct isFocused patch did not take; patching its callers instead.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogInfo("Direct isFocused patch unavailable (" + e.Message + "); patching its callers instead.");
            }
            PatchCallers(harmony);
        }

        // Compiled only after the patch is in place, like the game's own callers will be.
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool ProbeDirectPatch()
        {
            s_hit = false;
            bool _ = Application.isFocused;
            return s_hit;
        }

        private static bool DirectPrefix(ref bool __result)
        {
            s_hit = true;
            __result = true;
            return false;
        }

        private static void PatchCallers(Harmony harmony)
        {
            var transpiler = new HarmonyMethod(typeof(FocusPatch), nameof(Transpiler));
            string managed = Path.Combine(Application.dataPath, "Managed");
            int patched = 0, failed = 0;
            foreach (string name in new[] { "Assembly-CSharp", "UnityEngine.UIElementsModule" })
            {
                Assembly runtime = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.GetName().Name == name);
                string file = Path.Combine(managed, name + ".dll");
                if (runtime == null || !File.Exists(file)) continue;

                using (var module = ModuleDefinition.ReadModule(file, new ReaderParameters { ReadSymbols = false }))
                {
                    foreach (TypeDefinition type in AllTypes(module.Types))
                    {
                        foreach (MethodDefinition method in type.Methods)
                        {
                            if (!method.HasBody || !CallsIsFocused(method)) continue;
                            try
                            {
                                MethodBase target = runtime.ManifestModule.ResolveMethod(method.MetadataToken.ToInt32());
                                if (target.ContainsGenericParameters || (target.DeclaringType?.ContainsGenericParameters ?? false)) { failed++; continue; }
                                harmony.Patch(target, transpiler: transpiler);
                                patched++;
                            }
                            catch (Exception e)
                            {
                                failed++;
                                Plugin.Log.LogDebug($"isFocused caller {type.FullName}.{method.Name} not patched: {e.Message}");
                            }
                        }
                    }
                }
            }
            Plugin.Log.LogInfo($"Guest window counts as focused ({patched} methods patched, {failed} skipped).");
        }

        private static IEnumerable<TypeDefinition> AllTypes(IEnumerable<TypeDefinition> types)
        {
            foreach (TypeDefinition t in types)
            {
                yield return t;
                foreach (TypeDefinition n in AllTypes(t.NestedTypes)) yield return n;
            }
        }

        private static bool CallsIsFocused(MethodDefinition method)
        {
            foreach (Instruction ins in method.Body.Instructions)
            {
                if ((ins.OpCode == Mono.Cecil.Cil.OpCodes.Call || ins.OpCode == Mono.Cecil.Cil.OpCodes.Callvirt) &&
                    ins.Operand is MethodReference m && m.Name == "get_isFocused" && m.DeclaringType.FullName == "UnityEngine.Application")
                    return true;
            }
            return false;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            MethodInfo getter = AccessTools.PropertyGetter(typeof(Application), nameof(Application.isFocused));
            MethodInfo ours = AccessTools.Method(typeof(FocusPatch), nameof(IsFocused));
            foreach (CodeInstruction ins in instructions)
            {
                if ((ins.opcode == System.Reflection.Emit.OpCodes.Call || ins.opcode == System.Reflection.Emit.OpCodes.Callvirt) && ins.operand is MethodInfo m && m == getter)
                {
                    ins.opcode = System.Reflection.Emit.OpCodes.Call;
                    ins.operand = ours;
                }
                yield return ins;
            }
        }
    }
}
