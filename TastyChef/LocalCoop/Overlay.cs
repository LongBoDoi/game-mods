using UnityEngine;

namespace LocalCoop
{
    // Short on-screen prompts. Guests: which player this window is, and what it is waiting for.
    // Player 1: the F9 hint at the title screen, toasts, and the "create a LAN room" reminder.
    internal static class Overlay
    {
        private static string s_toast;
        private static float s_toastUntil;
        private static GUIStyle s_big, s_small;
        private static Texture2D s_bg;

        public static void Toast(string text, float seconds = 4f)
        {
            s_toast = text;
            s_toastUntil = Time.realtimeSinceStartup + seconds;
        }

        public static void Draw()
        {
            if (Event.current.type != EventType.Repaint) return;
            EnsureStyles();
            float scale = Mathf.Clamp(Screen.height / 1080f, 0.6f, 2f);

            if (Coop.IsGuest) DrawGuest(scale);
            else DrawHost(scale);

            if (s_toast != null && Time.realtimeSinceStartup < s_toastUntil)
                Box(s_toast, s_small, scale, 0.92f);
        }

        private static void DrawGuest(float scale)
        {
            string who = "Player " + Coop.Player;
            if (InputOwnership.WaitingForOtherGuest)
            {
                Box($"{who}\nWaiting for the other players to pick their controllers...", s_big, scale, 0.45f);
                return;
            }
            if (InputOwnership.MyPad == null)
            {
                Box($"{who}\nPress LB + RB together on your controller", s_big, scale, 0.45f);
                return;
            }
            switch (RoomLink.State)
            {
                case RoomLink.GuestState.NoRoom:
                    Box($"{who} - waiting for Player 1 to create a LAN room (Multiplayer menu)", s_small, scale, 0.06f);
                    break;
                case RoomLink.GuestState.WaitingForTitle:
                case RoomLink.GuestState.Joining:
                    Box($"{who} - joining Player 1's room...", s_small, scale, 0.06f);
                    break;
            }
        }

        private static void DrawHost(float scale)
        {
            if (!HostLauncher.HasGuests)
            {
                // A reminder that the mod is here, only on the title screen and only briefly.
                if (Time.realtimeSinceStartup < 90f && MainMenuController.Instance != null && !MultiplayerSessionContextIsOnline())
                    Box($"Local co-op: press {Plugin.AddPlayerKey.Value} to open a window for another player", s_small, scale, 0.96f, alpha: 0.6f);
                return;
            }
            if (!RoomLink.HostHasLanRoom && !InWorld())
                Box("Local co-op: open Multiplayer and create a LAN room - the other windows join it by themselves", s_small, scale, 0.06f);
        }

        private static bool MultiplayerSessionContextIsOnline()
        {
            MultiplayerSessionService svc = MultiplayerSessionService.Instance;
            return svc != null && svc.HasActiveSession;
        }

        private static bool InWorld()
        {
            MultiplayerSessionService svc = MultiplayerSessionService.Instance;
            return svc != null && svc.HasActiveSession;
        }

        private static void Box(string text, GUIStyle style, float scale, float yFraction, float alpha = 0.85f)
        {
            var content = new GUIContent(text);
            Matrix4x4 old = GUI.matrix;
            GUI.matrix = Matrix4x4.Scale(new Vector3(scale, scale, 1f));
            float w = Screen.width / scale, h = Screen.height / scale;
            float textW = Mathf.Min(w * 0.9f, style.CalcSize(content).x);
            float textH = style.CalcHeight(content, textW);
            var rect = new UnityEngine.Rect((w - textW) / 2f, h * yFraction - textH / 2f, textW, textH);
            rect.y = Mathf.Clamp(rect.y, 0f, h - textH);
            Color c = GUI.color;
            GUI.color = new Color(1f, 1f, 1f, alpha);
            GUI.DrawTexture(rect, s_bg);
            GUI.color = c;
            GUI.Label(rect, content, style);
            GUI.matrix = old;
        }

        private static void EnsureStyles()
        {
            if (s_big != null) return;
            s_bg = new Texture2D(1, 1);
            s_bg.SetPixel(0, 0, new Color(0.08f, 0.06f, 0.05f, 1f));
            s_bg.Apply();
            s_big = new GUIStyle(GUI.skin.label)
            {
                fontSize = 40,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleCenter,
                wordWrap = true,
                padding = new RectOffset(36, 36, 24, 24),
            };
            s_big.normal.textColor = Color.white;
            s_small = new GUIStyle(s_big) { fontSize = 22, fontStyle = FontStyle.Normal, padding = new RectOffset(18, 18, 10, 10) };
            s_small.normal.textColor = Color.white;
        }
    }
}
