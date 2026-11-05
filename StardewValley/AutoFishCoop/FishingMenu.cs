using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Utilities;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace AutoFishCoop
{
    /// <summary>A panel for one split-screen player to turn their own Auto Fishing settings on and off.</summary>
    /// <remarks>Stardew gives every split-screen player their own <see cref="Game1.activeClickableMenu" /> and
    /// feeds it that player's own controller, so a menu opened on one screen belongs entirely to that player:
    /// no key has to be shared and nothing has to be aimed at anybody. It is built out of the game's own menu
    /// furniture, so it looks and works like the rest of the game - the D-pad moves between the rows, A picks
    /// one, B closes.</remarks>
    public sealed class FishingMenu : IClickableMenu
    {
        /// <summary>What picking a row does.</summary>
        private enum Choice
        {
            AutoFish,
            AutoHit,
            AutoCast,
            CastingPower,
            Close
        }

        /// <summary>The rows, top to bottom.</summary>
        private static readonly Choice[] Choices =
        {
            Choice.AutoFish,
            Choice.AutoHit,
            Choice.AutoCast,
            Choice.CastingPower,
            Choice.Close
        };

        private const int MenuWidth = 704;
        private const int RowHeight = 64;

        /// <summary>The space between the menu's frame and what is in it.</summary>
        private const int Margin = 32;

        /// <summary>Room for the title above the rows.</summary>
        private const int TitleHeight = 72;

        /// <summary>The checkbox sprite is 9 pixels drawn at 4x.</summary>
        private const int CheckboxSize = 36;

        /// <summary>The ID of the first row, for the game's own menu navigation.</summary>
        private const int FirstRowId = 1000;

        private readonly PerPlayerSettings Settings;

        /// <summary>The player whose settings this menu belongs to.</summary>
        private readonly long Player;

        /// <summary>Their player number counting from 0, for the title and the overrides.</summary>
        private readonly int Number;

        /// <summary>The button that opened the menu, which may still be held.</summary>
        private readonly KeybindList OpenedWith;

        /// <summary>Whether that button is one of the ones the menu moves between its rows with.</summary>
        private readonly bool OpenedWithDirection;

        private readonly List<ClickableComponent> Rows = new List<ClickableComponent>();

        /// <summary>The row the cursor is on, or -1.</summary>
        private int Hovered = -1;

        public FishingMenu(PerPlayerSettings settings, long player, int number, KeybindList openedWith)
        {
            if (settings == null)
                throw new ArgumentNullException("settings");

            Settings = settings;
            Player = player;
            Number = number;
            OpenedWith = openedWith;
            OpenedWithDirection = IsDirection(openedWith);

            Place();
            if (Game1.options.snappyMenus && Game1.options.gamepadControls)
            {
                populateClickableComponentList();
                snapToDefaultClickableComponent();
            }
        }

        /// <summary>Centres the menu on this player's screen and lays the rows out.</summary>
        private void Place()
        {
            width = MenuWidth;
            height = Margin * 2 + TitleHeight + Choices.Length * RowHeight;
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            if (Rows.Count == 0)
            {
                for (int i = 0; i < Choices.Length; i++)
                {
                    // The IDs only have to be unique and not clash with the ones the base class keeps for
                    // itself (-1, and -7777 downwards); the rows wrap round top to bottom.
                    ClickableComponent row = new ClickableComponent(RowBounds(i), Choices[i].ToString());
                    row.myID = FirstRowId + i;
                    row.upNeighborID = FirstRowId + (i > 0 ? i - 1 : Choices.Length - 1);
                    row.downNeighborID = FirstRowId + (i < Choices.Length - 1 ? i + 1 : 0);
                    Rows.Add(row);
                }
            }
            else
            {
                for (int i = 0; i < Rows.Count; i++)
                    Rows[i].bounds = RowBounds(i);
            }
        }

        private Rectangle RowBounds(int row)
        {
            return new Rectangle(
                xPositionOnScreen + Margin,
                yPositionOnScreen + Margin + TitleHeight + row * RowHeight,
                width - Margin * 2,
                RowHeight);
        }

        /// <inheritdoc />
        /// <remarks>The base class gathers these by reflecting over a menu's public fields, which the rows
        /// are not, so they are handed over here instead. Without this there would be nothing for the D-pad
        /// to move between.</remarks>
        public override void populateClickableComponentList()
        {
            allClickableComponents = new List<ClickableComponent>(Rows);
        }

        /// <inheritdoc />
        public override void snapToDefaultClickableComponent()
        {
            if (Rows.Count == 0)
                return;

            currentlySnappedComponent = Rows[0];
            snapCursorToCurrentSnappedComponent();
        }

        /// <inheritdoc />
        /// <remarks>A menu opened with a direction button - someone who has put the menu on the D-pad - would
        /// otherwise run the selection round the rows while that button is still held.</remarks>
        public override void applyMovementKey(int direction)
        {
            if (OpenedWithDirection && OpenedWith.IsDown())
                return;

            base.applyMovementKey(direction);
        }

        /// <summary>Whether a binding includes a button the game moves a menu's selection with.</summary>
        private static bool IsDirection(KeybindList binding)
        {
            if (binding == null)
                return false;

            foreach (Keybind keybind in binding.Keybinds)
            {
                foreach (SButton button in keybind.Buttons)
                {
                    switch (button)
                    {
                        case SButton.DPadUp:
                        case SButton.DPadDown:
                        case SButton.DPadLeft:
                        case SButton.DPadRight:
                        case SButton.LeftThumbstickUp:
                        case SButton.LeftThumbstickDown:
                        case SButton.LeftThumbstickLeft:
                        case SButton.LeftThumbstickRight:
                            return true;
                    }
                }
            }

            return false;
        }

        /// <inheritdoc />
        public override void receiveGamePadButton(Buttons button)
        {
            base.receiveGamePadButton(button);

            if (button == Buttons.B && readyToClose())
                exitThisMenu();
        }

        /// <inheritdoc />
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            for (int i = 0; i < Rows.Count; i++)
            {
                if (Rows[i].containsPoint(x, y))
                {
                    Pick(Choices[i]);
                    return;
                }
            }
        }

        /// <inheritdoc />
        public override void performHoverAction(int x, int y)
        {
            Hovered = -1;
            for (int i = 0; i < Rows.Count; i++)
            {
                if (Rows[i].containsPoint(x, y))
                {
                    Hovered = i;
                    return;
                }
            }
        }

        /// <inheritdoc />
        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            Place();
            if (Game1.options.snappyMenus && Game1.options.gamepadControls)
                snapCursorToCurrentSnappedComponent();
        }

        /// <summary>Changes this player's settings, or closes the menu.</summary>
        private void Pick(Choice choice)
        {
            switch (choice)
            {
                case Choice.AutoFish:
                    Settings.Change(Player, Number, delegate(FishingSettings settings) { settings.AutoFish = !settings.AutoFish; });
                    Game1.playSound("drumkit6");
                    break;

                case Choice.AutoHit:
                    Settings.Change(Player, Number, delegate(FishingSettings settings) { settings.AutoHit = !settings.AutoHit; });
                    Game1.playSound("drumkit6");
                    break;

                case Choice.AutoCast:
                    Settings.Change(Player, Number, delegate(FishingSettings settings) { settings.AutoCast = !settings.AutoCast; });
                    Game1.playSound("drumkit6");
                    break;

                case Choice.CastingPower:
                    Settings.Change(Player, Number, delegate(FishingSettings settings)
                    {
                        settings.CastingPower = FishingSettings.NextCastingPower(settings.CastingPower);
                    });
                    Game1.playSound("shwip");
                    break;

                case Choice.Close:
                    exitThisMenu();
                    break;
            }
        }

        /// <inheritdoc />
        public override void draw(SpriteBatch b)
        {
            // Dim this player's screen behind the menu, the way the game's own menus do.
            b.Draw(Game1.fadeToBlackRect, new Rectangle(0, 0, Game1.uiViewport.Width, Game1.uiViewport.Height),
                Color.Black * 0.4f);
            drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

            SpriteText.drawString(b, "Auto Fishing - Player " + (Number + 1),
                xPositionOnScreen + Margin, yPositionOnScreen + Margin);

            FishingSettings settings = Settings.For(Player, Number);
            for (int i = 0; i < Rows.Count; i++)
                DrawRow(b, i, settings);

            drawMouse(b);
        }

        private void DrawRow(SpriteBatch b, int row, FishingSettings settings)
        {
            Rectangle bounds = Rows[row].bounds;
            if (row == Hovered)
                b.Draw(Game1.staminaRect, bounds, Color.Wheat * 0.35f);

            string label = Label(Choices[row]);
            Vector2 size = Game1.dialogueFont.MeasureString(label);
            Utility.drawTextWithShadow(b, label, Game1.dialogueFont,
                new Vector2(bounds.X + 16, bounds.Y + (RowHeight - size.Y) / 2f), Game1.textColor);

            switch (Choices[row])
            {
                case Choice.AutoFish:
                    DrawCheckbox(b, bounds, settings.AutoFish);
                    break;

                case Choice.AutoHit:
                    DrawCheckbox(b, bounds, settings.AutoHit);
                    break;

                case Choice.AutoCast:
                    DrawCheckbox(b, bounds, settings.AutoCast);
                    break;

                case Choice.CastingPower:
                    DrawValue(b, bounds, (int)Math.Round(settings.CastingPower * 100f) + "%");
                    break;
            }
        }

        private static void DrawCheckbox(SpriteBatch b, Rectangle row, bool on)
        {
            b.Draw(Game1.mouseCursors,
                new Vector2(row.Right - 16 - CheckboxSize, row.Y + (RowHeight - CheckboxSize) / 2),
                on ? OptionsCheckbox.sourceRectChecked : OptionsCheckbox.sourceRectUnchecked,
                Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.4f);
        }

        private static void DrawValue(SpriteBatch b, Rectangle row, string value)
        {
            Vector2 size = Game1.dialogueFont.MeasureString(value);
            Utility.drawTextWithShadow(b, value, Game1.dialogueFont,
                new Vector2(row.Right - 16 - size.X, row.Y + (RowHeight - size.Y) / 2f), Game1.textColor);
        }

        private static string Label(Choice choice)
        {
            switch (choice)
            {
                case Choice.AutoFish:
                    return "Auto fishing";
                case Choice.AutoHit:
                    return "Hook the fish for me";
                case Choice.AutoCast:
                    return "Cast again by itself";
                case Choice.CastingPower:
                    return "Casting power";
                default:
                    return "Close";
            }
        }
    }
}
