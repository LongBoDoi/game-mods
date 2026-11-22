# EmoteSync

A companion plugin for the third-party **Arcade MOD (HostTools)** (`com.ch3ngyz.plugin.HostUtilities`, tested with v2.0.2). It makes HostTools' emote settings belong to each player, and makes every one of them actually apply.

## The problems

HostTools rewrites emote bubbles with these settings:

- `[Config_Range_Emote_Wheel]`: per emote `Do_Not_Replace` / `Use_Custom_Text` / `Use_Voice_Pack_File_Name`, plus `Emote_Font_Size` and `Emote_Font_Color`
- `[Config_Range_Emote_Custom_Text]`: the custom text per emote

On its own, HostTools has two problems:

1. **It uses the watching machine's settings.** Every chef's bubble is drawn with the settings of the PC it runs on. So the host sees the host's texts over everyone's chefs, and each client sees their own.
2. **It recognises emotes by their text, using a fixed list that mixes languages.** The four in-kitchen emotes (and `Emote_Good`) are matched against their **Chinese** text (`烹调中……`, `上菜中……`, `清理中……`, `准备中……`, `好！`). The lobby emotes (and `Emote_OK`) are matched against their **English** text. So in an English game, Cooking / Serving / Cleaning / Preparing are never replaced. In a Chinese game, the lobby emotes are never replaced. In any other language, almost nothing is.

## What EmoteSync does

- **Emotes are recognised by their localisation key** (`Text.Emote.Cooking`, …), which is the same in every language. Every emote works whatever language each player's game uses.
- **Settings are shared.** Each machine resolves its HostTools settings per emote and shares them in its **Steam lobby member data** (key `emotesync.profile`). It rewrites them whenever you change a setting, even mid-game.
- **Every emote is shared.** When you emote, your machine shares which lobby card or chef it was, which emote, and exactly what your bubble shows (key `emotesync.emote`), including the voice-pack file name your game picked. Every other screen shows that same bubble over that same card or chef, in your font size and colour.
- **In-kitchen emotes always show over the right chef.** The game's own emote message names a player by their number on their own PC, so in a kitchen it can't tell chefs from different PCs apart. When everyone in the lobby has EmoteSync 1.1.0, in-kitchen emotes from other PCs are shown from EmoteSync's news instead, over the chef of the player who sent them.
- **Lobby emotes arrive as before.** The game's own message usually arrives first. Until the sender's news follows, the bubble uses the sender's shared settings, so it's right straight away. The only exception is a voice-pack name, which stays blank for a moment.
- **Players on the same PC (split pad) share that PC's settings.** Offline, everything uses the local settings.

### Emote_Good and Emote_OK

These two HostTools settings are the **same** in-game emote: the OK bubble reads "好！" in Chinese and "OK!" in other languages. EmoteSync applies **Emote_OK** to your OK emote, or **Emote_Good** if your game is set to Chinese (Simplified or Traditional), which is what HostTools intended. Whichever one applies to you is what everyone else sees for your OK emote.

## Requirements

- Arcade MOD (HostTools) installed. EmoteSync won't load without it.
- **Everyone in the lobby needs EmoteSync 1.1.0.** If someone doesn't have it, their bubbles show the game's own text on other screens, and in-kitchen emotes fall back to the game's own delivery for everyone.
- Only the text is synced. Each machine still plays the emote **sound** from its own voice pack, because the audio files live on each PC.
- Set `Enabled = false` in `BepInEx\config\com.manhl.overcooked2.emotesync.cfg` to turn EmoteSync off and get HostTools' own behaviour back.

## Build

```
build.cmd           build and stage in ..\dist\
build.cmd deploy    also copy into the game's BepInEx\plugins\EmoteSync (game closed)
```

It finds HostTools' emote settings by type, member and setting name. If a HostTools update renames those, EmoteSync logs an error and stays off, which leaves HostTools' own behaviour unchanged.
