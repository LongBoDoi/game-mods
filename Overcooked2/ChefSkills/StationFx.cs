using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace ChefSkills
{
    /// <summary>
    /// Shows on a station when a chef's skill levels change how fast it works: a ring on the
    /// board, sink, pot, mixing bowl, burning station or plate return while it works at that
    /// chef's rate (green when faster, red when slower: the skill card's colours), labelled with the
    /// skill and its percentage (CHOPPING +36%) in smaller letters than the specials' labels, stacked
    /// with them over the same thing; and a flash for every chop stroke the level added (or took
    /// away).
    ///
    /// Every machine sees the same thing. Chopping and washing are drawn by each machine from the
    /// chops and washing it already runs itself (ClientChopPatch, ClientWashPatch); cooking, slow
    /// burn, firefighting and serving only happen on the host, so the host sends them.
    /// </summary>
    internal static class StationFx
    {
        // The skill card's colours for a raised and a lowered skill (SkillSelectView).
        internal static readonly Color Faster = new Color(0.62f, 1f, 0.5f);
        internal static readonly Color Slower = new Color(1f, 0.58f, 0.48f);

        // How long a ring (and its label) stays after the last bit of work (work keeps renewing it).
        private const float BoardRing = 0.9f;
        private const float SinkRing = 0.6f;
        private const float HostRing = 1f;
        // Sparkles under a station working faster, at most this often (the host's go over the network).
        private const float SparkleEvery = 0.8f;
        private const float HostSparkleEvery = 1.5f;

        // Station and skill -> when it last sparkled.
        private static readonly Dictionary<long, float> s_sparkled = new Dictionary<long, float>();

        internal static void Reset()
        {
            s_sparkled.Clear();
        }

        private static int PercentOf(float multiplier)
        {
            return Mathf.RoundToInt((multiplier - 1f) * 100f);
        }

        private static string Signed(int percent)
        {
            return (percent > 0 ? " +" : " ") + percent.ToString(CultureInfo.InvariantCulture) + "%";
        }

        /// <summary>The label on this machine: CHOPPING +36%.</summary>
        private static string LabelHere(Skill skill, int percent)
        {
            return SkillStats.Names[(int)skill] + Signed(percent);
        }

        private static bool SparkleDue(GameObject station, Skill skill, float every)
        {
            long key = (long)station.GetInstanceID() * 16 + (int)skill;
            float now = SpecialRuntime.Now;
            float last;
            if (s_sparkled.TryGetValue(key, out last) && now - last < every) return false;
            if (s_sparkled.Count > 256) s_sparkled.Clear();
            s_sparkled[key] = now;
            return true;
        }

        // --- every machine, each drawing its own ----------------------------------------------------

        /// <summary>
        /// A chop on <paramref name="board"/> by a chef whose CHOPPING isn't 0, counted as
        /// <paramref name="strokes"/> strokes (ClientChopPatch, on every machine).
        /// </summary>
        internal static void Chopped(GameObject board, GameObject chef, float multiplier, int strokes)
        {
            if (board == null || !SpecialRuntime.InLevel) return;
            int percent = PercentOf(multiplier);
            if (percent == 0) return;
            Color colour = percent > 0 ? Faster : Slower;
            SpecialFx.Mark(board, colour, BoardRing, LabelHere(Skill.Chopping, percent), 0f, FxLabels.SkillLabelSize);
            // The stroke the level added, or the one it took away.
            if (strokes > 1) SpecialFx.Burst(SpecialFx.SurfacePoint(board), Faster, 0.75f, 0.35f);
            else if (strokes < 1) SpecialFx.Burst(SpecialFx.SurfacePoint(board), Slower, 0.55f, 0.3f);
        }

        /// <summary>
        /// A sink washing at <paramref name="multiplier"/> (the washers' average) this frame
        /// (ClientWashPatch, on every machine).
        /// </summary>
        internal static void Washing(GameObject sink, List<GameObject> washers, float multiplier)
        {
            if (sink == null || !SpecialRuntime.InLevel) return;
            int percent = PercentOf(multiplier);
            if (percent == 0) return;
            Color colour = percent > 0 ? Faster : Slower;
            SpecialFx.Mark(sink, colour, SinkRing, LabelHere(Skill.Washing, percent), 0f, FxLabels.SkillLabelSize);
            if (percent > 0 && SparkleDue(sink, Skill.Washing, SparkleEvery))
                SpecialFx.Burst(SpecialFx.SurfacePoint(sink), Faster, 0.7f, 0.35f);
        }

        // --- the host, for everyone -----------------------------------------------------------------

        /// <summary>
        /// A pot, pan or mixing bowl cooking (or waiting to burn) at its owner's COOKING (or SLOW
        /// BURN) rate this frame (LevelState.Scale, host only).
        /// </summary>
        internal static void Cooking(GameObject container, GameObject chef, Skill skill, float multiplier)
        {
            HostWork(container, skill, multiplier);
        }

        /// <summary>A fire being sprayed by a chef whose FIREFIGHTING isn't 0 (host only).</summary>
        internal static void Firefighting(GameObject burning, GameObject chef, float multiplier)
        {
            HostWork(burning, Skill.Firefighting, multiplier);
        }

        /// <summary>
        /// A dish handed in by a chef whose SERVING isn't 0: its plate comes back to
        /// <paramref name="returnStation"/> in <paramref name="seconds"/> (host only).
        /// </summary>
        internal static void Served(GameObject returnStation, GameObject chef, float multiplier, float seconds)
        {
            if (returnStation == null || !SpecialRuntime.InLevel || !SpecialNet.IsServer) return;
            int percent = PercentOf(multiplier);
            if (percent == 0) return;
            Color colour = percent > 0 ? Faster : Slower;
            // A ring and label on the plate return until the plate is back.
            SpecialFx.MarkAll(returnStation, colour, Mathf.Clamp(seconds, 0.5f, 30f),
                SkillStats.NameBoth(Skill.Serving, Signed(percent)), 0f, true);
        }

        private static void HostWork(GameObject station, Skill skill, float multiplier)
        {
            if (station == null || !SpecialRuntime.InLevel || !SpecialNet.IsServer) return;
            int percent = PercentOf(multiplier);
            if (percent == 0) return;
            Color colour = percent > 0 ? Faster : Slower;
            // The label goes to the other machines too, so in both languages.
            SpecialFx.MarkAll(station, colour, HostRing, SkillStats.NameBoth(skill, Signed(percent)), 0f, true);
            if (percent > 0 && SparkleDue(station, skill, HostSparkleEvery))
                SpecialFx.BurstAll(SpecialFx.SurfacePoint(station), Faster, 0.7f, 0.35f);
        }
    }
}
