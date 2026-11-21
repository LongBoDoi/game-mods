using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;
using Object = UnityEngine.Object;

namespace ChefSkills
{
    /// <summary>
    /// The UI art and fonts the skill-select screen borrows from the game. Everything here is
    /// looked up among assets the game already has in memory in a kitchen: the dialog art from
    /// the in-game menu scene (Mode Settings / Controls dialogs), button icons from the game's
    /// ControllerIconLookup and each chef's colour (with its chef-hat icon) from ChefColourData.
    /// </summary>
    internal sealed class GameArt
    {
        internal const string DialogSprite = "t_ui_dialog_large_01_d";
        internal const string HeaderSprite = "t_ui_dialog_header_01_d";
        internal const string BoxSprite = "t_ui_controls_inputbox_d";
        internal const string BadgeSprite = "t_ui_button_01_standard_d";
        internal const string LegendSprite = "control_bg";
        internal static readonly string[] CardSprites = { "UI_ControllerSettingsPanel_04", "UI_ControllerSettingsPanel_08" };

        // The coloured chef-select arrows, in the colour each one is drawn in.
        internal static readonly string[] ArrowColours = { "05", "04", "02", "03" };
        internal static readonly Color[] ArrowTints =
        {
            new Color32(24, 139, 231, 255), new Color32(233, 26, 54, 255),
            new Color32(41, 193, 41, 255), new Color32(244, 201, 51, 255),
        };

        private static readonly HashSet<string> Wanted = new HashSet<string>
        {
            DialogSprite, HeaderSprite, BoxSprite, BadgeSprite, LegendSprite,
            CardSprites[0], CardSprites[1],
            "UI_ChefSelectArrowsL_02", "UI_ChefSelectArrowsL_03", "UI_ChefSelectArrowsL_04", "UI_ChefSelectArrowsL_05",
            "UI_ChefSelectArrowsR_02", "UI_ChefSelectArrowsR_03", "UI_ChefSelectArrowsR_04", "UI_ChefSelectArrowsR_05",
            "t_ui_arrow_left_standard_01", "t_ui_arrow_right_standard_01",
        };

        private readonly Dictionary<string, Sprite> _sprites = new Dictionary<string, Sprite>();
        internal Font HeaderFont;
        internal Font BodyFont;

        internal bool HasDialogArt
        {
            get { return Get(DialogSprite) != null && Get(HeaderSprite) != null && HeaderFont != null; }
        }

        internal static GameArt Load()
        {
            var art = new GameArt();
            foreach (Sprite sprite in Resources.FindObjectsOfTypeAll<Sprite>())
            {
                if (sprite == null || sprite.texture == null || !Wanted.Contains(sprite.name)) continue;
                if (!art._sprites.ContainsKey(sprite.name)) art._sprites[sprite.name] = sprite;
            }
            foreach (Font font in Resources.FindObjectsOfTypeAll<Font>())
            {
                if (font == null) continue;
                if (font.name == "Header_Font" && art.HeaderFont == null) art.HeaderFont = font;
                if (font.name == "Body_Font" && art.BodyFont == null) art.BodyFont = font;
            }
            return art;
        }

        internal Sprite Get(string name)
        {
            Sprite sprite;
            return _sprites.TryGetValue(name, out sprite) && sprite != null ? sprite : null;
        }

        internal Font Header
        {
            get { return HeaderFont != null ? HeaderFont : Fallback(); }
        }

        internal Font Body
        {
            get { return BodyFont != null ? BodyFont : Header; }
        }

        private static Font Fallback()
        {
            return Resources.GetBuiltinResource<Font>("Arial.ttf");
        }

        /// <summary>
        /// A 9-sliced copy of one of the game's sprites, so a panel can be resized without
        /// stretching its corners (the taped corners of the controls panel, for instance).
        /// Larger <paramref name="pixelsPerUnit"/> draws the corners smaller.
        /// </summary>
        internal Sprite Sliced(string name, Vector4 border, float pixelsPerUnit)
        {
            Sprite source = Get(name);
            if (source == null) return null;
            try
            {
                Sprite sliced = Sprite.Create(source.texture, source.textureRect, new Vector2(0.5f, 0.5f),
                    pixelsPerUnit, 0, SpriteMeshType.FullRect, border);
                sliced.name = name + " (ChefSkills)";
                return sliced;
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không cắt được hình " + name + ": " + error.Message);
                return null;
            }
        }

        /// <summary>The colour the game gave this player's chef (null if it can't be found).</summary>
        internal static ChefColourData ColourOf(PlayerInputLookup.Player player)
        {
            try
            {
                foreach (PlayerIDProvider provider in Object.FindObjectsOfType<PlayerIDProvider>())
                {
                    if (!provider.IsLocallyControlled() || provider.GetID() != player) continue;
                    ChefMeshReplacer replacer = provider.GetComponent<ChefMeshReplacer>();
                    if (replacer == null) replacer = provider.GetComponentInChildren<ChefMeshReplacer>(true);
                    if (replacer == null) continue;
                    GameSession.SelectedChefData data = replacer.GetChefData();
                    if (data != null && data.Colour != null) return data.Colour;
                }
                AvatarDirectoryData directory = GameUtils.GetAvatarDirectoryData();
                if (directory != null && directory.Colours != null && (int)player < directory.Colours.Length)
                    return directory.Colours[(int)player];
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không đọc được màu đầu bếp: " + error.Message);
            }
            return null;
        }

        /// <summary>Position of a chef colour in the game's colour list (what other machines are told), or -1.</summary>
        internal static int ColourIndex(ChefColourData colour)
        {
            try
            {
                AvatarDirectoryData directory = GameUtils.GetAvatarDirectoryData();
                if (colour == null || directory == null || directory.Colours == null) return -1;
                return Array.IndexOf(directory.Colours, colour);
            }
            catch (Exception)
            {
                return -1;
            }
        }

        /// <summary>The chef colour at a position in the game's colour list, or null.</summary>
        internal static ChefColourData ColourAt(int index)
        {
            try
            {
                AvatarDirectoryData directory = GameUtils.GetAvatarDirectoryData();
                if (index < 0 || directory == null || directory.Colours == null || index >= directory.Colours.Length)
                    return null;
                return directory.Colours[index];
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>The icon the game shows for this button on this player's device, or null.</summary>
        internal static Sprite ButtonIcon(PlayerInputLookup.LogicalButtonID id, PlayerInputLookup.Player player)
        {
            try
            {
                ControllerIconLookup lookup = GameUtils.RequestManager<ControllerIconLookup>();
                PlayerManager playerManager = GameUtils.RequestManager<PlayerManager>();
                if (lookup == null || playerManager == null) return null;
                ControllerIconLookup.DeviceContext device = PlayerButtonImage.GetDevice(playerManager, player);
                ControlPadInput.Button? found =
                    PlayerButtonImage.GetControlPadButton<ControlPadInput.Button>(id, player, device);
                if (!found.HasValue) return null;
                // Same swap PlayerButtonImage does for players who inverted accept/cancel.
                ControlPadInput.Button button = found.Value;
                if (PlayerManagerShared<PCPlayerManager.PCPlayerProfile>.AcceptAndCancelButtonsInverted)
                {
                    if (button == ControlPadInput.Button.A) button = ControlPadInput.Button.B;
                    else if (button == ControlPadInput.Button.B) button = ControlPadInput.Button.A;
                }
                return lookup.GetIcon(button, ControllerIconLookup.IconContext.Bordered, device);
            }
            catch (Exception error)
            {
                Plugin.Log.LogDebug("Không lấy được biểu tượng nút: " + error.Message);
                return null;
            }
        }
    }
}
