# Load Tweaks

A BepInEx plugin for **Kebab Chefs! - Restaurant Simulator** that works out why opening a
heavy save is slow.

## What the measurements found

On a heavy save, measured rather than guessed:

```
total 29.88s, 2876 frames, 96.2 fps average
created 1351 restaurant objects and 3113 items
```

Every method on the load path put together was busy for **under a tenth of a second**. The
other 29.8 seconds was not the loading logic at all.

The two numbers that matter: **4464 objects over 2876 frames** is about one and a half
objects per frame, which confirms the game rebuilds a restaurant roughly one object at a
time. And **96 fps means 10ms per frame** - with vertical sync already off and no frame
cap, so nothing was being held back.

A load costs `frames x cost per frame`. The number of frames is decided by the game and is
not something to meddle with from outside. The cost of a frame is not: ten milliseconds a
frame, on a loading screen, is mostly spent drawing a restaurant nobody is looking at yet.

So the optimisation is to draw less often while loading, and nothing else.

## Installing

BepInEx is already set up if you are running Player Tweaks. Drop `LoadTweaks.dll` into:

```
Kebab Chefs!\BepInEx\plugins\LoadTweaks\
```

Building from source deploys it there automatically:

```
dotnet build -c Release
```

Press **F8** after a load for the breakdown; a copy also goes to the BepInEx log.

## What it measures

Every phase is recorded twice: how long it **lasted**, and how much of that it was
actually **busy**. That one distinction decides which fix could ever apply.

- Lasted 20s, busy 1s → it was waiting, not working. More frames per second would help.
- Lasted 20s, busy 19s → it is doing real work. Frame rate is irrelevant; the work has to
  get smaller.

It also counts how many restaurant objects and items the save rebuilt, and the average
frame rate across the load, so the numbers can be read against the size of the save.

## Why loading a big save *should* be slow

The restaurant is not rebuilt in one go. `RestaurantManager.ProcessLoadedGameData` is a
coroutine, and a coroutine that yields gets one turn per frame. Individual objects then
wait on each other: every placed object runs a `WaitForInit` loop and every item a
`WaitGameIDForInitialization` loop, each polling once a frame until what it needs exists.
Several hundred of each costs several hundred frames.

That is the theory. Whether it is what is happening on your machine is what the report is
for - and note the first finding below, which says the obvious version of this theory is
already ruled out here.

## Two things learned the hard way

**The frame rate was never the problem here.** The first build logged
`vsync 0 -> 0, frame cap unlimited -> unlimited` - this machine already runs with no
vertical sync and no frame cap, so there were no held-back frames to release. The
`UncapFrameRateWhileLoading` setting remains, off, for anyone who has since capped their
frame rate. It cannot help otherwise.

**Do not hook the load coroutines.** The first build also timed the step function of each
load coroutine, which is where the per-phase frame counts came from. It crashed the game
on the scene change. Those are hidden compiler-generated classes driven from Unity's own
native coroutine runner, and standing in the middle of that is not worth doing for a
measurement. They are gone, along with everything else that runs during a scene change.

What remains hooks seven ordinary methods, none of which runs during a scene change or per
object per frame.

## Settings

Live in `BepInEx\config\com.manhl.kebabchefs.loadtweaks.cfg` after the first run.

| Setting | Default | Notes |
|---|---|---|
| `RenderLessWhileLoading` | **on** | The optimisation. Draws the scene every Nth frame while loading; the update loop is untouched. |
| `RenderEveryNthFrame` | 4 | One frame in four. Raise it while it still helps - the report will show when it stops. |
| `LogReportAfterEachLoad` | on | Breakdown to the log after every load. |
| `UncapFrameRateWhileLoading` | off | Does nothing unless you have a frame cap or vertical sync on. This machine has neither. |
| `LoadFrameRateCap` | 0 | Only used when the above is on. 0 = no limit. |
| `FastAssetIntegration` | off | Was the only behaviour change in effect when the game crashed. Off until that is ruled out. |

## Reading the timeline

The report ends with a second-by-second table:

```
  second   frames   frame ms   objects built
       1      140        7.1             210
      15       92       10.9             145
      29       78       12.8             130
```

Roughly flat frame cost means a fixed overhead - drawing - and drawing less often is the
fix. Clearly rising means the objects being rebuilt are themselves what makes each later
frame slower, and the answer would be somewhere else entirely. This is the number to
check first if raising `RenderEveryNthFrame` stops helping.
