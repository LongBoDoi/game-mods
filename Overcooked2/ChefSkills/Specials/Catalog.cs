using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;
using UnityEngine;

namespace ChefSkills
{
    /// <summary>
    /// Every special skill and quirk. The number is the one in SPECIAL_SKILLS.md and is what goes
    /// over the network, so never renumber an entry (add new ones at the end).
    /// </summary>
    public enum Special
    {
        None = 0,
        // 1. Movement
        Vault = 1, Blink = 2, Sprinter = 3, DoubleDash = 4, ChargeDash = 5, Momentum = 6, Slipstream = 7,
        SureFooted = 8, Immovable = 9, Ghost = 10, Recall = 11, Swap = 12, Summon = 13, QuickRespawn = 14,
        SafetyNet = 15, ConveyorRider = 16, BigChef = 17, TinyChef = 18, Hover = 19, Grapple = 20,
        // 2. Carrying and throwing
        PackMule = 21, Pocket = 22, Waiter = 23, LongArms = 24, Magnet = 25, Telekinesis = 26, BulkBuyer = 27,
        CatchersMitt = 28, StickyFingers = 29, StrongArm = 30, HomingThrow = 31, Lob = 32, AssemblyLine = 33,
        Juggler = 34, Rebound = 35, HandOff = 36,
        // 3. Prep stations
        Multitask = 37, EchoKnife = 38, PowerChop = 39, LuckyKnife = 40, PrepCook = 41, DashChop = 42,
        WashBurst = 43, Dishwasher = 44, Butler = 45, PlateConjurer = 46, SparePan = 47,
        // 4. Cooking and fire
        HotHands = 48, ColdHands = 49, Rescue = 50, Overclock = 51, SizzleMaster = 52, SteadyMixer = 53,
        Fireproof = 54, FireMarshal = 55, FireBlast = 56, BuiltInExtinguisher = 57, SlowSpread = 58,
        // 5. Orders and score
        Charmer = 59, MaitreD = 60, Clutch = 61, ComboKeeper = 62, SecondChance = 63, Gambler = 64,
        FreshDelivery = 65, Reroll = 66, TimeOut = 67, CloseEnough = 68, FortuneTeller = 69, BountyHunter = 70,
        GoldenHour = 71,
        // 6. Team support
        RallyCry = 72, Inspiring = 73, Medic = 74, Bodyguard = 75, Battery = 76, BuddySystem = 77, Quartermaster = 78,
        // 7. Temporary buffs
        Frenzy = 79, Overdrive = 80, SecondWind = 81, Adrenaline = 82, Zen = 83, CoffeeRun = 84, GlassCannon = 85,
        SlowStarter = 86, UnderPressure = 87, LuckyCharm = 88, HotStreak = 89,
        // 8. Level hazards and vehicles
        PortalPro = 90, Cannoneer = 91, Driver = 92, Weatherproof = 93, Lifeguard = 94,
        // 9. Versus / party
        Shove = 95, Pickpocket = 96, GreaseTrail = 97, SneezeBomb = 98, Hex = 99, DecoyOrder = 100,
        // 10. Quirks
        HeavyBoots = 101, Clumsy = 102, WeakArm = 103, Butterfingers = 104, Pyrophobe = 105, Germaphobe = 106,
        Vegetarian = 107, Sleepy = 108, Fragile = 109, ShowOff = 110, TunnelVision = 111, LoneWolf = 112,
    }

    internal enum SpecialKind { Active, Passive, Quirk }

    /// <summary>
    /// The picks on a card, in the order they are sent. New slots go at the end so old numbers keep
    /// their meaning (the third passive and the second active came after the quirk); the card
    /// shows them in <see cref="SpecialCatalog.RowOrder"/>.
    /// </summary>
    internal enum SpecialSlot { Active = 0, Passive1 = 1, Passive2 = 2, Quirk = 3, Passive3 = 4, Active2 = 5 }

    internal sealed class SpecialInfo
    {
        public Special Id;
        /// <summary>The English name: what the log and the icon letters use.</summary>
        public string NameEn;
        public string NameVi;
        public string DescriptionEn;
        public string DescriptionVi;
        public SpecialKind Kind;

        /// <summary>The name on the screen, in the language in use.</summary>
        public string Name
        {
            get { return Lang.T(NameEn, NameVi); }
        }

        /// <summary>The description on the screen, in the language in use.</summary>
        public string Description
        {
            get { return Lang.T(DescriptionEn, DescriptionVi); }
        }

        /// <summary>Actives: seconds before it can be used again.</summary>
        public float Cooldown;
        /// <summary>Quirks: skill points it frees.</summary>
        public int Refund;
        /// <summary>
        /// Passives: how much of the passive budget it takes (1 light, 2 solid, 3 build-defining).
        /// A chef's passives add up to at most <see cref="SpecialCatalog.PassiveBudget"/>.
        /// </summary>
        public int Level;
        /// <summary>Only one chef in the kitchen can have it (its effect covers the whole team).</summary>
        public bool TeamWide;
        /// <summary>Only offered when the host turns on versus/party specials.</summary>
        public bool Versus;
        /// <summary>Picks in the same group exclude each other (0 = no group).</summary>
        public int Group;
        /// <summary>
        /// Retired (merged into another special): never offered, and dropped from any loadout, so
        /// a last-picked build can't bring it back. Its number stays reserved.
        /// </summary>
        public bool Hidden;
    }

    /// <summary>
    /// What each special is, how it is picked, and which picks go together. A chef has up to two
    /// actives (the SKILL KEY uses the selected one, the SWITCH KEY changes which), up to three
    /// passives whose levels add up to at most 3, and optionally one quirk (a drawback that frees
    /// skill points). Specials cost no skill points.
    /// </summary>
    internal static class SpecialCatalog
    {
        internal const int SlotCount = 6;

        /// <summary>The most passive levels one chef can hold (one level 3, a 2 and a 1, or three 1s).</summary>
        internal const int PassiveBudget = 3;

        /// <summary>The slot shown on each card row, top to bottom: the active, the passives, the quirk.</summary>
        internal static readonly int[] RowOrder = { 0, 5, 1, 2, 4, 3 };

        /// <summary>The active slots: active 1, active 2 (index into a loadout).</summary>
        internal static readonly int[] ActiveSlots = { 0, 5 };

        /// <summary>The passive slots, in the order they are filled (and given up by Enforce last first).</summary>
        internal static readonly int[] PassiveSlots = { 1, 2, 4 };

        // Groups of specials that exclude each other. The last five only hold actives, so they
        // matter once a chef has two: one carry, one jump, one way of moving a teammate, one stun
        // (versus), and one one-per-kitchen active per chef.
        private const int DashStyle = 1, Size = 2, ThrowPath = 3, Carry = 4, Jump = 5, MateMove = 6, Stun = 7,
            KitchenActive = 8;

        // Picks that clash although they share no group, mostly a quirk that takes away what a
        // special needs: the first entry of each set clashes with each of the others.
        private static readonly Special[][] Clashes =
        {
            new[] { Special.HeavyBoots, Special.Sprinter, Special.DoubleDash, Special.ChargeDash, Special.Hover,
                Special.DashChop, Special.Overdrive, Special.Shove, Special.Pickpocket, Special.PortalPro, Special.Adrenaline },
            new[] { Special.WeakArm, Special.StrongArm, Special.HomingThrow, Special.Lob, Special.AssemblyLine, Special.Juggler },
            new[] { Special.Butterfingers, Special.CatchersMitt, Special.Juggler },
            new[] { Special.Pyrophobe, Special.BuiltInExtinguisher, Special.FireBlast, Special.Fireproof },
            new[] { Special.Germaphobe, Special.WashBurst },
            new[] { Special.Clumsy, Special.StickyFingers, Special.PackMule },
            new[] { Special.Fragile, Special.SafetyNet },
            new[] { Special.SecondChance, Special.ComboKeeper },
        };

        // Specials that can't both be in the same kitchen (team), whoever picks them: together
        // they'd mean the team's combo never breaks.
        private static readonly Special[][] KitchenExclusive =
        {
            new[] { Special.SecondChance, Special.ComboKeeper },
        };

        // Quirks that take a skill away entirely: that skill can't be lowered below 0 with them
        // (lowering it would be a free point).
        private static readonly Dictionary<Special, Skill> LockedSkills = new Dictionary<Special, Skill>
        {
            { Special.HeavyBoots, Skill.Dashing },
            { Special.WeakArm, Skill.Throwing },
            { Special.Germaphobe, Skill.Washing },
        };

        private static readonly Dictionary<Special, SpecialInfo> Infos = new Dictionary<Special, SpecialInfo>();
        private static readonly List<SpecialInfo> Ordered = new List<SpecialInfo>();

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<bool> _versus;
        private static ConfigEntry<float> _secondActiveCooldown;

        // Online, the host's switches replace this machine's own (null: use the config).
        private static bool? s_hostEnabled;
        private static bool? s_hostVersus;
        private static float? s_hostSecondActive;

        internal static string[] SlotNames
        {
            get
            {
                return Lang.Vi
                    ? new[] { "CHỦ ĐỘNG 1", "BỊ ĐỘNG", "BỊ ĐỘNG", "TẬT", "BỊ ĐỘNG", "CHỦ ĐỘNG 2" }
                    : new[] { "ACTIVE 1", "PASSIVE", "PASSIVE", "QUIRK", "PASSIVE", "ACTIVE 2" };
            }
        }

        internal static string[] SlotHelp
        {
            get
            {
                if (Lang.Vi)
                    return new[]
                    {
                        "Kỹ năng đặc biệt chủ động: bấm PHÍM KỸ NĂNG để dùng (có hai thì PHÍM ĐỔI chọn cái nào)",
                        "Kỹ năng đặc biệt bị động: luôn bật. Cấp các kỹ năng bị động cộng lại tối đa 3",
                        "Kỹ năng đặc biệt bị động thứ hai: luôn bật. Cấp các kỹ năng bị động cộng lại tối đa 3",
                        "Tật tùy chọn, đổi lại bạn có thêm điểm kỹ năng để cộng",
                        "Kỹ năng đặc biệt bị động thứ ba: luôn bật. Cấp các kỹ năng bị động cộng lại tối đa 3",
                        "Kỹ năng chủ động thứ hai: PHÍM ĐỔI chọn nó, PHÍM KỸ NĂNG dùng nó. Có hai kỹ năng chủ động thì hồi chiêu lâu hơn 25%",
                    };
                return new[]
                {
                    "Your active special: press your SKILL KEY to use it (with two, your SWITCH KEY picks which)",
                    "A passive special: always on. Your passives' levels add up to at most 3",
                    "A second passive special: always on. Your passives' levels add up to at most 3",
                    "Optional drawback that gives you skill points to spend",
                    "A third passive special: always on. Your passives' levels add up to at most 3",
                    "A second active special: your SWITCH KEY selects it, your SKILL KEY uses it. With two actives, cooldowns are 25% longer",
                };
            }
        }

        static SpecialCatalog()
        {
            // 1. Movement
            A(Special.Vault, "VAULT", "Hop over whatever is right in front of you (walls too), landing just past it", 9f, group: Jump);
            A(Special.Blink, "BLINK", "Teleport to the farthest free spot up to 6 m ahead, through counters and walls", 10f, group: Jump);
            P(Special.Sprinter, "SPRINTER", "Hold DASH to sprint +35% (more with DASHING; 4 s of stamina); you can't dash", level: 3, group: DashStyle);
            P(Special.DoubleDash, "DOUBLE DASH", "Two dash charges that refill 25% faster: chain two dashes back to back", level: 1, group: DashStyle);
            P(Special.ChargeDash, "CHARGE DASH", "Hold DASH to charge, release for up to twice the distance", level: 2, group: DashStyle);
            P(Special.Momentum, "MOMENTUM", "Keep running to build up to +30% speed; it fades soon after you stop", level: 2);
            P(Special.Slipstream, "SLIPSTREAM", "Co-op: teammates running close behind you get +20% speed, and you +10%", level: 1);
            P(Special.SureFooted, "SURE-FOOTED", "No slipping on ice, slopes or puddles; walkways carry you but never slow you", level: 2);
            P(Special.Immovable, "IMMOVABLE", "Dashes, thrown items, fire and wind can't knock or push you", level: 1);
            P(Special.Ghost, "GHOST", "Walk straight through other chefs (in versus, only your own team)", level: 1);
            A(Special.Recall, "RECALL", "Plant a flag; use again within 20 s to teleport back to it (cooldown starts on return)", 15f);
            A(Special.Swap, "SWAP", "Trade places with the teammate you face (or the nearest)", 14f, group: MateMove);
            A(Special.Summon, "SUMMON", "Pull the teammate you face (or the nearest) to your side", 14f, group: MateMove);
            P(Special.QuickRespawn, "QUICK RESPAWN", "Respawn after 1 s, back at the last safe spot you stood on", level: 1);
            P(Special.SafetyNet, "SAFETY NET", "Once every 30 s, falling off puts you back where you fell", level: 2);
            P(Special.ConveyorRider, "CONVEYOR RIDER", "Moving floors and wind carry you twice as fast and never slow you down", level: 1);
            P(Special.BigChef, "BIG CHEF", "Bigger: can't be knocked, knock others further, dash 25% further, -5% speed", level: 1, group: Size);
            P(Special.TinyChef, "TINY CHEF", "Smaller: +15% speed, but knocks send you further", level: 2, group: Size);
            P(Special.Hover, "HOVER", "Dash across a gap, up to 0.6 s in the air (10 s recharge)", level: 2);
            A(Special.Grapple, "GRAPPLE", "Pull yourself to the counter you face, up to 7 m away, over gaps too", 9f, group: Jump);

            // 2. Carrying and throwing
            A(Special.PackMule, "PACK MULE", "Carry a second item on your back; SKILL KEY swaps hand and back", 0.5f, group: Carry);
            A(Special.Pocket, "POCKET", "Pocket up to 2 ingredients, hidden; SKILL KEY stores or takes the last out", 0.5f, group: Carry);
            P(Special.Waiter, "WAITER", "Put a dish onto another dish to carry both; hand one in and the other goes in too if it's ordered", level: 2);
            P(Special.LongArms, "LONG ARMS", "Pick up, put down, chop and wash one tile further away", level: 2);
            P(Special.Magnet, "MAGNET", "Loose items on the floor within 6 m slide to you (none by another chef)", hidden: true);
            A(Special.Telekinesis, "TELEKINESIS", "Pull the item you face, up to 6 m away (through walls, or from a crate), into your hands", 8f);
            P(Special.BulkBuyer, "BULK BUYER", "Taking from a crate also gives a spare, on an empty board if it can be chopped (8 s)", level: 1);
            P(Special.CatchersMitt, "CATCHER'S MITT", "Catch from any side and further away; uncaught throws near you hop onto a counter, loose items within 6 m slide to you", level: 1);
            P(Special.StickyFingers, "STICKY FINGERS", "Keep what you hold when shoved or falling off; a fall holding something respawns you in 3 s", level: 1);
            P(Special.StrongArm, "STRONG ARM", "Throw anything (pots, dishes), aimed like a throw: to a teammate you aim at (8 m), or onto stations", level: 2);
            P(Special.HomingThrow, "HOMING THROW", "Throws fly to the teammate you aim at (10 m, more with THROWING); they catch from any side", level: 3, group: ThrowPath);
            P(Special.Lob, "LOB", "Throws arc high over counters and chefs, and go 30% further", level: 1, group: ThrowPath);
            P(Special.AssemblyLine, "ASSEMBLY LINE", "Take from a crate, then face a free teammate within 2.5 s: it flies to them", level: 1);
            P(Special.Juggler, "JUGGLER", "Re-throw within 2 s of a catch (or of taking from a crate) for a 50% longer throw", level: 1);
            P(Special.Rebound, "REBOUND", "Uncaught throws (yours too) landing within 4.5 m of you hop onto a free counter", hidden: true);
            P(Special.HandOff, "HAND-OFF", "PICK UP facing a teammate within 3 m gives them what you hold", level: 1);

            // 3. Prep stations
            P(Special.Multitask, "MULTITASK", "Up to 2 boards or sinks you leave keep working at 40% speed (2 per player, also in solo)", level: 3);
            P(Special.EchoKnife, "ECHO KNIFE", "60% of your chops also chop the last other board you used (within 6 m)", level: 3);
            A(Special.PowerChop, "POWER CHOP", "Finish the item on the board you face at once", 10f);
            P(Special.LuckyKnife, "LUCKY KNIFE", "Each chop has a 10% chance to finish the item at once (about 30-40% faster)", level: 2);
            P(Special.PrepCook, "PREP COOK", "Every 15 s, an ingredient you take from a crate comes chopped", level: 2);
            P(Special.DashChop, "DASH-CHOP", "Dashing past a loaded board chops it 3 times (each board once per 2 s)", level: 2);
            A(Special.WashBurst, "WASH BURST", "Wash every plate in the sink you face at once (not when its rack is full)", 40f);
            P(Special.Dishwasher, "DISHWASHER", "Every second dish you hand in comes back clean, on a sink's drying rack", level: 3);
            P(Special.Butler, "BUTLER", "Your dirty plates come back twice as fast, straight into a sink", level: 1);
            A(Special.PlateConjurer, "PLATE CONJURER", "Conjure a clean plate in your empty hands; it's gone once its dish is handed in", 30f);
            A(Special.SparePan, "SPARE PAN", "Conjure a copy of the empty pan, pot or bowl you face (one out at a time); it's gone once emptied", 25f);

            // 4. Cooking and fire
            P(Special.HotHands, "HOT HANDS", "A pot or pan you carry cooks 25% faster than on a cooker and stops when done", level: 1);
            P(Special.ColdHands, "COLD HANDS", "Pots and pans you carry or fill wait 8 s at done before they burn", level: 2);
            A(Special.Rescue, "RESCUE", "Un-burn the pot or pan you hold or face, put out its cooker's fire and hold it at done for 10 s", 30f);
            A(Special.Overclock, "OVERCLOCK", "The cooker or mixer you face works (and burns) three times as fast for 10 s", 30f);
            P(Special.SizzleMaster, "SIZZLE MASTER", "Pots and pans you carry or fill cook (and burn) twice as fast", level: 3);
            P(Special.SteadyMixer, "STEADY MIXER", "Bowls you carry or fill mix 75% faster and never over-mix", level: 2);
            P(Special.Fireproof, "FIREPROOF", "Walk through flames, and fire right next to you dies out in 2 s", level: 2);
            P(Special.FireMarshal, "FIRE MARSHAL", "A cooker under a pot or pan you carried or filled never catches fire, and its food burns half as fast", level: 1);
            A(Special.FireBlast, "FIRE BLAST", "Put out every fire within 6 m at once", 20f);
            P(Special.BuiltInExtinguisher, "HOSE HANDS", "Hold USE with empty hands facing a fire to spray it like an extinguisher", level: 2);
            P(Special.SlowSpread, "SLOW SPREAD", "Fire can't spread within 7 m of you", level: 1);

            // 5. Orders and score
            P(Special.Charmer, "CHARMER", "Dishes you hand in give +50% tip", level: 2);
            P(Special.MaitreD, "MAITRE D'", "Every order lasts 25% longer (whole team; levels with timed orders)", level: 3, teamWide: true);
            P(Special.Clutch, "CLUTCH", "A dish handed in during its order's last 20% scores x1.5", level: 2);
            P(Special.ComboKeeper, "COMBO KEEPER", "A missed order or wrong dish keeps the tip streak (every 25 s; not with Second Chance)", level: 1, teamWide: true);
            P(Special.SecondChance, "SECOND CHANCE", "An order's first expiry costs nothing, streak kept (every 30 s; not with Combo Keeper)", level: 2, teamWide: true);
            P(Special.Gambler, "GAMBLER", "Your tips are multiplied by 0, 1, 2 or 3 at random", level: 2);
            P(Special.FreshDelivery, "FRESH DELIVERY", "A dish handed in within 25 s of its first part cooking gets +50% tip", level: 1);
            A(Special.Reroll, "REROLL", "Replace the oldest order with a different dish, at no cost (whole team)", 40f, group: KitchenActive, teamWide: true);
            A(Special.TimeOut, "TIME OUT", "Order timers (and new orders) stop for 10 s (whole team)", 50f, group: KitchenActive, teamWide: true);
            P(Special.CloseEnough, "CLOSE ENOUGH", "Every 45 s, a dish nobody ordered counts as the oldest order", level: 2, teamWide: true);
            P(Special.FortuneTeller, "FORTUNE TELLER", "The next order shows early on the order bar and comes with 10% more time (not on phased levels)", level: 1, teamWide: true);
            P(Special.BountyHunter, "BOUNTY HUNTER", "One order at a time is framed in gold: hand it in for x1.5 points (next one 15 s later)", level: 3, teamWide: true);
            P(Special.GoldenHour, "GOLDEN HOUR", "In the last 60 s of the level, your tips are doubled", level: 1);

            // 6. Team support
            A(Special.RallyCry, "RALLY CRY", "You and every teammate run 30% faster for 8 s", 30f, group: KitchenActive, teamWide: true);
            P(Special.Inspiring, "INSPIRING", "Teammates within 3.5 m of you chop and wash about 25% faster (not your own other chef in solo)", level: 2, teamWide: true);
            P(Special.Medic, "MEDIC", "Teammates who fall empty-handed respawn next to you after 1 s; you respawn within 2.5 s", level: 1);
            P(Special.Bodyguard, "BODYGUARD", "Teammates within 6 m of you can't be knocked back or slip", level: 2);
            P(Special.Battery, "BATTERY", "Your active (5 s+ cooldown) cuts a teammate's longest cooldown, by up to half of it and 65% of yours (max 6.5 s); once per 10 s", level: 3, teamWide: true);
            P(Special.BuddySystem, "BUDDY SYSTEM", "Near your closest teammate (5 m) both run 15% faster and share speed buffs", level: 2);
            P(Special.Quartermaster, "QUARTERMASTER", "Teammates taking from a crate within 5 m of you get a spare, put on a counter next to you (5 s)", level: 1);

            // 7. Temporary buffs
            A(Special.Frenzy, "FRENZY", "All your skills +2 for 10 s, then -1 for 4 s", 30f);
            A(Special.Overdrive, "OVERDRIVE", "No dash recharge and +20% speed for 6 s", 30f);
            P(Special.SecondWind, "SECOND WIND", "In the last 60 s, all skills +1 (RUNNING and DASHING +3)", level: 1);
            P(Special.Adrenaline, "ADRENALINE", "After a knock, slip, respawn or team mistake: +50% run speed, free dashes for 8 s", level: 1);
            P(Special.Zen, "ZEN", "Stand still (not working) 1.5 s while you control the chef: your next chop or wash finishes at once", level: 2);
            P(Special.CoffeeRun, "COFFEE RUN", "Every correct dish you hand in gives you +25% run speed for 10 s", level: 1);
            P(Special.GlassCannon, "GLASS CANNON", "All skills +1, but a knock (even blocked), slip, fall or team mistake makes them -1 for 20 s", level: 3);
            P(Special.SlowStarter, "SLOW STARTER", "All skills -1 for the first 45 s of play, then +1", level: 2);
            P(Special.UnderPressure, "UNDER PRESSURE", "+7% run speed for each order waiting beyond the second, up to +21%", level: 2);
            P(Special.LuckyCharm, "LUCKY CHARM", "Each chop stroke (or half-second washing) 25% chance to count double", level: 2);
            P(Special.HotStreak, "HOT STREAK", "Team orders in a row without a miss: +3% speed each (up to 15%); at 8, +1 to every skill but RUNNING and DASHING", level: 3);

            // 8. Level hazards and vehicles
            P(Special.PortalPro, "TRANSIT PRO", "After a teleporter, cannon or steered platform: run 40% faster, free dashes for 4 s; no cannon fuse, steer 50% faster", level: 1);
            P(Special.Cannoneer, "CANNONEER", "Cannons fire you the moment the button is pressed, without the fuse", hidden: true);
            P(Special.Driver, "DRIVER", "Platforms and carts you steer move 50% faster", hidden: true);
            P(Special.Weatherproof, "WEATHERPROOF", "Wind and moving floors only push you the way you face", hidden: true);
            P(Special.Lifeguard, "LIFEGUARD", "Teammates who fall within 7 m of you are put back instead", level: 2);

            // 9. Versus / party
            P(Special.Shove, "SHOVE", "Your dashes knock harder and make chefs drop what they hold (in versus, only rivals)", level: 2, versus: true);
            P(Special.Pickpocket, "PICKPOCKET", "Dashing into a chef takes what they hold (in versus, only rivals; not from Sticky Fingers)", level: 2, versus: true);
            A(Special.GreaseTrail, "GREASE TRAIL", "For 4 s you leave a slippery trail; chefs who step on it slip (in versus, only rivals)", 20f, group: Stun, versus: true);
            A(Special.SneezeBomb, "SNEEZE BOMB", "Every chef within 4 m of you (teammates too) sneezes, drops what they hold and freezes for 2 s", 20f, group: Stun, versus: true);
            A(Special.Hex, "HEX", "The chef you face (5 m): their next 10 chops (within 30 s) don't count, and their washing starts 3 s late", 20f, versus: true);
            A(Special.DecoyOrder, "DECOY ORDER", "Add a fake order to the rival team's list for 25 s (it scores nothing)", 45f, versus: true);

            // 10. Quirks
            Q(Special.HeavyBoots, "HEAVY BOOTS", "You can't dash at all (DASHING can't go below 0)", 2, 0);
            Q(Special.Clumsy, "CLUMSY", "Dashing drops a plate you hold (clean, dirty or with food)", 1, 0);
            Q(Special.WeakArm, "WEAK ARM", "You can't throw (THROWING can't go below 0)", 1, 0);
            Q(Special.Butterfingers, "BUTTERFINGERS", "You can't catch thrown items", 1, 0);
            Q(Special.Pyrophobe, "PYROPHOBE", "Fire extinguishers won't spray for you", 1, 0);
            Q(Special.Germaphobe, "GERMAPHOBE", "You can't wash plates (WASHING can't go below 0)", 1, 0);
            Q(Special.Vegetarian, "VEGETARIAN", "You won't pick up or catch raw meat or fish", 1, 0);
            Q(Special.Sleepy, "SLEEPY", "Every 30 s you doze off for 2 s: you can't move, chop or wash", 2, 0);
            Q(Special.Fragile, "FRAGILE", "Respawning takes 5 s longer", 1, 0);
            Q(Special.ShowOff, "SHOW-OFF", "After every dish you hand in, you pose for 1 s", 1, 0);
            Q(Special.TunnelVision, "TUNNEL VISION", "You can only work at the first board or sink you use", 1, 0);
            Q(Special.LoneWolf, "LONE WOLF", "Teammates' specials and auras don't affect you", 1, 0);

            Vietnamese();
        }

        /// <summary>Every special's name and description in Vietnamese.</summary>
        private static void Vietnamese()
        {
            // 1. Movement
            Vi(Special.Vault, "NHẢY VỌT", "Nhảy qua thứ ngay trước mặt (kể cả tường), đáp ngay phía sau nó");
            Vi(Special.Blink, "THOẮT ẢNH", "Dịch chuyển tới chỗ trống xa nhất trong 6 m phía trước, xuyên qua quầy và tường");
            Vi(Special.Sprinter, "NƯỚC RÚT", "Giữ LƯỚT để chạy nước rút +35% (hơn nữa nhờ LƯỚT; 4 s thể lực); bạn không lướt được");
            Vi(Special.DoubleDash, "LƯỚT ĐÔI", "Hai lượt lướt hồi nhanh hơn 25%: lướt hai lần liền nhau");
            Vi(Special.ChargeDash, "LƯỚT TÍCH LỰC", "Giữ LƯỚT để tích lực, thả ra để lướt xa tới gấp đôi");
            Vi(Special.Momentum, "LẤY ĐÀ", "Chạy liên tục để tăng dần tới +30% tốc độ; dừng lại một lúc là mất dần");
            Vi(Special.Slipstream, "BÁM ĐUÔI", "Chơi chung đội: đồng đội chạy sát sau lưng bạn được +20% tốc độ, bạn được +10%");
            Vi(Special.SureFooted, "CHÂN VỮNG", "Không trượt trên băng, dốc hay vũng nước; băng chuyền chở bạn nhưng không làm bạn chậm");
            Vi(Special.Immovable, "BẤT ĐỘNG", "Cú lướt, đồ bị ném, lửa và gió không đẩy được bạn");
            Vi(Special.Ghost, "BÓNG MA", "Đi xuyên qua các đầu bếp khác (đối kháng: chỉ đồng đội)");
            Vi(Special.Recall, "QUAY VỀ", "Cắm cờ; dùng lại trong 20 s để dịch chuyển về đó (hồi chiêu tính từ lúc về)");
            Vi(Special.Swap, "ĐỔI CHỖ", "Đổi chỗ với đồng đội bạn đang nhìn (hoặc người gần nhất)");
            Vi(Special.Summon, "TRIỆU HỒI", "Kéo đồng đội bạn đang nhìn (hoặc người gần nhất) về cạnh bạn");
            Vi(Special.QuickRespawn, "HỒI SINH NHANH", "Hồi sinh sau 1 s, ở chỗ an toàn cuối cùng bạn đứng");
            Vi(Special.SafetyNet, "LƯỚI AN TOÀN", "Cứ 30 s một lần, rơi khỏi màn sẽ đưa bạn về chỗ vừa rơi");
            Vi(Special.ConveyorRider, "CƯỠI BĂNG CHUYỀN", "Sàn di chuyển và gió đưa bạn đi nhanh gấp đôi và không bao giờ làm bạn chậm");
            Vi(Special.BigChef, "BẾP KHỔNG LỒ", "To hơn: không bị đẩy, đẩy người khác xa hơn, lướt xa hơn 25%, -5% tốc độ");
            Vi(Special.TinyChef, "BẾP TÍ HON", "Nhỏ hơn: +15% tốc độ, nhưng bị đẩy văng xa hơn");
            Vi(Special.Hover, "LƠ LỬNG", "Lướt qua khoảng trống, bay được tới 0.6 s (hồi 10 s)");
            Vi(Special.Grapple, "MÓC KÉO", "Kéo mình tới quầy bạn đang nhìn, xa tới 7 m, bay qua cả khoảng trống");

            // 2. Carrying and throwing
            Vi(Special.PackMule, "THỒ HÀNG", "Đeo thêm một món trên lưng; PHÍM KỸ NĂNG đổi đồ ở tay và lưng");
            Vi(Special.Pocket, "TÚI ÁO", "Giấu tới 2 nguyên liệu vào túi; PHÍM KỸ NĂNG cất vào hoặc lấy món cuối ra");
            Vi(Special.Waiter, "BỒI BÀN", "Đặt món lên món khác để bưng cả hai; giao một món thì món kia cũng được giao nếu có người gọi");
            Vi(Special.LongArms, "TAY DÀI", "Nhặt, đặt, thái và rửa xa thêm một ô");
            Vi(Special.Magnet, "NAM CHÂM", "Đồ rơi trên sàn trong vòng 6 m trượt về phía bạn (trừ đồ cạnh đầu bếp khác)");
            Vi(Special.Telekinesis, "NIỆM LỰC", "Kéo món đồ bạn đang nhìn, xa tới 6 m (xuyên tường, hoặc từ thùng), vào tay bạn");
            Vi(Special.BulkBuyer, "MUA SỈ", "Lấy đồ từ thùng thì có thêm một phần, lên thớt trống nếu thái được (8 s)");
            Vi(Special.CatchersMitt, "GĂNG BẮT BÓNG", "Bắt được đồ từ mọi phía và ở xa hơn; đồ ném rơi gần bạn nảy lên quầy, đồ rơi trong 6 m trượt về phía bạn");
            Vi(Special.StickyFingers, "TAY DÍNH", "Giữ được đồ khi bị xô hay rơi khỏi màn; rơi khi đang cầm đồ thì hồi sinh sau 3 s");
            Vi(Special.StrongArm, "TAY KHỎE", "Ném được mọi thứ (nồi, món ăn), ngắm như khi ném: tới đồng đội bạn nhắm (8 m), hoặc lên trạm");
            Vi(Special.HomingThrow, "NÉM DẪN ĐƯỜNG", "Đồ ném bay tới đồng đội bạn nhắm (10 m, xa hơn nhờ NÉM); họ bắt được từ mọi phía");
            Vi(Special.Lob, "NÉM BỔNG", "Đồ ném bay vòng cao qua quầy và đầu bếp, và xa hơn 30%");
            Vi(Special.AssemblyLine, "DÂY CHUYỀN", "Lấy đồ từ thùng rồi nhìn đồng đội đang rảnh tay trong 2.5 s: đồ sẽ bay tới họ");
            Vi(Special.Juggler, "TUNG HỨNG", "Ném lại trong 2 s sau khi bắt (hoặc lấy từ thùng) để ném xa hơn 50%");
            Vi(Special.Rebound, "NẢY LẠI", "Đồ ném không ai bắt (cả đồ bạn ném) rơi trong 4.5 m quanh bạn sẽ nảy lên quầy trống");
            Vi(Special.HandOff, "TRAO TAY", "NHẶT khi nhìn đồng đội trong vòng 3 m để đưa họ đồ bạn đang cầm");

            // 3. Prep stations
            Vi(Special.Multitask, "ĐA NHIỆM", "Tối đa 2 thớt hay bồn rửa bạn rời đi vẫn chạy tiếp với 40% tốc độ (2 cho mỗi người chơi, cả khi chơi một mình)");
            Vi(Special.EchoKnife, "DAO VỌNG", "60% nhát thái của bạn thái luôn thớt khác bạn dùng gần nhất (trong 6 m)");
            Vi(Special.PowerChop, "THÁI MẠNH", "Thái xong ngay món trên thớt bạn đang nhìn");
            Vi(Special.LuckyKnife, "DAO MAY MẮN", "Mỗi nhát thái có 10% cơ hội thái xong món ngay (nhanh hơn khoảng 30-40%)");
            Vi(Special.PrepCook, "PHỤ BẾP", "Cứ 15 s, nguyên liệu bạn lấy từ thùng đã được thái sẵn");
            Vi(Special.DashChop, "LƯỚT-THÁI", "Lướt ngang thớt có đồ sẽ thái nó 3 nhát (mỗi thớt một lần mỗi 2 s)");
            Vi(Special.WashBurst, "RỬA CẤP TỐC", "Rửa sạch ngay mọi đĩa trong bồn rửa bạn đang nhìn (trừ khi giá phơi đã đầy)");
            Vi(Special.Dishwasher, "MÁY RỬA BÁT", "Cứ món thứ hai bạn giao, đĩa về sạch trên giá phơi của bồn rửa");
            Vi(Special.Butler, "QUẢN GIA", "Đĩa bẩn của bạn quay về nhanh gấp đôi, thẳng vào bồn rửa");
            Vi(Special.PlateConjurer, "HÓA PHÉP ĐĨA", "Hóa ra một đĩa sạch trên tay không của bạn; giao món xong thì đĩa biến mất");
            Vi(Special.SparePan, "CHẢO DỰ PHÒNG", "Hóa ra bản sao của nồi, chảo hay bát rỗng bạn đang nhìn (mỗi lúc một cái); đổ ra hết thì biến mất");

            // 4. Cooking and fire
            Vi(Special.HotHands, "TAY NÓNG", "Nồi hay chảo bạn cầm nấu nhanh hơn trên bếp 25% và dừng khi chín");
            Vi(Special.ColdHands, "TAY LẠNH", "Nồi, chảo bạn cầm hoặc cho đồ vào chờ 8 s khi chín rồi mới cháy");
            Vi(Special.Rescue, "CỨU NGUY", "Hết cháy nồi hay chảo bạn cầm hoặc đang nhìn, dập lửa bếp của nó và giữ chín 10 s");
            Vi(Special.Overclock, "ÉP XUNG", "Bếp hoặc máy trộn bạn đang nhìn chạy (và làm cháy) nhanh gấp ba trong 10 s");
            Vi(Special.SizzleMaster, "VUA XÈO XÈO", "Nồi, chảo bạn cầm hoặc cho đồ vào nấu (và cháy) nhanh gấp đôi");
            Vi(Special.SteadyMixer, "TRỘN ĐỀU TAY", "Bát bạn cầm hoặc cho đồ vào trộn nhanh hơn 75%, không bao giờ quá tay");
            Vi(Special.Fireproof, "CHỐNG CHÁY", "Đi xuyên qua lửa, và lửa sát cạnh bạn tắt sau 2 s");
            Vi(Special.FireMarshal, "LÍNH CỨU HỎA", "Bếp dưới nồi hay chảo bạn từng cầm hoặc cho đồ vào không bao giờ bắt lửa, đồ trong đó cháy chậm gấp đôi");
            Vi(Special.FireBlast, "DẬP LỬA", "Dập ngay mọi đám lửa trong vòng 6 m");
            Vi(Special.BuiltInExtinguisher, "TAY VÒI NƯỚC", "Giữ DÙNG khi tay không và nhìn đám lửa để phun như bình chữa cháy");
            Vi(Special.SlowSpread, "LỬA CHẬM LAN", "Lửa không lan được trong vòng 7 m quanh bạn");

            // 5. Orders and score
            Vi(Special.Charmer, "DUYÊN DÁNG", "Món bạn giao được +50% tiền boa");
            Vi(Special.MaitreD, "QUẢN LÝ SẢNH", "Mọi đơn kéo dài thêm 25% (cả đội; màn có đơn tính giờ)");
            Vi(Special.Clutch, "PHÚT CHÓT", "Món giao trong 20% thời gian cuối của đơn được x1.5 điểm");
            Vi(Special.ComboKeeper, "GIỮ CHUỖI", "Lỡ đơn hay giao sai món vẫn giữ chuỗi tiền boa (mỗi 25 s; không đi cùng CƠ HỘI THỨ HAI)");
            Vi(Special.SecondChance, "CƠ HỘI THỨ HAI", "Lần hết giờ đầu của một đơn không mất gì, giữ chuỗi (mỗi 30 s; không đi cùng GIỮ CHUỖI)");
            Vi(Special.Gambler, "CON BẠC", "Tiền boa của bạn được nhân ngẫu nhiên 0, 1, 2 hoặc 3");
            Vi(Special.FreshDelivery, "GIAO NÓNG HỔI", "Món giao trong 25 s sau khi phần đầu tiên nấu xong được +50% tiền boa");
            Vi(Special.Reroll, "ĐỔI ĐƠN", "Đổi đơn cũ nhất sang một món khác, không mất gì (cả đội)");
            Vi(Special.TimeOut, "TẠM DỪNG", "Đồng hồ các đơn (và đơn mới) dừng 10 s (cả đội)");
            Vi(Special.CloseEnough, "TẠM CHẤP NHẬN", "Cứ 45 s, một món không ai gọi được tính là đơn cũ nhất");
            Vi(Special.FortuneTeller, "THẦY BÓI", "Đơn tiếp theo hiện sớm trên thanh đơn và có thêm 10% thời gian (trừ màn nhiều giai đoạn)");
            Vi(Special.BountyHunter, "SĂN TIỀN THƯỞNG", "Mỗi lúc một đơn được viền vàng: giao nó được x1.5 điểm (đơn kế sau 15 s)");
            Vi(Special.GoldenHour, "GIỜ VÀNG", "Trong 60 s cuối màn, tiền boa của bạn được nhân đôi");

            // 6. Team support
            Vi(Special.RallyCry, "HIỆU TRIỆU", "Bạn và mọi đồng đội chạy nhanh hơn 30% trong 8 s");
            Vi(Special.Inspiring, "TRUYỀN LỬA", "Đồng đội trong vòng 3.5 m quanh bạn thái và rửa nhanh hơn khoảng 25% (không tính đầu bếp kia của bạn khi chơi một mình)");
            Vi(Special.Medic, "CỨU THƯƠNG", "Đồng đội tay không bị rơi hồi sinh cạnh bạn sau 1 s; bạn hồi sinh tối đa 2.5 s");
            Vi(Special.Bodyguard, "VỆ SĨ", "Đồng đội trong vòng 6 m quanh bạn không bị đẩy lùi hay trượt ngã");
            Vi(Special.Battery, "CỤC PIN", "Kỹ năng chủ động (hồi từ 5 s) của bạn cắt hồi chiêu dài nhất của một đồng đội, tối đa nửa hồi chiêu đó và 65% hồi chiêu của bạn (tối đa 6.5 s); mỗi 10 s một lần");
            Vi(Special.BuddySystem, "CẠ CỨNG", "Ở gần đồng đội gần nhất (5 m) cả hai chạy nhanh hơn 15% và chia nhau hiệu ứng tăng tốc");
            Vi(Special.Quartermaster, "HẬU CẦN", "Đồng đội lấy đồ từ thùng trong vòng 5 m quanh bạn được thêm một phần, đặt lên quầy cạnh bạn (5 s)");

            // 7. Temporary buffs
            Vi(Special.Frenzy, "CUỒNG NHIỆT", "Mọi kỹ năng +2 trong 10 s, rồi -1 trong 4 s");
            Vi(Special.Overdrive, "BỨT TỐC", "Lướt không cần hồi và +20% tốc độ trong 6 s");
            Vi(Special.SecondWind, "HỒI SỨC", "Trong 60 s cuối, mọi kỹ năng +1 (CHẠY và LƯỚT +3)");
            Vi(Special.Adrenaline, "ADRENALINE", "Sau khi bị đẩy, trượt, hồi sinh hay đội mắc lỗi: +50% tốc độ chạy, lướt không cần hồi trong 8 s");
            Vi(Special.Zen, "THIỀN", "Đứng yên (không làm gì) 1.5 s khi đang điều khiển: nhát thái hoặc lần rửa kế tiếp xong ngay");
            Vi(Special.CoffeeRun, "CÀ PHÊ SỮA ĐÁ", "Mỗi món đúng bạn giao cho bạn +25% tốc độ chạy trong 10 s");
            Vi(Special.GlassCannon, "PHÁO THỦY TINH", "Mọi kỹ năng +1, nhưng bị đẩy (kể cả bị chặn), trượt, rơi hay đội mắc lỗi thì -1 trong 20 s");
            Vi(Special.SlowStarter, "KHỞI ĐỘNG CHẬM", "Mọi kỹ năng -1 trong 45 s đầu, sau đó +1");
            Vi(Special.UnderPressure, "ÁP LỰC", "+7% tốc độ chạy cho mỗi đơn đang chờ từ đơn thứ ba, tối đa +21%");
            Vi(Special.LuckyCharm, "BÙA MAY MẮN", "Mỗi nhát thái (hoặc nửa giây rửa) có 25% cơ hội tính gấp đôi");
            Vi(Special.HotStreak, "ĐANG NÓNG TAY", "Đội giao liên tiếp không lỡ: +3% tốc độ mỗi đơn (tối đa 15%); tới 8, mọi kỹ năng +1 trừ CHẠY và LƯỚT");

            // 8. Level hazards and vehicles
            Vi(Special.PortalPro, "CAO THỦ DI CHUYỂN", "Sau cổng dịch chuyển, pháo hay bệ lái: chạy nhanh hơn 40%, lướt không cần hồi 4 s; pháo không chờ ngòi, lái nhanh hơn 50%");
            Vi(Special.Cannoneer, "PHÁO THỦ", "Pháo bắn bạn đi ngay khi bấm nút, không phải chờ ngòi");
            Vi(Special.Driver, "TÀI XẾ", "Bệ và xe bạn lái chạy nhanh hơn 50%");
            Vi(Special.Weatherproof, "CHỊU GIÓ", "Gió và sàn di chuyển chỉ đẩy bạn theo hướng bạn nhìn");
            Vi(Special.Lifeguard, "CỨU HỘ", "Đồng đội rơi trong vòng 7 m quanh bạn được đưa trở lại ngay");

            // 9. Versus / party
            Vi(Special.Shove, "XÔ ĐẨY", "Cú lướt của bạn đẩy mạnh hơn và làm đầu bếp đánh rơi đồ đang cầm (đối kháng: chỉ đối thủ)");
            Vi(Special.Pickpocket, "MÓC TÚI", "Lướt vào đầu bếp để lấy đồ họ đang cầm (đối kháng: chỉ đối thủ; trừ người có TAY DÍNH)");
            Vi(Special.GreaseTrail, "VỆT DẦU", "Trong 4 s bạn để lại vệt trơn; đầu bếp giẫm lên sẽ trượt (đối kháng: chỉ đối thủ)");
            Vi(Special.SneezeBomb, "BOM HẮT XÌ", "Mọi đầu bếp trong vòng 4 m quanh bạn (cả đồng đội) hắt xì, đánh rơi đồ và đứng hình 2 s");
            Vi(Special.Hex, "NGUYỀN RỦA", "Đầu bếp bạn đang nhìn (5 m): 10 nhát thái tiếp theo (trong 30 s) không tính, rửa đĩa chậm mất 3 s");
            Vi(Special.DecoyOrder, "ĐƠN GIẢ", "Thêm một đơn giả vào danh sách của đội đối thủ trong 25 s (không ra điểm)");

            // 10. Quirks
            Vi(Special.HeavyBoots, "GIÀY NẶNG", "Bạn không lướt được (LƯỚT không hạ dưới 0 được)");
            Vi(Special.Clumsy, "VỤNG VỀ", "Lướt là đánh rơi đĩa đang cầm (sạch, bẩn hay có món)");
            Vi(Special.WeakArm, "TAY YẾU", "Bạn không ném được (NÉM không hạ dưới 0 được)");
            Vi(Special.Butterfingers, "TAY TRƠN", "Bạn không bắt được đồ bị ném");
            Vi(Special.Pyrophobe, "SỢ LỬA", "Bình chữa cháy không phun khi bạn dùng");
            Vi(Special.Germaphobe, "SỢ BẨN", "Bạn không rửa được đĩa (RỬA ĐĨA không hạ dưới 0 được)");
            Vi(Special.Vegetarian, "ĂN CHAY", "Bạn không nhặt hay bắt thịt, cá sống");
            Vi(Special.Sleepy, "BUỒN NGỦ", "Cứ 30 s bạn ngủ gật 2 s: không đi, thái hay rửa được");
            Vi(Special.Fragile, "MONG MANH", "Hồi sinh lâu hơn 5 s");
            Vi(Special.ShowOff, "THÍCH KHOE", "Sau mỗi món bạn giao, bạn tạo dáng 1 s");
            Vi(Special.TunnelVision, "MỘT VIỆC THÔI", "Bạn chỉ làm được ở thớt hay bồn rửa bạn dùng đầu tiên");
            Vi(Special.LoneWolf, "SÓI ĐƠN ĐỘC", "Kỹ năng đặc biệt và hào quang của đồng đội không ảnh hưởng tới bạn");
        }

        private static void Vi(Special id, string name, string description)
        {
            SpecialInfo info;
            if (!Infos.TryGetValue(id, out info)) return;
            info.NameVi = name;
            info.DescriptionVi = description;
        }

        private static void A(Special id, string name, string description, float cooldown, int group = 0,
            bool teamWide = false, bool versus = false)
        {
            Add(new SpecialInfo { Id = id, NameEn = name, NameVi = name, DescriptionEn = description,
                DescriptionVi = description, Kind = SpecialKind.Active,
                Cooldown = cooldown, Group = group, TeamWide = teamWide, Versus = versus });
        }

        private static void P(Special id, string name, string description, int level = 0, int group = 0,
            bool teamWide = false, bool versus = false, bool hidden = false)
        {
            // A passive that can be picked must take part of the budget; only retired ones have none.
            if (!hidden && (level < 1 || level > PassiveBudget))
                throw new ArgumentOutOfRangeException("level", id + " needs a level from 1 to " + PassiveBudget);
            Add(new SpecialInfo { Id = id, NameEn = name, NameVi = name, DescriptionEn = description,
                DescriptionVi = description, Kind = SpecialKind.Passive, Level = level,
                Group = group, TeamWide = teamWide, Versus = versus, Hidden = hidden });
        }

        private static void Q(Special id, string name, string description, int refund, int group)
        {
            Add(new SpecialInfo { Id = id, NameEn = name, NameVi = name, DescriptionEn = description,
                DescriptionVi = description, Kind = SpecialKind.Quirk,
                Refund = refund, Group = group });
        }

        private static void Add(SpecialInfo info)
        {
            Infos[info.Id] = info;
            Ordered.Add(info);
        }

        internal static void Bind(ConfigFile config)
        {
            _enabled = config.Bind("Specials", "Enabled", true,
                "Cho phép chọn kỹ năng đặc biệt (tới 2 chủ động, tới 3 bị động có tổng cấp tối đa 3, 1 tật). Khi chơi trực tuyến dùng cài đặt của chủ phòng.");
            _versus = config.Bind("Specials", "VersusSpecials", false,
                "Cho phép các kỹ năng đặc biệt phá đối thủ (Shove, Pickpocket, Grease Trail, Sneeze Bomb, Hex, Decoy Order). Dùng cài đặt của chủ phòng.");
            _secondActiveCooldown = config.Bind("Specials", "SecondActiveCooldown", 1.25f,
                "Đầu bếp có hai kỹ năng chủ động: hồi chiêu nhân với số này (1.25 = lâu hơn 25%; không tính Pocket, Pack Mule hay kỹ năng mỗi màn một lần). Dùng cài đặt của chủ phòng.");
        }

        /// <summary>
        /// Cooldowns of a chef with two actives are this much longer (host: it sets every
        /// cooldown, so its value is what everyone gets).
        /// </summary>
        internal static float SecondActiveCooldown
        {
            get
            {
                if (s_hostSecondActive.HasValue) return s_hostSecondActive.Value;
                return _secondActiveCooldown != null ? Mathf.Clamp(_secondActiveCooldown.Value, 1f, 3f) : 1.25f;
            }
        }

        internal static bool Enabled
        {
            get { return s_hostEnabled ?? (_enabled == null || _enabled.Value); }
        }

        internal static bool VersusAllowed
        {
            get { return s_hostVersus ?? (_versus != null && _versus.Value); }
        }

        /// <summary>
        /// This machine's switches as the host publishes them: flag bits, with the second-active
        /// cooldown tax in hundredths above them (so every machine shows the cooldowns the host sets).
        /// </summary>
        internal static int OwnFlags()
        {
            int tax = Mathf.RoundToInt((_secondActiveCooldown != null ? Mathf.Clamp(_secondActiveCooldown.Value, 1f, 3f) : 1.25f) * 100f);
            return (_enabled == null || _enabled.Value ? 1 : 0) | (_versus != null && _versus.Value ? 2 : 0) | (tax << 2);
        }

        /// <summary>Uses the host's switches (null: this machine's own config).</summary>
        internal static void UseHostFlags(int? flags)
        {
            if (flags == null)
            {
                s_hostEnabled = null;
                s_hostVersus = null;
                s_hostSecondActive = null;
                return;
            }
            s_hostEnabled = (flags.Value & 1) != 0;
            s_hostVersus = (flags.Value & 2) != 0;
            int tax = flags.Value >> 2;
            s_hostSecondActive = tax >= 100 && tax <= 300 ? tax / 100f : (float?)null;
        }

        internal static SpecialInfo Info(Special id)
        {
            SpecialInfo info;
            return Infos.TryGetValue(id, out info) ? info : null;
        }

        internal static string NameOf(Special id)
        {
            SpecialInfo info = Info(id);
            return info != null ? info.Name : Lang.T("NONE", "KHÔNG CÓ");
        }

        /// <summary>The English name whatever the language (the log, the icon letters).</summary>
        internal static string EnglishNameOf(Special id)
        {
            SpecialInfo info = Info(id);
            return info != null ? info.NameEn : "NONE";
        }

        internal static SpecialKind KindFor(SpecialSlot slot)
        {
            switch (slot)
            {
                case SpecialSlot.Active:
                case SpecialSlot.Active2: return SpecialKind.Active;
                case SpecialSlot.Quirk: return SpecialKind.Quirk;
                default: return SpecialKind.Passive;
            }
        }

        /// <summary>The slot shown on card row <paramref name="row"/> (0 = the first special row).</summary>
        internal static SpecialSlot SlotAtRow(int row)
        {
            return row >= 0 && row < RowOrder.Length ? (SpecialSlot)RowOrder[row] : (SpecialSlot)row;
        }

        /// <summary>The special in active slot <paramref name="index"/> (0 or 1) of a loadout.</summary>
        internal static Special ActiveIn(int[] loadout, int index)
        {
            if (loadout == null || index < 0 || index >= ActiveSlots.Length) return Special.None;
            int slot = ActiveSlots[index];
            return slot < loadout.Length ? (Special)loadout[slot] : Special.None;
        }

        /// <summary>Does the loadout have an active in both slots?</summary>
        internal static bool TwoActives(int[] loadout)
        {
            return ActiveIn(loadout, 0) != Special.None && ActiveIn(loadout, 1) != Special.None;
        }

        /// <summary>A passive's level (0 for anything else).</summary>
        internal static int LevelOf(Special id)
        {
            SpecialInfo info = Info(id);
            return info != null && info.Kind == SpecialKind.Passive ? info.Level : 0;
        }

        /// <summary>The levels of the loadout's passives, leaving out <paramref name="except"/> (-1: none left out).</summary>
        internal static int PassiveLevels(int[] loadout, int except)
        {
            int total = 0;
            if (loadout == null) return 0;
            foreach (int slot in PassiveSlots)
                if (slot != except && slot < loadout.Length) total += LevelOf((Special)loadout[slot]);
            return total;
        }

        /// <summary>Passive levels still free for <paramref name="slot"/>, counting what the other passive slots hold.</summary>
        internal static int BudgetLeft(int[] loadout, SpecialSlot slot)
        {
            return PassiveBudget - PassiveLevels(loadout, (int)slot);
        }

        /// <summary>Is <paramref name="candidate"/> a passive too heavy for what the loadout's other passives leave?</summary>
        internal static bool OverBudget(int[] loadout, SpecialSlot slot, Special candidate)
        {
            int level = LevelOf(candidate);
            return level > 0 && level > BudgetLeft(loadout, slot);
        }

        /// <summary>A loadout with nothing picked.</summary>
        internal static int[] Empty()
        {
            return new int[SlotCount];
        }

        /// <summary>Skill points the loadout's quirk frees.</summary>
        internal static int Refund(int[] loadout)
        {
            if (loadout == null || loadout.Length < SlotCount) return 0;
            SpecialInfo quirk = Info((Special)loadout[(int)SpecialSlot.Quirk]);
            return quirk != null && quirk.Kind == SpecialKind.Quirk ? quirk.Refund : 0;
        }

        /// <summary>
        /// Can <paramref name="candidate"/> go in <paramref name="slot"/> next to the loadout's
        /// other picks? (Right kind, offered under the current rules, no clash of groups, not the
        /// same passive twice, and the passives' levels stay within the budget.)
        /// </summary>
        internal static bool Fits(int[] loadout, SpecialSlot slot, Special candidate)
        {
            if (candidate == Special.None) return true;
            SpecialInfo info = Info(candidate);
            if (info == null || info.Kind != KindFor(slot) || info.Hidden) return false;
            if (info.Versus && !VersusAllowed) return false;
            if (OverBudget(loadout, slot, candidate)) return false;
            for (int i = 0; i < SlotCount; i++)
            {
                if (i == (int)slot || loadout == null || i >= loadout.Length) continue;
                Special other = (Special)loadout[i];
                if (other == Special.None) continue;
                if (other == candidate) return false;
                SpecialInfo otherInfo = Info(other);
                if (otherInfo != null && info.Group != 0 && otherInfo.Group == info.Group) return false;
                if (Clash(candidate, other)) return false;
            }
            return true;
        }

        /// <summary>
        /// Which of the loadout's other picks stops <paramref name="candidate"/> going in
        /// <paramref name="slot"/> (None when it fits, or when nothing else is the reason).
        /// </summary>
        internal static Special ClashWith(int[] loadout, SpecialSlot slot, Special candidate)
        {
            SpecialInfo info = Info(candidate);
            if (info == null || loadout == null) return Special.None;
            for (int i = 0; i < SlotCount && i < loadout.Length; i++)
            {
                if (i == (int)slot) continue;
                Special other = (Special)loadout[i];
                if (other == Special.None) continue;
                SpecialInfo otherInfo = Info(other);
                if (other == candidate || Clash(candidate, other) ||
                    (otherInfo != null && info.Group != 0 && otherInfo.Group == info.Group)) return other;
            }
            return Special.None;
        }

        /// <summary>The categories specials are grouped in (by their number), in order.</summary>
        internal static string[] CategoryNames
        {
            get
            {
                if (Lang.Vi)
                    return new[]
                    {
                        "DI CHUYỂN", "BƯNG & NÉM", "SƠ CHẾ", "NẤU & LỬA", "ĐƠN & ĐIỂM",
                        "HỖ TRỢ ĐỘI", "TĂNG SỨC", "BẪY & XE", "ĐỐI KHÁNG", "TẬT",
                    };
                return new[]
                {
                    "MOVEMENT", "CARRYING & THROWING", "PREP STATIONS", "COOKING & FIRE", "ORDERS & SCORE",
                    "TEAM SUPPORT", "BOOSTS", "HAZARDS & VEHICLES", "VERSUS", "QUIRKS",
                };
            }
        }

        private static readonly int[] CategoryLast = { 20, 36, 47, 58, 71, 78, 89, 94, 100, 112 };

        /// <summary>The special's category (index into CategoryNames); -1 for None.</summary>
        internal static int CategoryOf(Special special)
        {
            int n = (int)special;
            if (n <= 0) return -1;
            for (int i = 0; i < CategoryLast.Length; i++)
                if (n <= CategoryLast[i]) return i;
            return CategoryLast.Length - 1;
        }

        private static bool Clash(Special a, Special b)
        {
            foreach (Special[] set in Clashes)
            {
                if (set[0] == a && Array.IndexOf(set, b) > 0) return true;
                if (set[0] == b && Array.IndexOf(set, a) > 0) return true;
            }
            return false;
        }

        /// <summary>
        /// The level being picked for has rival teams (set by the skill screen as it opens). Only
        /// changes what the picker offers, never what a loadout may hold, so machines can't disagree.
        /// </summary>
        internal static bool CompetitiveLevel;

        /// <summary>The choices for a slot in the order Left/Right steps through them, "none" first.</summary>
        internal static List<Special> Choices(SpecialSlot slot)
        {
            var list = new List<Special> { Special.None };
            SpecialKind kind = KindFor(slot);
            foreach (SpecialInfo info in Ordered)
            {
                if (info.Kind != kind || info.Hidden || (info.Versus && !VersusAllowed)) continue;
                // A fake order for the rival team does nothing without one.
                if (info.Id == Special.DecoyOrder && !CompetitiveLevel) continue;
                list.Add(info.Id);
            }
            return list;
        }

        /// <summary>
        /// Does the loadout's quirk take <paramref name="skill"/> away entirely (so it can't be
        /// lowered below 0: Heavy Boots and DASHING, Weak Arm and THROWING, Germaphobe and WASHING)?
        /// </summary>
        internal static bool LocksSkill(int[] loadout, Skill skill)
        {
            if (loadout == null || loadout.Length < SlotCount) return false;
            Skill locked;
            return LockedSkills.TryGetValue((Special)loadout[(int)SpecialSlot.Quirk], out locked) && locked == skill;
        }

        /// <summary>The quirk that keeps <paramref name="skill"/> at 0 or above in this loadout (None if none).</summary>
        internal static Special SkillLock(int[] loadout, Skill skill)
        {
            return LocksSkill(loadout, skill) ? (Special)loadout[(int)SpecialSlot.Quirk] : Special.None;
        }

        /// <summary>
        /// Specials that can't be in the same kitchen as <paramref name="special"/> whoever picks
        /// them (Second Chance and Combo Keeper): the select screen treats them as taken.
        /// </summary>
        internal static IEnumerable<Special> KitchenExclusiveWith(Special special)
        {
            foreach (Special[] set in KitchenExclusive)
            {
                if (Array.IndexOf(set, special) < 0) continue;
                foreach (Special other in set)
                    if (other != special) yield return other;
            }
        }

        /// <summary>
        /// A loadout under the rules in use: unknown, wrong-kind, versus-when-off, clashing or
        /// over-budget picks are dropped (later slots give way to earlier ones, so the third passive
        /// goes first), and nothing at all when specials are turned off. Every machine runs this on
        /// every loadout, so they all agree.
        /// </summary>
        internal static int[] Enforce(int[] loadout)
        {
            int[] result = Empty();
            if (loadout == null || !Enabled) return result;
            for (int i = 0; i < SlotCount && i < loadout.Length; i++)
            {
                Special pick = (Special)loadout[i];
                if (pick != Special.None && Fits(result, (SpecialSlot)i, pick)) result[i] = (int)pick;
            }
            return result;
        }

        internal static string Format(int[] loadout)
        {
            var parts = new string[SlotCount];
            for (int i = 0; i < SlotCount; i++)
                parts[i] = (loadout != null && i < loadout.Length ? loadout[i] : 0).ToString(CultureInfo.InvariantCulture);
            return string.Join(".", parts);
        }

        internal static int[] Parse(string text)
        {
            int[] result = Empty();
            if (string.IsNullOrEmpty(text)) return result;
            string[] parts = text.Split('.');
            for (int i = 0; i < SlotCount && i < parts.Length; i++)
            {
                int value;
                if (int.TryParse(parts[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out value) &&
                    Infos.ContainsKey((Special)value))
                    result[i] = value;
            }
            return result;
        }

        /// <summary>"BLINK, LONG ARMS, CHARMER, HEAVY BOOTS" for the log.</summary>
        internal static string Summary(int[] loadout)
        {
            var names = new List<string>();
            for (int i = 0; loadout != null && i < loadout.Length; i++)
                if (loadout[i] != 0) names.Add(EnglishNameOf((Special)loadout[i]));
            return names.Count > 0 ? string.Join(", ", names.ToArray()) : "không có kỹ năng đặc biệt";
        }
    }
}
