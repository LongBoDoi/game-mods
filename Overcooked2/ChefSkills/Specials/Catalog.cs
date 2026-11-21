using System;
using System.Collections.Generic;
using System.Globalization;
using BepInEx.Configuration;

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

    /// <summary>The four picks on a card, in the order they are shown and sent.</summary>
    internal enum SpecialSlot { Active = 0, Passive1 = 1, Passive2 = 2, Quirk = 3 }

    internal sealed class SpecialInfo
    {
        public Special Id;
        public string Name;
        public string Description;
        public SpecialKind Kind;
        /// <summary>Actives: seconds before it can be used again.</summary>
        public float Cooldown;
        /// <summary>Quirks: skill points it frees.</summary>
        public int Refund;
        /// <summary>Only one chef in the kitchen can have it (its effect covers the whole team).</summary>
        public bool TeamWide;
        /// <summary>Only offered when the host turns on versus/party specials.</summary>
        public bool Versus;
        /// <summary>Picks in the same group exclude each other (0 = no group).</summary>
        public int Group;
    }

    /// <summary>
    /// What each special is, how it is picked, and which picks go together. A chef has one active
    /// (used with the emote button), two passives and optionally one quirk (a drawback that frees
    /// skill points). Specials cost no points.
    /// </summary>
    internal static class SpecialCatalog
    {
        internal const int SlotCount = 4;

        // Groups of specials that exclude each other.
        private const int DashStyle = 1, Size = 2, ThrowPath = 3;

        // Picks that clash although they share no group, mostly a quirk that takes away what a
        // special needs: the first entry of each set clashes with each of the others.
        private static readonly Special[][] Clashes =
        {
            new[] { Special.HeavyBoots, Special.Sprinter, Special.DoubleDash, Special.ChargeDash, Special.Hover,
                Special.DashChop, Special.Overdrive, Special.Shove, Special.Pickpocket, Special.PortalPro },
            new[] { Special.WeakArm, Special.StrongArm, Special.HomingThrow, Special.Lob, Special.AssemblyLine, Special.Juggler },
            new[] { Special.Butterfingers, Special.CatchersMitt, Special.Juggler },
            new[] { Special.Pyrophobe, Special.BuiltInExtinguisher },
            new[] { Special.Germaphobe, Special.WashBurst },
            new[] { Special.Clumsy, Special.StickyFingers },
        };

        private static readonly Dictionary<Special, SpecialInfo> Infos = new Dictionary<Special, SpecialInfo>();
        private static readonly List<SpecialInfo> Ordered = new List<SpecialInfo>();

        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<bool> _versus;

        // Online, the host's switches replace this machine's own (null: use the config).
        private static bool? s_hostEnabled;
        private static bool? s_hostVersus;

        internal static readonly string[] SlotNames = { "ACTIVE", "PASSIVE", "PASSIVE", "QUIRK" };

        internal static readonly string[] SlotHelp =
        {
            "Your active special: press EMOTE in the kitchen to use it",
            "A passive special: always on",
            "A second passive special: always on",
            "Optional drawback that gives you skill points to spend",
        };

        static SpecialCatalog()
        {
            // 1. Movement
            A(Special.Vault, "VAULT", "Hop over the counter you face", 6f);
            A(Special.Blink, "BLINK", "Teleport 3 m forward, stopping before walls and edges", 8f);
            P(Special.Sprinter, "SPRINTER", "Hold DASH to sprint +50% (3 s of stamina) instead of dashing", group: DashStyle);
            P(Special.DoubleDash, "DOUBLE DASH", "Two dash charges: chain two dashes back to back", group: DashStyle);
            P(Special.ChargeDash, "CHARGE DASH", "Hold DASH to charge, release for up to twice the distance", group: DashStyle);
            P(Special.Momentum, "MOMENTUM", "Running without stopping builds up to +30% speed");
            P(Special.Slipstream, "SLIPSTREAM", "Teammates running close behind you get +20% speed");
            P(Special.SureFooted, "SURE-FOOTED", "No slipping on ice or puddles, no drag from wind or moving walkways");
            P(Special.Immovable, "IMMOVABLE", "Dashes, thrown items and fire can't knock you back");
            P(Special.Ghost, "GHOST", "Walk straight through other chefs");
            A(Special.Recall, "RECALL", "Plant a flag; use again within 20 s to teleport back to it (cooldown starts on return)", 25f);
            A(Special.Swap, "SWAP", "Trade places with the teammate you face (or the nearest)", 20f);
            A(Special.Summon, "SUMMON", "Pull the teammate you face (or the nearest) to your side", 25f);
            P(Special.QuickRespawn, "QUICK RESPAWN", "Respawn after 1.5 s instead of 5 s");
            P(Special.SafetyNet, "SAFETY NET", "Once every 30 s, falling off puts you back where you fell");
            P(Special.ConveyorRider, "CONVEYOR RIDER", "Conveyors and moving floors carry you twice as fast");
            P(Special.BigChef, "BIG CHEF", "Bigger: can't be knocked, knock others further, -10% speed", group: Size);
            P(Special.TinyChef, "TINY CHEF", "Smaller: +12% speed, but knocks send you further", group: Size);
            P(Special.Hover, "HOVER", "Dash across a gap up to one tile wide (10 s recharge)");
            A(Special.Grapple, "GRAPPLE", "Pull yourself to the counter you face, up to 6 m away", 12f);

            // 2. Carrying and throwing
            A(Special.PackMule, "PACK MULE", "Carry a second item on your back; EMOTE swaps hand and back", 0.5f);
            A(Special.Pocket, "POCKET", "Keep one item hidden in your pocket; EMOTE swaps it with your hand", 0.5f);
            P(Special.Waiter, "WAITER", "Put a dish onto another dish to carry both; hand one in and the other goes in too");
            P(Special.LongArms, "LONG ARMS", "Pick up, put down, chop and wash one tile further away");
            P(Special.Magnet, "MAGNET", "Loose items on the floor within 3 m slide towards you");
            A(Special.Telekinesis, "TELEKINESIS", "Pull the item you face, up to 6 m away, into your hands", 10f);
            P(Special.BulkBuyer, "BULK BUYER", "Taking from a crate also puts a spare on a counter (15 s)");
            P(Special.CatchersMitt, "CATCHER'S MITT", "Catch from any direction and further away");
            P(Special.StickyFingers, "STICKY FINGERS", "Keep what you hold when shoved, and even when you fall off the map");
            P(Special.StrongArm, "STRONG ARM", "Throw anything: pots, pans, plates and dishes fly off as loose items (no catching)");
            P(Special.HomingThrow, "HOMING THROW", "Thrown items bend towards the teammate you face", group: ThrowPath);
            P(Special.Lob, "LOB", "Throws arc high over counters and chefs (same distance)", group: ThrowPath);
            P(Special.AssemblyLine, "ASSEMBLY LINE", "Taking from a crate facing a teammate throws it to them");
            P(Special.Juggler, "JUGGLER", "Re-throw within 0.5 s of a catch for a 50% longer throw");
            P(Special.Rebound, "REBOUND", "Uncaught throws that land near you go onto the nearest empty counter");
            P(Special.HandOff, "HAND-OFF", "PICK UP facing a teammate takes their item or gives yours");

            // 3. Prep stations
            P(Special.Multitask, "MULTITASK", "A board or sink you leave keeps working at 25% speed");
            P(Special.EchoKnife, "ECHO KNIFE", "Each chop also chops the last other board you used");
            A(Special.PowerChop, "POWER CHOP", "Finish the item on the board you face at once", 15f);
            P(Special.LuckyKnife, "LUCKY KNIFE", "Each chop has a 10% chance to finish the item at once");
            P(Special.PrepCook, "PREP COOK", "Every 20 s, an ingredient you take from a crate comes chopped");
            P(Special.DashChop, "DASH-CHOP", "Dashing past a loaded board you face chops it once");
            A(Special.WashBurst, "WASH BURST", "Wash every plate in the sink you face at once", 40f);
            P(Special.Dishwasher, "DISHWASHER", "Every second dish you hand in comes back clean, on a sink's drying rack");
            P(Special.Butler, "BUTLER", "Your dirty plates come back straight into a sink");
            A(Special.PlateConjurer, "PLATE CONJURER", "Conjure a clean plate in your empty hands", 30f);
            A(Special.SparePan, "SPARE PAN", "Conjure a copy of the empty pan or pot you face (once per level)", 9999f);

            // 4. Cooking and fire
            P(Special.HotHands, "HOT HANDS", "A pot or pan you carry keeps cooking at half speed and never burns");
            P(Special.ColdHands, "COLD HANDS", "Picking up or putting down a pot or pan pauses its burning for 6 s");
            A(Special.Rescue, "RESCUE", "Turn the burnt (or burning) pot or pan you hold or face back to cooked", 45f);
            A(Special.Overclock, "OVERCLOCK", "The cooker or mixer you face works twice as fast for 10 s", 30f);
            P(Special.SizzleMaster, "SIZZLE MASTER", "Your pots and pans cook twice as fast but burn twice as fast");
            P(Special.SteadyMixer, "STEADY MIXER", "Mixers you load never over-mix");
            P(Special.Fireproof, "FIREPROOF", "Flames can't knock you back or block you: walk right through them");
            P(Special.FireMarshal, "FIRE MARSHAL", "Cookers you last put a pot or food on never catch fire (food still burns)");
            A(Special.FireBlast, "FIRE BLAST", "Put out every fire within 3 m at once", 30f);
            P(Special.BuiltInExtinguisher, "HOSE HANDS", "Hold USE with empty hands facing a fire to spray it like an extinguisher");
            P(Special.SlowSpread, "SLOW SPREAD", "Fire near you spreads half as fast");

            // 5. Orders and score
            P(Special.Charmer, "CHARMER", "Dishes you hand in give +50% tip");
            P(Special.MaitreD, "MAITRE D'", "Every order lasts 15% longer (whole team)", teamWide: true);
            P(Special.Clutch, "CLUTCH", "A dish handed in during its order's last 15% scores double");
            P(Special.ComboKeeper, "COMBO KEEPER", "Once per level, a missed order keeps the tip streak", teamWide: true);
            P(Special.SecondChance, "SECOND CHANCE", "The first order that expires costs no points", teamWide: true);
            P(Special.Gambler, "GAMBLER", "Your tips are multiplied by 0, 1, 2 or 3 at random");
            P(Special.FreshDelivery, "FRESH DELIVERY", "A dish handed in within 15 s of cooking gets +40% tip");
            A(Special.Reroll, "REROLL", "Swap the newest order for a new random one", 45f);
            A(Special.TimeOut, "TIME OUT", "Order timers (and new orders) stop for 6 s", 60f, teamWide: true);
            P(Special.CloseEnough, "CLOSE ENOUGH", "Every 60 s, a dish from this level nobody ordered counts as the oldest order", teamWide: true);
            P(Special.FortuneTeller, "FORTUNE TELLER", "See the next order before it arrives (not on levels with phases)");
            P(Special.BountyHunter, "BOUNTY HUNTER", "One order at a time is marked; handing it in scores double", teamWide: true);
            P(Special.GoldenHour, "GOLDEN HOUR", "In the last 30 s of the level, your tips are doubled");

            // 6. Team support
            A(Special.RallyCry, "RALLY CRY", "Every teammate runs 30% faster for 6 s", 30f);
            P(Special.Inspiring, "INSPIRING", "Teammates within 3 m of you chop and wash about 20% faster", teamWide: true);
            P(Special.Medic, "MEDIC", "Teammates who fall respawn next to you after 1 s");
            P(Special.Bodyguard, "BODYGUARD", "Teammates within 3 m of you can't be knocked back or slip");
            P(Special.Battery, "BATTERY", "Using your active halves the cooldown of the nearest teammate still recharging");
            P(Special.BuddySystem, "BUDDY SYSTEM", "Speed buffs are shared between you and the teammate nearest you");
            P(Special.Quartermaster, "QUARTERMASTER", "Teammates taking from a crate next to you get a spare");

            // 7. Temporary buffs
            A(Special.Frenzy, "FRENZY", "All your skills +2 for 8 s, then -2 for 8 s", 40f);
            A(Special.Overdrive, "OVERDRIVE", "No dash recharge for 5 s", 30f);
            P(Special.SecondWind, "SECOND WIND", "In the last 30 s of the level, RUNNING and DASHING +3");
            P(Special.Adrenaline, "ADRENALINE", "After a knock, slip or respawn, +40% run speed for 4 s");
            P(Special.Zen, "ZEN", "Stand still (not working) 1.5 s: your next chop or wash finishes at once");
            P(Special.CoffeeRun, "COFFEE RUN", "Every correct dish you hand in gives you +20% speed for 5 s");
            P(Special.GlassCannon, "GLASS CANNON", "All skills +1, but a knock, slip or fall makes them -1 for 15 s");
            P(Special.SlowStarter, "SLOW STARTER", "All skills -1 for the first 60 s, then +2");
            P(Special.UnderPressure, "UNDER PRESSURE", "+5% speed for each order waiting, up to +25%");
            P(Special.LuckyCharm, "LUCKY CHARM", "Each chop stroke (or half-second of washing) has a 15% chance to count double");
            P(Special.HotStreak, "HOT STREAK", "Orders in a row without a miss: +3% speed each (up to 15%); at 5, +1 to every skill");

            // 8. Level hazards and vehicles
            P(Special.PortalPro, "PORTAL PRO", "Out of a teleporter: run 40% faster and dash with no recharge for 2 s");
            P(Special.Cannoneer, "CANNONEER", "Cannons fire you the moment the button is pressed, without the fuse");
            P(Special.Driver, "DRIVER", "Carts and platforms you push or steer move 50% faster");
            P(Special.Weatherproof, "WEATHERPROOF", "Wind and moving floors don't push you around");
            P(Special.Lifeguard, "LIFEGUARD", "Teammates who fall near you are put back instead");

            // 9. Versus / party
            P(Special.Shove, "SHOVE", "Your dashes knock harder and make chefs drop what they hold", versus: true);
            P(Special.Pickpocket, "PICKPOCKET", "Dashing into a chef takes their item", versus: true);
            A(Special.GreaseTrail, "GREASE TRAIL", "For 3 s you leave a slippery trail; other chefs who step on it slip", 25f, versus: true);
            A(Special.SneezeBomb, "SNEEZE BOMB", "Every chef within 2 m of you (teammates too) sneezes and freezes for 1 s", 30f, versus: true);
            A(Special.Hex, "HEX", "The chef you face: their next 5 chops don't count", 30f, versus: true);
            A(Special.DecoyOrder, "DECOY ORDER", "Add a fake order to the rival team's list for 15 s (it scores nothing)", 45f, versus: true);

            // 10. Quirks
            Q(Special.HeavyBoots, "HEAVY BOOTS", "You can't dash at all", 2, 0);
            Q(Special.Clumsy, "CLUMSY", "Dashing drops whatever you hold", 1, 0);
            Q(Special.WeakArm, "WEAK ARM", "You can't throw", 1, 0);
            Q(Special.Butterfingers, "BUTTERFINGERS", "You can't catch thrown items", 1, 0);
            Q(Special.Pyrophobe, "PYROPHOBE", "Fire extinguishers won't spray for you", 1, 0);
            Q(Special.Germaphobe, "GERMAPHOBE", "You can't wash plates", 1, 0);
            Q(Special.Vegetarian, "VEGETARIAN", "You won't pick up raw meat or fish", 1, 0);
            Q(Special.Sleepy, "SLEEPY", "Every 30 s you doze off and can't move for 1 s", 2, 0);
            Q(Special.Fragile, "FRAGILE", "Respawning takes 10 s instead of 5 s", 1, 0);
            Q(Special.ShowOff, "SHOW-OFF", "After every dish you hand in, you pose for 1 s", 1, 0);
            Q(Special.TunnelVision, "TUNNEL VISION", "You can only chop or only wash: the first one you do", 2, 0);
            Q(Special.LoneWolf, "LONE WOLF", "Teammates' specials and auras don't affect you", 1, 0);

        }

        private static void A(Special id, string name, string description, float cooldown, int group = 0,
            bool teamWide = false, bool versus = false)
        {
            Add(new SpecialInfo { Id = id, Name = name, Description = description, Kind = SpecialKind.Active,
                Cooldown = cooldown, Group = group, TeamWide = teamWide, Versus = versus });
        }

        private static void P(Special id, string name, string description, int group = 0, bool teamWide = false,
            bool versus = false)
        {
            Add(new SpecialInfo { Id = id, Name = name, Description = description, Kind = SpecialKind.Passive,
                Group = group, TeamWide = teamWide, Versus = versus });
        }

        private static void Q(Special id, string name, string description, int refund, int group)
        {
            Add(new SpecialInfo { Id = id, Name = name, Description = description, Kind = SpecialKind.Quirk,
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
                "Cho phép chọn kỹ năng đặc biệt (1 chủ động, 2 bị động, 1 tật). Khi chơi trực tuyến dùng cài đặt của chủ phòng.");
            _versus = config.Bind("Specials", "VersusSpecials", false,
                "Cho phép các kỹ năng đặc biệt phá đối thủ (Shove, Pickpocket, Grease Trail, Sneeze Bomb, Hex, Decoy Order). Dùng cài đặt của chủ phòng.");
        }

        internal static bool Enabled
        {
            get { return s_hostEnabled ?? (_enabled == null || _enabled.Value); }
        }

        internal static bool VersusAllowed
        {
            get { return s_hostVersus ?? (_versus != null && _versus.Value); }
        }

        /// <summary>This machine's switches as the host publishes them (a number of flag bits).</summary>
        internal static int OwnFlags()
        {
            return (_enabled == null || _enabled.Value ? 1 : 0) | (_versus != null && _versus.Value ? 2 : 0);
        }

        /// <summary>Uses the host's switches (null: this machine's own config).</summary>
        internal static void UseHostFlags(int? flags)
        {
            if (flags == null)
            {
                s_hostEnabled = null;
                s_hostVersus = null;
                return;
            }
            s_hostEnabled = (flags.Value & 1) != 0;
            s_hostVersus = (flags.Value & 2) != 0;
        }

        internal static SpecialInfo Info(Special id)
        {
            SpecialInfo info;
            return Infos.TryGetValue(id, out info) ? info : null;
        }

        internal static string NameOf(Special id)
        {
            SpecialInfo info = Info(id);
            return info != null ? info.Name : "NONE";
        }

        internal static SpecialKind KindFor(SpecialSlot slot)
        {
            switch (slot)
            {
                case SpecialSlot.Active: return SpecialKind.Active;
                case SpecialSlot.Quirk: return SpecialKind.Quirk;
                default: return SpecialKind.Passive;
            }
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
        /// other picks? (Right kind, offered under the current rules, no clash of groups, and not
        /// the same passive twice.)
        /// </summary>
        internal static bool Fits(int[] loadout, SpecialSlot slot, Special candidate)
        {
            if (candidate == Special.None) return true;
            SpecialInfo info = Info(candidate);
            if (info == null || info.Kind != KindFor(slot)) return false;
            if (info.Versus && !VersusAllowed) return false;
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

        private static bool Clash(Special a, Special b)
        {
            foreach (Special[] set in Clashes)
            {
                if (set[0] == a && Array.IndexOf(set, b) > 0) return true;
                if (set[0] == b && Array.IndexOf(set, a) > 0) return true;
            }
            return false;
        }

        /// <summary>The choices for a slot in the order Left/Right steps through them, "none" first.</summary>
        internal static List<Special> Choices(SpecialSlot slot)
        {
            var list = new List<Special> { Special.None };
            SpecialKind kind = KindFor(slot);
            foreach (SpecialInfo info in Ordered)
                if (info.Kind == kind && (!info.Versus || VersusAllowed)) list.Add(info.Id);
            return list;
        }

        /// <summary>
        /// A loadout under the rules in use: unknown, wrong-kind, versus-when-off or clashing picks
        /// are dropped (later slots give way to earlier ones), and nothing at all when specials are
        /// turned off. Every machine runs this on every loadout, so they all agree.
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

        /// <summary>"BLINK, MAGNET, CHARMER, HEAVY BOOTS" for the log.</summary>
        internal static string Summary(int[] loadout)
        {
            var names = new List<string>();
            for (int i = 0; loadout != null && i < loadout.Length; i++)
                if (loadout[i] != 0) names.Add(NameOf((Special)loadout[i]));
            return names.Count > 0 ? string.Join(", ", names.ToArray()) : "không có kỹ năng đặc biệt";
        }
    }
}
