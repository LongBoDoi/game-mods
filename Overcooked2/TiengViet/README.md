# Tiếng Việt (Overcooked! 2)

Plays Overcooked! 2 in Vietnamese, together with every mod in this collection.

## Using it

Open **Settings → Game Settings** (Cài đặt → Cài đặt trò chơi). The first row is
**LANGUAGE** (**NGÔN NGỮ (LANGUAGE)** while Vietnamese is on): press left/right, or click the arrows, to
choose **English** or **Tiếng Việt**. The change happens straight away and is saved at once. The
screen's Save/Cancel buttons only cover the game's own options, so Cancel does not undo it.

The default is **Tiếng Việt**, so installing the mod is enough.

What changes:

- **The game**: all 1,357 texts the game shows from its text tables are translated: menus,
  settings, level names, tutorials, the story dialogue, DLC and seasonal content, and credit titles.
  Most text on screen redraws in the new language when you switch. A few texts the game builds
  from several pieces, such as score lines, switch the next time the game rewrites them.
- **The mods in this collection**: ChefSkills (the skill screen, all 112 special skills, the
  skill bar and its status chips), EmoteSync, ModUpdater, OC2Tweaks and RandomLevel all follow
  the same choice. Each mod shows its own text in English or Vietnamese to match.
- **Titles**: the game's title font (Naughty Squirrel) has letters such as é and â, but not the
  Vietnamese letters with a hook, a dot below or a horn (ả, ạ, ơ, ư, ộ…). Unity would fill in only
  the missing letters from another font, leaving one word in two typefaces. So while Vietnamese is
  on, every title with words in it is drawn in **Segoe UI Bold**, which comes with Windows; a row
  of tabs or buttons then shares one typeface. Numbers and symbols, such as timers and scores, keep
  the game's font. With English chosen, only a title that needs one of those letters switches. A
  one-line label that the wider font would push onto a second line is shrunk just enough to fit,
  down to 70% of its size. Everything goes back to the game's font and sizes when you switch to
  English.
- **Other mods' dialog texts**: a few texts that third-party mods show on the game's dialogs are
  translated too, such as DIYChef's "Invalid default chef" warning (see the end of `vi.tsv`).

It only changes what you see on your own PC. Other players can use any language, with or without
the mod.

## Settings

`BepInEx\config\com.manhl.overcooked2.tiengviet.cfg`:

| Setting | Default | |
|---|---|---|
| `Language` | `Vietnamese` | `Vietnamese` or `English`; the same setting as the in-game row |
| `HeaderFont` | `Segoe UI Bold, Arial Bold, Arial` | Windows fonts for titles. The first one Unity can find is used. Unity only sees regular, bold and italic faces, so names such as Segoe UI Black are not found. Leave it empty to never swap fonts |

MOD Settings (OC2ModSettings) copies its font from the game's titles and stops working if that
font isn't the game's own. TiengViet gives it the game's title font, so its pages keep working
and are fixed up like any other text.

With **English** chosen the game uses Steam's language, as it would without the mod.

## How the mods follow it

TiengViet writes `"vi"` or `"en"` into one AppDomain slot,
`com.manhl.overcooked2.language`. Each mod in the collection has a small `Lang.cs` that reads
that slot when it builds text, through `Lang.T("English", "Tiếng Việt")`. No mod references
another, so each one still works on its own. Without TiengViet installed, each mod keeps the
language it always had: ChefSkills and EmoteSync in English, OC2Tweaks, RandomLevel and ModUpdater
in Vietnamese.

To make a new mod follow the setting, copy any mod's `Lang.cs`, change its namespace and
`Default`, and wrap each visible string in `Lang.T(...)`. Read the value when text is built
rather than caching it, because the player can switch languages mid-game.

## The translation table

`vi.tsv` holds one line per game text: `Tag<TAB>English<TAB>Vietnamese`. The English column is
only a reference for whoever edits the file. Keep `[NAME]`-style placeholders, `<quad …/>` and
`<key …/>` markup, and `[A]`/`[B]`/`[X]`/`[Y]` button markers exactly as they are in the English. A
`\n` is a line break. The table is built into the DLL, so rebuild the mod after editing it.

After a game update, compare the table with the game's current text:

```
.tools\oc2-python\python.exe -I Overcooked2\TiengViet\tools\table.py check "E:\Steam\steamapps\common\Overcooked! 2"
```

It lists untranslated tags, tags the game dropped, texts whose English changed since they were
translated, and placeholders that no longer match.

## Building

```
TiengViet\build.cmd            build and stage in ..\dist\
TiengViet\build.cmd deploy     also copy into the game (game closed)
powershell -NoProfile -File tests\CheckTiengViet.ps1
```

`CheckTiengViet.ps1` checks that everything the mod reaches by name in the installed game still
exists: the Harmony targets and the private fields it reads. It also checks the plugin GUID and
the table built into the DLL.

For the Steam Mono build (Unity 2017, 32-bit, BepInEx 5 x86).
