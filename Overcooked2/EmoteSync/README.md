# EmoteSync

A companion plugin for the third-party **Arcade MOD (HostTools)** (`com.ch3ngyz.plugin.HostUtilities`, tested with v2.0.2). It makes HostTools' emote settings belong to each player.

## The problem

HostTools rewrites emote bubbles with these settings:

- `[Config_Range_Emote_Wheel]`: per emote `Do_Not_Replace` / `Use_Custom_Text` / `Use_Voice_Pack_File_Name`, plus `Emote_Font_Size` and `Emote_Font_Color`
- `[Config_Range_Emote_Custom_Text]`: the custom text per emote

It applies them to **every** chef's bubble using the settings of the machine it runs on. So the host sees the host's texts over everyone's chefs, and each client sees their own texts over everyone's chefs. What one player chose was never what the others saw.

## What EmoteSync does

- Each machine shares its HostTools emote settings (all of the above) in its **Steam lobby member data** (key `emotesync.profile`). It rewrites them whenever you change a setting, even mid-game.
- Every bubble is drawn with the settings of the **player who sent the emote**: their mode, their custom text, their font size and colour. Every screen shows the same thing.
- `Use_Voice_Pack_File_Name` picks a random file each time, so the sender's machine shares the file name it actually picked (key `emotesync.emote`), and every screen shows that name. Until the name arrives (usually well under a second), other screens show a blank bubble. If it never arrives within 2.5 s, they show the game's own text.
- Players on the same PC (split pad) share that PC's settings. Offline, everything uses the local settings, as HostTools did.
- HostTools' own bubble rewrite is turned off while EmoteSync runs. Set `Enabled = false` in `BepInEx\config\com.manhl.overcooked2.emotesync.cfg` to get the old behaviour back.

## Requirements

- Arcade MOD (HostTools) installed. EmoteSync won't load without it.
- **Everyone in the lobby needs EmoteSync.** A player without it doesn't share settings, so their chef's bubbles show the game's own text on everyone else's screen. On their own screen, HostTools still shows everyone with *their* settings.
- Only the text is synced. Each machine still plays the emote **sound** from its own voice pack, because the audio files live on each PC.

## Build

```
build.cmd           build and stage in ..\dist\
build.cmd deploy    also copy into the game's BepInEx\plugins\EmoteSync (game closed)
```

It finds HostTools by type and member name. If a HostTools update renames those members, EmoteSync logs an error and stays off, which leaves HostTools' own behaviour unchanged.
