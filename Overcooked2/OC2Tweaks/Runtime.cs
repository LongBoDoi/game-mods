using System;
using UnityEngine;

namespace OC2Tweaks
{
    /// <summary>
    /// The part of the mod that runs all the time, whatever screen is up: automatic kicks for
    /// saved bans, ping measurement and the host's relay of it, the quick-restart and overlay
    /// keys, and the corner ping overlay itself. The settings live in the game's Settings menu
    /// (<see cref="SettingsScreen"/>); nothing here takes clicks.
    /// </summary>
    internal sealed class Runtime : MonoBehaviour
    {
        private float nextTick, nextRoster, nextErrorLog, nextFontLookup;
        private DisplayParty[] parties = new DisplayParty[0];
        private Texture2D fill;
        private Font font;
        private GUIStyle nameStyle, valueStyle;

        private void Update()
        {
            float now = Time.realtimeSinceStartup;
            // Independent of any screen and of timeScale, so a match cannot outrun a saved ban.
            if (now >= nextTick)
            {
                nextTick = now + 0.25f;
                Guard("Không thể tự động đuổi người chơi đã cấm", Moderation.EnforceBans);
                Guard("Không thể đo độ trễ kết nối", delegate
                {
                    ConnectionMonitor.Tick(now);
                    // Clients only see each other's ping through this, so it runs regardless.
                    PingRelay.Broadcast(now);
                });
            }

            bool overlayKey = false;
            // While a hotkey is being chosen on the settings screen, every key belongs to that.
            if (!TweaksMenu.HoldsKeys)
            {
                try { overlayKey = Plugin.PingOverlayKey.Value.IsDown(); }
                catch (Exception error) { LogLimited("Không đọc được phím bảng độ trễ: " + error.Message); }
            }
            if (overlayKey)
            {
                Plugin.PingOverlay.Value = !Plugin.PingOverlay.Value;
                nextRoster = 0;
            }

            if (Plugin.PingOverlay.Value && now >= nextRoster)
            {
                nextRoster = now + 0.25f;
                try { parties = Moderation.DisplayParties(); }
                catch (Exception error) { parties = new DisplayParty[0]; LogLimited("Không thể tải danh sách người chơi: " + error.Message); }
            }

            // Not while the settings screen is open: the key could be the one being chosen.
            if (!SettingsScreen.IsOpen) QuickRestart.Tick(now);
        }

        private void Guard(string what, Action action)
        {
            try { action(); }
            catch (Exception error) { LogLimited(what + ": " + error.Message); }
        }

        private void LogLimited(string message)
        {
            if (Time.realtimeSinceStartup < nextErrorLog) return;
            nextErrorLog = Time.realtimeSinceStartup + 5f;
            Plugin.Log.LogError(message);
        }

        private void OnDestroy()
        {
            if (fill != null) Destroy(fill);
        }

        private void OnGUI()
        {
            if (!Plugin.PingOverlay.Value || parties.Length == 0) return;
            if (Event.current == null || Event.current.type != EventType.Repaint) return;
            Color previous = GUI.color;
            try { DrawOverlay(); }
            catch (Exception error) { LogLimited("Không vẽ được bảng độ trễ: " + error); }
            finally { GUI.color = previous; }
        }

        /// <summary>
        /// The game's own body font, so the overlay matches the menus (it carries Vietnamese
        /// glyphs); Windows' Segoe UI until the game has loaded it.
        /// </summary>
        private void EnsureStyle()
        {
            if (fill == null)
            {
                fill = new Texture2D(1, 1);
                fill.hideFlags = HideFlags.HideAndDontSave;
                fill.SetPixel(0, 0, new Color(0.05f, 0.08f, 0.12f, 0.66f));
                fill.Apply();
            }
            if (font == null || (font.name != "Body_Font" && Time.realtimeSinceStartup >= nextFontLookup))
            {
                nextFontLookup = Time.realtimeSinceStartup + 5f;
                Font found = null;
                foreach (Font candidate in Resources.FindObjectsOfTypeAll<Font>())
                    if (candidate.name == "Body_Font") { found = candidate; break; }
                if (found == null && font == null)
                {
                    found = Font.CreateDynamicFontFromOSFont("Segoe UI", 16);
                    found.hideFlags = HideFlags.HideAndDontSave;
                }
                if (found != null && found != font)
                {
                    font = found;
                    nameStyle = null;
                }
            }
            if (nameStyle == null)
            {
                nameStyle = new GUIStyle(GUI.skin.label)
                {
                    font = font, fontSize = 17, wordWrap = false, clipping = TextClipping.Clip,
                    richText = false, alignment = TextAnchor.MiddleLeft
                };
                nameStyle.normal.textColor = Color.white;
                valueStyle = new GUIStyle(nameStyle) { alignment = TextAnchor.MiddleRight };
            }
        }

        /// <summary>
        /// One line per machine in the chosen corner: name on the left, ping on the right in the
        /// same colours as the settings screen. Draw-only, so it can stay up while playing.
        /// </summary>
        private void DrawOverlay()
        {
            EnsureStyle();
            const float margin = 12, pad = 8, line = 22, width = 290, valueWidth = 128;
            float now = Time.realtimeSinceStartup;
            float height = pad * 2 + line * parties.Length;
            OverlayCorner corner = Plugin.PingOverlayCorner.Value;
            bool left = corner == OverlayCorner.TopLeft || corner == OverlayCorner.BottomLeft;
            bool top = corner == OverlayCorner.TopLeft || corner == OverlayCorner.TopRight;
            var box = new Rect(left ? margin : Screen.width - width - margin,
                top ? margin : Screen.height - height - margin, width, height);
            GUI.color = Color.white;
            GUI.DrawTexture(box, fill);

            for (int i = 0; i < parties.Length; i++)
            {
                DisplayParty party = parties[i];
                float y = box.y + pad + i * line;
                GUI.color = Color.white;
                GUI.Label(new Rect(box.x + pad, y, width - pad * 2 - valueWidth - 4, line),
                    (party.IsLocal ? "Bạn" : MenuText.SafeName(party.Name)) + (party.Count > 1 ? " (" + party.Count + ")" : ""),
                    nameStyle);

                float ms;
                bool known = ConnectionMonitor.TryPing(party.Session, out ms);
                PingBand band;
                string value = MenuText.PingCell(known, ms, known && ConnectionMonitor.IsCurrent(party.Session, now),
                    !known && ConnectionMonitor.HostLacksMod(party.Session, now), out band);
                GUI.color = band == PingBand.Waiting ? new Color(0.75f, 0.78f, 0.82f) : TweaksMenu.Colour(band);
                GUI.Label(new Rect(box.x + width - pad - valueWidth, y, valueWidth, line), value, valueStyle);
            }
        }
    }
}
