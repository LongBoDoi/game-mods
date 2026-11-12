using System;
using System.Collections.Generic;

namespace RandomLevel
{
    // Independent of Unity so exhaustion, cycle boundaries and repeated scene queries can be tested.
    internal sealed class RandomRotation
    {
        private readonly Random random;
        private readonly List<string> levels = new List<string>();
        private readonly List<string> remaining = new List<string>();
        private object pendingOwner;
        private string pendingScene;

        internal RandomRotation(Random random) { this.random = random; }
        internal int Count { get { return levels.Count; } }

        internal void SetLevels(IEnumerable<string> scenes)
        {
            levels.Clear();
            foreach (string scene in scenes)
                if (!string.IsNullOrEmpty(scene) && !levels.Contains(scene)) levels.Add(scene);
            remaining.Clear();
            ClearPending();
        }

        internal void ClearPending() { pendingOwner = null; pendingScene = null; }

        internal string Next(object owner, string current)
        {
            // OC2DIYLevel probes GetNextScene, then the game calls it again to do the load.
            // Both queries must return the same scene and consume just one entry.
            if (ReferenceEquals(owner, pendingOwner) && pendingScene != null) return pendingScene;
            remaining.Remove(current);
            if (remaining.Count == 0)
            {
                remaining.AddRange(levels);
            }
            if (remaining.Count == 0) return null;
            // At a cycle boundary keep the previous scene in the new bag, but don't draw it
            // first. It still needs to be available later for a complete new cycle.
            int excluded = remaining.Count > 1 ? remaining.IndexOf(current) : -1;
            int index = random.Next(remaining.Count - (excluded >= 0 ? 1 : 0));
            if (excluded >= 0 && index >= excluded) index++;
            pendingScene = remaining[index];
            remaining.RemoveAt(index);
            pendingOwner = owner;
            return pendingScene;
        }
    }
}
