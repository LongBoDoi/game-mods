using System;
using System.Collections.Generic;
using RandomLevel;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string message)
    {
        checks++;
        if (!value) throw new Exception(message);
    }

    private static void Main()
    {
        // Every full cycle must contain every level, even across a non-repeating boundary.
        for (int seed = 0; seed < 100; seed++)
        {
            var rotation = new RandomRotation(new Random(seed));
            rotation.SetLevels(new[] { "a", "b", "c", "d", "a", null, "" });
            Check(rotation.Count == 4, "Invalid and duplicate scenes must be omitted.");
            string current = null;
            for (int cycle = 0; cycle < 15; cycle++)
            {
                var visited = new HashSet<string>();
                for (int i = 0; i < 4; i++)
                {
                    var owner = new object();
                    string next = rotation.Next(owner, current);
                    Check(next != current, "Consecutive scenes must differ at cycle boundaries too.");
                    Check(visited.Add(next), "A complete cycle must have no repeats.");
                    for (int query = 0; query < 3; query++)
                        Check(rotation.Next(owner, current) == next, "Probe queries must not change the pending scene.");
                    current = next;
                    rotation.ClearPending();
                }
                Check(visited.Count == 4, "Every level must remain reachable in every cycle.");
            }
        }
        var edge = new RandomRotation(new Random(1));
        edge.SetLevels(new string[0]);
        Check(edge.Next(new object(), null) == null, "Empty catalog must not throw.");
        edge.SetLevels(new[] { "only" });
        Check(edge.Next(new object(), "only") == "only", "One-level catalogs must remain playable.");
        edge.SetLevels(new[] { "new" });
        Check(edge.Next(new object(), "only") == "new", "Refreshing must discard the old rotation.");
        Transition();
        Skip();
        Console.WriteLine("PASS: " + checks + " checks (rotation cycles, probe idempotency, Continue, Restart, mid-round skip and transition guards).");
    }

    private static string Query(ServerCampaignFlowController flow, bool restart, string original, out GameState start, out GameState end, out bool loading)
    {
        start = GameState.MainMenu;
        end = GameState.NotSet;
        loading = false;
        NextRandomScene.Postfix(flow, restart, ref start, ref end, ref loading, ref original);
        return original;
    }

    private static void Transition()
    {
        Plugin.Rotation = new RandomRotation(new Random(8));
        Plugin.Rotation.SetLevels(new[] { "current", "other", "third" });
        GameUtils.Session = new GameSession { LevelSettings = new LevelSettings { SceneDirectoryVarientEntry = new SceneEntry { SceneName = "current" } } };
        var flow = new ServerCampaignFlowController();
        GameState start, end;
        bool loading;
        Plugin.Enabled = false;
        Plugin.Control = true;
        Check(Query(flow, false, "StartScreen", out start, out end, out loading) == "StartScreen" && !loading,
            "Manual mode must preserve the game's transition.");
        Plugin.Enabled = true;
        Plugin.Control = false;
        Check(Query(flow, false, "Lobbies", out start, out end, out loading) == "Lobbies" && !loading,
            "Clients and unrelated sessions must preserve the game's transition.");
        Plugin.Control = true;
        // Simulate DIY's probe followed by vanilla's real query, after the DIY prefix blanked it.
        string probe = Query(flow, false, "", out start, out end, out loading);
        Check(probe != "current" && !string.IsNullOrEmpty(probe), "Continue must choose a different installed scene.");
        Check(start == GameState.LoadKitchen && end == GameState.RunKitchen && loading,
            "The next scene must enter the pack's normal kitchen loading flow.");
        Check(Query(flow, false, "", out start, out end, out loading) == probe,
            "DIY's probe and the actual load must agree.");
        Check(Query(flow, true, "", out start, out end, out loading) == "current",
            "Restart must win over the pending random selection and the DIY prefix.");
        Check(Query(flow, false, "", out start, out end, out loading) == probe,
            "Restart queries must not consume another random scene.");
        Check(OC2DIYLevel.DIYLevelEntryUI.selectedLevel == probe, "The pack's selected scene must track Continue.");
        GameUtils.Session.LevelSettings = null;
        Check(Query(new ServerCampaignFlowController(), false, "original", out start, out end, out loading) == "original",
            "Missing level settings must preserve the original flow.");
        GameUtils.Session.LevelSettings = new LevelSettings();
        Check(Query(new ServerCampaignFlowController(), false, "original", out start, out end, out loading) == "original",
            "Missing scene settings must preserve the original flow.");
    }

    private static ServerCampaignFlowController SetupSkip()
    {
        SkipLevel.Reset();
        Plugin.Enabled = true;
        Plugin.Control = true;
        Plugin.Rotation = new RandomRotation(new Random(9));
        Plugin.Rotation.SetLevels(new[] { "current", "other", "third" });
        GameUtils.Session = new GameSession { LevelSettings = new LevelSettings { SceneDirectoryVarientEntry = new SceneEntry { SceneName = "current" } } };
        GameUtils.Multiplayer = new MultiplayerController();
        GameUtils.Events.Clear();
        OC2DIYLevel.DIYLevelAssetBundleManager.Loads = 0;
        OC2DIYLevel.DIYLevelAssetBundleManager.FailLoading = false;
        UnityEngine.SceneManagement.SceneManager.Active = new UnityEngine.SceneManagement.Scene { name = "current", Valid = true };
        var flow = new ServerCampaignFlowController { InRound = true };
        UnityEngine.Resources.Flows = new[] { flow };
        return flow;
    }

    private static void Skip()
    {
        var flow = SetupSkip();
        Plugin.Enabled = false;
        Check(!SkipLevel.TrySkip(10), "Skip must ignore manual mode.");
        Plugin.Enabled = true;
        Plugin.Control = false;
        Check(!SkipLevel.TrySkip(10), "Only the host may skip.");
        Plugin.Control = true;
        flow.InRound = false;
        Check(!SkipLevel.TrySkip(10), "Skip must ignore intros and results.");
        flow.InRound = true;
        flow.gameObject.activeInHierarchy = false;
        Check(!SkipLevel.TrySkip(10), "Inactive controller must not drive a skip.");
        flow.gameObject.activeInHierarchy = true;
        flow.gameObject.scene = new UnityEngine.SceneManagement.Scene { name = "current", Valid = false };
        Check(!SkipLevel.TrySkip(10), "Prefab controllers must be ignored.");
        flow.gameObject.scene = new UnityEngine.SceneManagement.Scene { name = "old", Valid = true };
        Check(!SkipLevel.TrySkip(10), "Unloading controllers must be ignored.");
        flow.gameObject.scene = UnityEngine.SceneManagement.SceneManager.Active;
        Plugin.Rotation.SetLevels(new[] { "current" });
        Check(!SkipLevel.TrySkip(10), "One-level pack must not turn skip into a restart.");
        Plugin.Rotation.SetLevels(new[] { "current", "other", "third" });
        GameUtils.Session.LevelSettings.SceneDirectoryVarientEntry.SceneName = "loading-next";
        Check(!SkipLevel.TrySkip(10), "Settings already switched to the next scene must prevent another load.");
        GameUtils.Session.LevelSettings.SceneDirectoryVarientEntry.SceneName = "current";
        var manager = GameUtils.Multiplayer;
        GameUtils.Multiplayer = null;
        Check(!SkipLevel.TrySkip(10), "Absent synchronisation manager must not consume a skip.");
        GameUtils.Multiplayer = manager;
        Check(GameUtils.Events.Count == 0, "Rejected skips must not tear down or load anything.");

        Check(SkipLevel.TrySkip(10), "A live random round should skip.");
        string next = OC2DIYLevel.DIYLevelAssetBundleManager.LastScene;
        Check(next != "current" && !string.IsNullOrEmpty(next), "Skip must choose another scene.");
        Check(GameUtils.Events.Count == 2 && GameUtils.Events[0] == "stop" && GameUtils.Events[1] == "load:" + next,
            "Skip must stop synchronisation before the pack broadcasts the new level.");
        Check(OC2DIYLevel.DIYLevelEntryUI.selectedLevel == next, "Skip must update the pack's selected scene.");
        Check(!SkipLevel.TrySkip(10) && !SkipLevel.TrySkip(100), "The old controller must stay locked throughout slow loads.");
        Check(!SkipLevel.AllowRestart(100), "Restart must not race an in-flight skip.");
        flow.InRound = false;
        Check(!SkipLevel.AllowRestart(100), "Restart must remain blocked after teardown deactivates the old round.");
        flow.InRound = true;
        Check(OC2DIYLevel.DIYLevelAssetBundleManager.Loads == 1, "Repeated requests must dispatch exactly one load.");

        Plugin.Rotation.ClearPending();
        GameUtils.Session.LevelSettings.SceneDirectoryVarientEntry.SceneName = next;
        UnityEngine.SceneManagement.SceneManager.Active = new UnityEngine.SceneManagement.Scene { name = next, Valid = true };
        UnityEngine.Resources.Flows = new[] { new ServerCampaignFlowController { InRound = true,
            gameObject = new UnityEngine.GameObject { scene = UnityEngine.SceneManagement.SceneManager.Active } } };
        Check(!SkipLevel.TrySkip(11), "New rounds must respect the transition cooldown.");
        Check(SkipLevel.TrySkip(13), "A fresh round must unlock skip after the cooldown.");
        Check(OC2DIYLevel.DIYLevelAssetBundleManager.LastScene != next, "The next skip must also avoid the current scene.");

        flow = SetupSkip();
        Check(SkipLevel.AllowRestart(20), "Normal restart must remain available.");
        Check(!SkipLevel.TrySkip(20) && !SkipLevel.AllowRestart(21), "An in-flight restart must block skip and another restart.");
        Plugin.Enabled = false;
        Check(SkipLevel.AllowRestart(21), "The transition guard must leave manual play alone.");
        flow = SetupSkip();
        OC2DIYLevel.DIYLevelAssetBundleManager.FailLoading = true;
        int errors = Plugin.Log.Errors;
        Check(!SkipLevel.TrySkip(50) && Plugin.Log.Errors == errors + 1, "Loading failures must be logged.");
        Check(!SkipLevel.TrySkip(100) && OC2DIYLevel.DIYLevelAssetBundleManager.Loads == 1,
            "A partial teardown must not be retried on the old controller.");
        SetupSkip();
        UnityEngine.Resources.Flows = new ServerCampaignFlowController[0];
        Check(!SkipLevel.TrySkip(10) && SkipLevel.AllowRestart(10), "Menus must not dispatch skips or change native restart handling.");
    }
}
