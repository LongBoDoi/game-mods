using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace TiengViet
{
    /// <summary>
    /// Redraws text already on screen (or built and hidden) after the language changes. The game
    /// looks a tag up once, when a text starts or is given a new tag, so each tagged text is asked
    /// to look its tag up again. Texts the game composed itself from several lookups (scores,
    /// player lists) catch up the next time the game rewrites them.
    /// </summary>
    internal static class Refresh
    {
        // Set in T17Text.Start: a text without them has not started, and will look its tag up
        // in the new language when it does.
        private static readonly FieldInfo Processors = AccessTools.Field(typeof(T17Text), "m_TextProcessors");
        private static readonly FieldInfo LocalisedTag = AccessTools.Field(typeof(LocalisedText), "m_tag");
        private const string UnsetTag = "XXX.YYY.ZZZ";

        internal static void All()
        {
            int redrawn = 0;
            foreach (T17Text text in Resources.FindObjectsOfTypeAll<T17Text>())
            {
                if (!InScene(text) || !text.m_bNeedsLocalization) continue;
                string tag = text.m_LocalizationTag;
                if (string.IsNullOrEmpty(tag) || tag == UnsetTag) continue;
                if (Processors != null && Processors.GetValue(text) == null) continue;
                try { text.Convert(); redrawn++; }
                catch (Exception) { }
            }
            if (LocalisedTag != null)
                foreach (LocalisedText text in Resources.FindObjectsOfTypeAll<LocalisedText>())
                {
                    if (!InScene(text)) continue;
                    var tag = LocalisedTag.GetValue(text) as string;
                    if (string.IsNullOrEmpty(tag)) continue;
                    try { text.text = tag; redrawn++; }
                    catch (Exception) { }
                }
            HeaderFont.Sweep();
            Plugin.Log.LogDebug("Redrew " + redrawn + " texts.");
        }

        /// <summary>Loaded in a scene (or kept across scenes), not a prefab or other asset.</summary>
        internal static bool InScene(Component component)
        {
            return component != null && component.gameObject.scene.IsValid();
        }
    }
}
