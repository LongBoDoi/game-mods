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

        internal static string[] SlotNames
        {
            get
            {
                return Lang.Vi
                    ? new[] { "CHỦ ĐỘNG", "BỊ ĐỘNG", "BỊ ĐỘNG", "TẬT" }
                    : new[] { "ACTIVE", "PASSIVE", "PASSIVE", "QUIRK" };
            }
        }

        internal static string[] SlotHelp
        {
            get
            {
                if (Lang.Vi)
                    return new[]
                    {
                        "Kỹ năng đặc biệt chủ động: bấm PHÍM KỸ NĂNG (mặc định là BIỂU CẢM) để dùng",
                        "Kỹ năng đặc biệt bị động: luôn bật",
                        "Kỹ năng đặc biệt bị động thứ hai: luôn bật",
                        "Tật tùy chọn, đổi lại bạn có thêm điểm kỹ năng để cộng",
                    };
                return new[]
                {
                    "Your active special: press your SKILL KEY (EMOTE unless changed) to use it",
                    "A passive special: always on",
                    "A second passive special: always on",
                    "Optional drawback that gives you skill points to spend",
                };
            }
        }

        static SpecialCatalog()
        {
            // 1. Movement
            A(Special.Vault, "VAULT", "Hop over whatever is in front of you, walls too, landing up to 4 m away", 6f);
            A(Special.Blink, "BLINK", "Teleport up to 6 m ahead, straight through counters and walls", 10f);
            P(Special.Sprinter, "SPRINTER", "Hold DASH to sprint +50% (4 s of stamina) instead of dashing", group: DashStyle);
            P(Special.DoubleDash, "DOUBLE DASH", "Two dash charges: chain two dashes back to back", group: DashStyle);
            P(Special.ChargeDash, "CHARGE DASH", "Hold DASH to charge, release for up to twice the distance", group: DashStyle);
            P(Special.Momentum, "MOMENTUM", "Keep running to build up to +30% speed; stopping loses it");
            P(Special.Slipstream, "SLIPSTREAM", "Teammates running close behind you get +30% speed");
            P(Special.SureFooted, "SURE-FOOTED", "No slipping on ice or puddles, no drag from wind or moving walkways");
            P(Special.Immovable, "IMMOVABLE", "Dashes, thrown items and fire can't knock you back");
            P(Special.Ghost, "GHOST", "Walk straight through other chefs");
            A(Special.Recall, "RECALL", "Plant a flag; use again within 20 s to teleport back to it (cooldown starts on return)", 25f);
            A(Special.Swap, "SWAP", "Trade places with the teammate you face (or the nearest)", 20f);
            A(Special.Summon, "SUMMON", "Pull the teammate you face (or the nearest) to your side", 25f);
            P(Special.QuickRespawn, "QUICK RESPAWN", "Respawn after 1.5 s instead of 5 s");
            P(Special.SafetyNet, "SAFETY NET", "Once every 30 s, falling off puts you back where you fell");
            P(Special.ConveyorRider, "CONVEYOR RIDER", "Moving floors carry you twice as fast and never slow you down");
            P(Special.BigChef, "BIG CHEF", "Bigger: can't be knocked, knock others further, -10% speed", group: Size);
            P(Special.TinyChef, "TINY CHEF", "Smaller: +15% speed, but knocks send you further", group: Size);
            P(Special.Hover, "HOVER", "Dash across a gap up to one tile wide (10 s recharge)");
            A(Special.Grapple, "GRAPPLE", "Pull yourself to the counter you face, up to 6 m away", 12f);

            // 2. Carrying and throwing
            A(Special.PackMule, "PACK MULE", "Carry a second item on your back; SKILL KEY swaps hand and back", 0.5f);
            A(Special.Pocket, "POCKET", "Pocket up to 3 ingredients, hidden; SKILL KEY stores or takes the last out", 0.5f);
            P(Special.Waiter, "WAITER", "Put a dish onto another dish to carry both; hand one in and the other goes in too");
            P(Special.LongArms, "LONG ARMS", "Pick up, put down, chop and wash one tile further away");
            P(Special.Magnet, "MAGNET", "Loose items on the floor within 4 m slide towards you");
            A(Special.Telekinesis, "TELEKINESIS", "Pull the item you face, up to 6 m away, into your hands", 8f);
            P(Special.BulkBuyer, "BULK BUYER", "Taking from a crate also puts a spare on a counter (12 s)");
            P(Special.CatchersMitt, "CATCHER'S MITT", "Catch from any direction and further away");
            P(Special.StickyFingers, "STICKY FINGERS", "Keep what you hold when shoved, and even when you fall off the map");
            P(Special.StrongArm, "STRONG ARM", "Throw anything (pots, pans, dishes); teammates in its path catch it");
            P(Special.HomingThrow, "HOMING THROW", "Throws fly to the teammate you aim at (14 m); they catch from any side", group: ThrowPath);
            P(Special.Lob, "LOB", "Throws arc high over counters and chefs (same distance)", group: ThrowPath);
            P(Special.AssemblyLine, "ASSEMBLY LINE", "Take from a crate, then face a teammate within 1.5 s: it flies to them");
            P(Special.Juggler, "JUGGLER", "Re-throw within 1 s of a catch for a 50% longer throw");
            P(Special.Rebound, "REBOUND", "Uncaught throws landing within 3 m of you hop onto a free counter");
            P(Special.HandOff, "HAND-OFF", "PICK UP facing a teammate takes their item or gives yours");

            // 3. Prep stations
            P(Special.Multitask, "MULTITASK", "A board or sink you leave keeps working at 40% speed");
            P(Special.EchoKnife, "ECHO KNIFE", "Each chop also chops the last other board you used");
            A(Special.PowerChop, "POWER CHOP", "Finish the item on the board you face at once", 15f);
            P(Special.LuckyKnife, "LUCKY KNIFE", "Each chop has a 10% chance to finish the item at once");
            P(Special.PrepCook, "PREP COOK", "Every 15 s, an ingredient you take from a crate comes chopped");
            P(Special.DashChop, "DASH-CHOP", "Dashing past a loaded board chops it 3 times");
            A(Special.WashBurst, "WASH BURST", "Wash every plate in the sink you face at once", 40f);
            P(Special.Dishwasher, "DISHWASHER", "Every second dish you hand in comes back clean, on a sink's drying rack");
            P(Special.Butler, "BUTLER", "Your dirty plates come back straight into a sink");
            A(Special.PlateConjurer, "PLATE CONJURER", "Conjure a clean plate in your empty hands", 30f);
            A(Special.SparePan, "SPARE PAN", "Conjure a copy of the empty pan or pot you face (once per level)", 9999f);

            // 4. Cooking and fire
            P(Special.HotHands, "HOT HANDS", "A pot or pan you carry keeps cooking at full speed and stops when done");
            P(Special.ColdHands, "COLD HANDS", "Pots and pans you carry or fill wait 8 s at done before they burn");
            A(Special.Rescue, "RESCUE", "Un-burn the pot or pan you hold or face and put out its cooker's fire", 45f);
            A(Special.Overclock, "OVERCLOCK", "The cooker or mixer you face works three times as fast for 10 s", 30f);
            P(Special.SizzleMaster, "SIZZLE MASTER", "Pots and pans you carry or fill cook (and burn) twice as fast");
            P(Special.SteadyMixer, "STEADY MIXER", "Bowls you carry or fill mix 30% faster and never over-mix");
            P(Special.Fireproof, "FIREPROOF", "Walk through flames, and fire right next to you dies out in 2 s");
            P(Special.FireMarshal, "FIRE MARSHAL", "A cooker under a pot or pan you carried or filled never catches fire");
            A(Special.FireBlast, "FIRE BLAST", "Put out every fire within 4 m at once", 30f);
            P(Special.BuiltInExtinguisher, "HOSE HANDS", "Hold USE with empty hands facing a fire to spray it like an extinguisher");
            P(Special.SlowSpread, "SLOW SPREAD", "Fire within 5 m of you spreads 3 times slower");

            // 5. Orders and score
            P(Special.Charmer, "CHARMER", "Dishes you hand in give +50% tip");
            P(Special.MaitreD, "MAITRE D'", "Every order lasts 25% longer (whole team)", teamWide: true);
            P(Special.Clutch, "CLUTCH", "A dish handed in during its order's last 25% scores double");
            P(Special.ComboKeeper, "COMBO KEEPER", "A missed order or wrong dish keeps the tip streak (every 40 s)", teamWide: true);
            P(Special.SecondChance, "SECOND CHANCE", "Each order's first expiry costs no points (whole team)", teamWide: true);
            P(Special.Gambler, "GAMBLER", "Your tips are multiplied by 0, 1, 2 or 3 at random");
            P(Special.FreshDelivery, "FRESH DELIVERY", "A dish handed in within 20 s of cooking gets +50% tip");
            A(Special.Reroll, "REROLL", "Swap the newest order for a different dish", 30f);
            A(Special.TimeOut, "TIME OUT", "Order timers (and new orders) stop for 10 s (whole team)", 50f, teamWide: true);
            P(Special.CloseEnough, "CLOSE ENOUGH", "Every 45 s, a dish nobody ordered counts as the oldest order", teamWide: true);
            P(Special.FortuneTeller, "FORTUNE TELLER", "The next order shows early as a see-through card on the order bar (not on phased levels)");
            P(Special.BountyHunter, "BOUNTY HUNTER", "One order at a time is framed in gold on the order bar: hand it in for double points", teamWide: true);
            P(Special.GoldenHour, "GOLDEN HOUR", "In the last 45 s of the level, your tips are doubled");

            // 6. Team support
            A(Special.RallyCry, "RALLY CRY", "You and every teammate run 30% faster for 8 s", 30f);
            P(Special.Inspiring, "INSPIRING", "Teammates within 3.5 m of you chop and wash about 25% faster", teamWide: true);
            P(Special.Medic, "MEDIC", "Teammates who fall respawn next to you after 1 s");
            P(Special.Bodyguard, "BODYGUARD", "Teammates within 4 m of you can't be knocked back or slip");
            P(Special.Battery, "BATTERY", "Your active cuts half a full cooldown off a teammate's (kept if none)");
            P(Special.BuddySystem, "BUDDY SYSTEM", "Near your closest teammate (4 m) both run 15% faster; speed buffs shared");
            P(Special.Quartermaster, "QUARTERMASTER", "Teammates taking from a crate within 3.5 m of you get a spare (10 s)");

            // 7. Temporary buffs
            A(Special.Frenzy, "FRENZY", "All your skills +2 for 10 s, then -1 for 6 s", 40f);
            A(Special.Overdrive, "OVERDRIVE", "No dash recharge and +20% speed for 6 s", 30f);
            P(Special.SecondWind, "SECOND WIND", "In the last 30 s, all skills +1 (RUNNING and DASHING +3)");
            P(Special.Adrenaline, "ADRENALINE", "After a knock, slip or respawn: +50% run speed, free dashes for 5 s");
            P(Special.Zen, "ZEN", "Stand still (not working) 1.5 s: your next chop or wash finishes at once");
            P(Special.CoffeeRun, "COFFEE RUN", "Every correct dish you hand in gives you +25% speed for 6 s");
            P(Special.GlassCannon, "GLASS CANNON", "All skills +1, but a knock, slip or fall makes them -1 for 15 s");
            P(Special.SlowStarter, "SLOW STARTER", "All skills -1 for the first 60 s of play, then +2");
            P(Special.UnderPressure, "UNDER PRESSURE", "+5% speed for each order waiting, up to +25%");
            P(Special.LuckyCharm, "LUCKY CHARM", "Each chop stroke (or half-second washing) 25% chance to count double");
            P(Special.HotStreak, "HOT STREAK", "Orders in a row without a miss: +3% speed each (up to 15%); at 5, +1 to every skill");

            // 8. Level hazards and vehicles
            P(Special.PortalPro, "PORTAL PRO", "Out of a teleporter: run 40% faster and dash with no recharge for 2 s");
            P(Special.Cannoneer, "CANNONEER", "Cannons fire you the moment the button is pressed, without the fuse");
            P(Special.Driver, "DRIVER", "Platforms and carts you steer move 50% faster");
            P(Special.Weatherproof, "WEATHERPROOF", "Wind and moving floors only push you the way you face");
            P(Special.Lifeguard, "LIFEGUARD", "Teammates who fall near you are put back instead");

            // 9. Versus / party
            P(Special.Shove, "SHOVE", "Your dashes knock harder and make chefs drop what they hold", versus: true);
            P(Special.Pickpocket, "PICKPOCKET", "Dashing into a chef takes what they hold (not from Sticky Fingers)", versus: true);
            A(Special.GreaseTrail, "GREASE TRAIL", "For 3 s you leave a slippery trail; other chefs who step on it slip", 25f, versus: true);
            A(Special.SneezeBomb, "SNEEZE BOMB", "Every chef within 3 m of you (teammates too) sneezes and freezes for 2 s", 30f, versus: true);
            A(Special.Hex, "HEX", "The chef you face: their next 5 chops (within 30 s) don't count", 30f, versus: true);
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

            Vietnamese();
        }

        /// <summary>Every special's name and description in Vietnamese.</summary>
        private static void Vietnamese()
        {
            // 1. Movement
            Vi(Special.Vault, "NHẢY VỌT", "Nhảy qua thứ trước mặt, kể cả tường, đáp xa tới 4 m");
            Vi(Special.Blink, "THOẮT ẢNH", "Dịch chuyển tới 6 m phía trước, xuyên qua quầy và tường");
            Vi(Special.Sprinter, "NƯỚC RÚT", "Giữ LƯỚT để chạy nước rút +50% (4 s thể lực) thay vì lướt");
            Vi(Special.DoubleDash, "LƯỚT ĐÔI", "Hai lượt lướt: lướt hai lần liền nhau");
            Vi(Special.ChargeDash, "LƯỚT TÍCH LỰC", "Giữ LƯỚT để tích lực, thả ra để lướt xa tới gấp đôi");
            Vi(Special.Momentum, "LẤY ĐÀ", "Chạy liên tục để tăng dần tới +30% tốc độ; dừng lại là mất");
            Vi(Special.Slipstream, "BÁM ĐUÔI", "Đồng đội chạy sát sau lưng bạn được +30% tốc độ");
            Vi(Special.SureFooted, "CHÂN VỮNG", "Không trượt trên băng hay vũng nước, không bị gió hay băng chuyền kéo");
            Vi(Special.Immovable, "BẤT ĐỘNG", "Cú lướt, đồ bị ném và lửa không đẩy lùi được bạn");
            Vi(Special.Ghost, "BÓNG MA", "Đi xuyên qua các đầu bếp khác");
            Vi(Special.Recall, "QUAY VỀ", "Cắm cờ; dùng lại trong 20 s để dịch chuyển về đó (hồi chiêu tính từ lúc về)");
            Vi(Special.Swap, "ĐỔI CHỖ", "Đổi chỗ với đồng đội bạn đang nhìn (hoặc người gần nhất)");
            Vi(Special.Summon, "TRIỆU HỒI", "Kéo đồng đội bạn đang nhìn (hoặc người gần nhất) về cạnh bạn");
            Vi(Special.QuickRespawn, "HỒI SINH NHANH", "Hồi sinh sau 1.5 s thay vì 5 s");
            Vi(Special.SafetyNet, "LƯỚI AN TOÀN", "Cứ 30 s một lần, rơi khỏi màn sẽ đưa bạn về chỗ vừa rơi");
            Vi(Special.ConveyorRider, "CƯỠI BĂNG CHUYỀN", "Sàn di chuyển chở bạn nhanh gấp đôi và không bao giờ làm bạn chậm");
            Vi(Special.BigChef, "BẾP KHỔNG LỒ", "To hơn: không bị đẩy, đẩy người khác xa hơn, -10% tốc độ");
            Vi(Special.TinyChef, "BẾP TÍ HON", "Nhỏ hơn: +15% tốc độ, nhưng bị đẩy văng xa hơn");
            Vi(Special.Hover, "LƠ LỬNG", "Lướt qua khoảng trống rộng tới một ô (hồi 10 s)");
            Vi(Special.Grapple, "MÓC KÉO", "Kéo mình tới quầy bạn đang nhìn, xa tới 6 m");

            // 2. Carrying and throwing
            Vi(Special.PackMule, "THỒ HÀNG", "Đeo thêm một món trên lưng; PHÍM KỸ NĂNG đổi đồ ở tay và lưng");
            Vi(Special.Pocket, "TÚI ÁO", "Giấu tới 3 nguyên liệu vào túi; PHÍM KỸ NĂNG cất vào hoặc lấy món cuối ra");
            Vi(Special.Waiter, "BỒI BÀN", "Đặt món lên món khác để bưng cả hai; giao một món thì món kia cũng được giao");
            Vi(Special.LongArms, "TAY DÀI", "Nhặt, đặt, thái và rửa xa thêm một ô");
            Vi(Special.Magnet, "NAM CHÂM", "Đồ rơi trên sàn trong vòng 4 m trượt về phía bạn");
            Vi(Special.Telekinesis, "NIỆM LỰC", "Kéo món đồ bạn đang nhìn, xa tới 6 m, vào tay bạn");
            Vi(Special.BulkBuyer, "MUA SỈ", "Lấy đồ từ thùng thì có thêm một phần dự phòng trên quầy (12 s)");
            Vi(Special.CatchersMitt, "GĂNG BẮT BÓNG", "Bắt được đồ từ mọi hướng và ở xa hơn");
            Vi(Special.StickyFingers, "TAY DÍNH", "Giữ được đồ trên tay khi bị xô, kể cả khi rơi khỏi màn");
            Vi(Special.StrongArm, "TAY KHỎE", "Ném được mọi thứ (nồi, chảo, món ăn); đồng đội trên đường bay sẽ bắt");
            Vi(Special.HomingThrow, "NÉM DẪN ĐƯỜNG", "Đồ ném bay tới đồng đội bạn nhắm (14 m); họ bắt được từ mọi phía");
            Vi(Special.Lob, "NÉM BỔNG", "Đồ ném bay vòng cao qua quầy và đầu bếp (tầm xa như cũ)");
            Vi(Special.AssemblyLine, "DÂY CHUYỀN", "Lấy đồ từ thùng rồi nhìn đồng đội trong 1.5 s: đồ sẽ bay tới họ");
            Vi(Special.Juggler, "TUNG HỨNG", "Ném lại trong 1 s sau khi bắt để ném xa hơn 50%");
            Vi(Special.Rebound, "NẢY LẠI", "Đồ ném không ai bắt rơi trong 3 m quanh bạn sẽ nảy lên quầy trống");
            Vi(Special.HandOff, "TRAO TAY", "NHẶT khi nhìn đồng đội để lấy đồ của họ hoặc đưa đồ của bạn");

            // 3. Prep stations
            Vi(Special.Multitask, "ĐA NHIỆM", "Thớt hay bồn rửa bạn rời đi vẫn chạy tiếp với 40% tốc độ");
            Vi(Special.EchoKnife, "DAO VỌNG", "Mỗi nhát thái cũng thái luôn thớt khác bạn dùng gần nhất");
            Vi(Special.PowerChop, "THÁI MẠNH", "Thái xong ngay món trên thớt bạn đang nhìn");
            Vi(Special.LuckyKnife, "DAO MAY MẮN", "Mỗi nhát thái có 10% cơ hội thái xong món ngay");
            Vi(Special.PrepCook, "PHỤ BẾP", "Cứ 15 s, nguyên liệu bạn lấy từ thùng đã được thái sẵn");
            Vi(Special.DashChop, "LƯỚT-THÁI", "Lướt ngang thớt có đồ sẽ thái nó 3 nhát");
            Vi(Special.WashBurst, "RỬA CẤP TỐC", "Rửa sạch ngay mọi đĩa trong bồn rửa bạn đang nhìn");
            Vi(Special.Dishwasher, "MÁY RỬA BÁT", "Cứ món thứ hai bạn giao, đĩa về sạch trên giá phơi của bồn rửa");
            Vi(Special.Butler, "QUẢN GIA", "Đĩa bẩn của bạn quay thẳng về bồn rửa");
            Vi(Special.PlateConjurer, "HÓA PHÉP ĐĨA", "Hóa ra một đĩa sạch trên tay không của bạn");
            Vi(Special.SparePan, "CHẢO DỰ PHÒNG", "Hóa ra bản sao của nồi hay chảo rỗng bạn đang nhìn (một lần mỗi màn)");

            // 4. Cooking and fire
            Vi(Special.HotHands, "TAY NÓNG", "Nồi hay chảo bạn cầm vẫn nấu hết tốc độ và dừng khi chín");
            Vi(Special.ColdHands, "TAY LẠNH", "Nồi, chảo bạn cầm hoặc cho đồ vào chờ 8 s khi chín rồi mới cháy");
            Vi(Special.Rescue, "CỨU NGUY", "Hết cháy nồi hay chảo bạn cầm hoặc đang nhìn, dập lửa bếp của nó");
            Vi(Special.Overclock, "ÉP XUNG", "Bếp hoặc máy trộn bạn đang nhìn chạy nhanh gấp ba trong 10 s");
            Vi(Special.SizzleMaster, "VUA XÈO XÈO", "Nồi, chảo bạn cầm hoặc cho đồ vào nấu (và cháy) nhanh gấp đôi");
            Vi(Special.SteadyMixer, "TRỘN ĐỀU TAY", "Bát bạn cầm hoặc cho đồ vào trộn nhanh hơn 30%, không bao giờ quá tay");
            Vi(Special.Fireproof, "CHỐNG CHÁY", "Đi xuyên qua lửa, và lửa sát cạnh bạn tắt sau 2 s");
            Vi(Special.FireMarshal, "LÍNH CỨU HỎA", "Bếp dưới nồi hay chảo bạn từng cầm hoặc cho đồ vào không bao giờ bắt lửa");
            Vi(Special.FireBlast, "DẬP LỬA", "Dập ngay mọi đám lửa trong vòng 4 m");
            Vi(Special.BuiltInExtinguisher, "TAY VÒI NƯỚC", "Giữ DÙNG khi tay không và nhìn đám lửa để phun như bình chữa cháy");
            Vi(Special.SlowSpread, "LỬA CHẬM LAN", "Lửa trong vòng 5 m quanh bạn lan chậm hơn 3 lần");

            // 5. Orders and score
            Vi(Special.Charmer, "DUYÊN DÁNG", "Món bạn giao được +50% tiền boa");
            Vi(Special.MaitreD, "QUẢN LÝ SẢNH", "Mọi đơn kéo dài thêm 25% (cả đội)");
            Vi(Special.Clutch, "PHÚT CHÓT", "Món giao trong 25% thời gian cuối của đơn được gấp đôi điểm");
            Vi(Special.ComboKeeper, "GIỮ CHUỖI", "Lỡ đơn hay giao sai món vẫn giữ chuỗi tiền boa (mỗi 40 s)");
            Vi(Special.SecondChance, "CƠ HỘI THỨ HAI", "Lần hết giờ đầu tiên của mỗi đơn không bị trừ điểm (cả đội)");
            Vi(Special.Gambler, "CON BẠC", "Tiền boa của bạn được nhân ngẫu nhiên 0, 1, 2 hoặc 3");
            Vi(Special.FreshDelivery, "GIAO NÓNG HỔI", "Món giao trong 20 s sau khi nấu xong được +50% tiền boa");
            Vi(Special.Reroll, "ĐỔI ĐƠN", "Đổi đơn mới nhất sang một món khác");
            Vi(Special.TimeOut, "TẠM DỪNG", "Đồng hồ các đơn (và đơn mới) dừng 10 s (cả đội)");
            Vi(Special.CloseEnough, "TẠM CHẤP NHẬN", "Cứ 45 s, một món không ai gọi được tính là đơn cũ nhất");
            Vi(Special.FortuneTeller, "THẦY BÓI", "Đơn tiếp theo hiện sớm thành thẻ mờ trên thanh đơn (trừ màn nhiều giai đoạn)");
            Vi(Special.BountyHunter, "SĂN TIỀN THƯỞNG", "Mỗi lúc một đơn được viền vàng trên thanh đơn: giao nó được gấp đôi điểm");
            Vi(Special.GoldenHour, "GIỜ VÀNG", "Trong 45 s cuối màn, tiền boa của bạn được nhân đôi");

            // 6. Team support
            Vi(Special.RallyCry, "HIỆU TRIỆU", "Bạn và mọi đồng đội chạy nhanh hơn 30% trong 8 s");
            Vi(Special.Inspiring, "TRUYỀN LỬA", "Đồng đội trong vòng 3.5 m quanh bạn thái và rửa nhanh hơn khoảng 25%");
            Vi(Special.Medic, "CỨU THƯƠNG", "Đồng đội bị rơi hồi sinh cạnh bạn sau 1 s");
            Vi(Special.Bodyguard, "VỆ SĨ", "Đồng đội trong vòng 4 m quanh bạn không bị đẩy lùi hay trượt ngã");
            Vi(Special.Battery, "CỤC PIN", "Kỹ năng chủ động của bạn cắt nửa hồi chiêu của một đồng đội (để dành nếu chưa có)");
            Vi(Special.BuddySystem, "CẠ CỨNG", "Ở gần đồng đội gần nhất (4 m) cả hai chạy nhanh hơn 15%; chia nhau hiệu ứng tăng tốc");
            Vi(Special.Quartermaster, "HẬU CẦN", "Đồng đội lấy đồ từ thùng trong vòng 3.5 m quanh bạn được thêm một phần (10 s)");

            // 7. Temporary buffs
            Vi(Special.Frenzy, "CUỒNG NHIỆT", "Mọi kỹ năng +2 trong 10 s, rồi -1 trong 6 s");
            Vi(Special.Overdrive, "BỨT TỐC", "Lướt không cần hồi và +20% tốc độ trong 6 s");
            Vi(Special.SecondWind, "HỒI SỨC", "Trong 30 s cuối, mọi kỹ năng +1 (CHẠY và LƯỚT +3)");
            Vi(Special.Adrenaline, "ADRENALINE", "Sau khi bị đẩy, trượt hay hồi sinh: +50% tốc độ chạy, lướt không cần hồi trong 5 s");
            Vi(Special.Zen, "THIỀN", "Đứng yên (không làm gì) 1.5 s: nhát thái hoặc lần rửa kế tiếp xong ngay");
            Vi(Special.CoffeeRun, "CÀ PHÊ SỮA ĐÁ", "Mỗi món đúng bạn giao cho bạn +25% tốc độ trong 6 s");
            Vi(Special.GlassCannon, "PHÁO THỦY TINH", "Mọi kỹ năng +1, nhưng bị đẩy, trượt hay rơi thì -1 trong 15 s");
            Vi(Special.SlowStarter, "KHỞI ĐỘNG CHẬM", "Mọi kỹ năng -1 trong 60 s đầu, sau đó +2");
            Vi(Special.UnderPressure, "ÁP LỰC", "+5% tốc độ cho mỗi đơn đang chờ, tối đa +25%");
            Vi(Special.LuckyCharm, "BÙA MAY MẮN", "Mỗi nhát thái (hoặc nửa giây rửa) có 25% cơ hội tính gấp đôi");
            Vi(Special.HotStreak, "ĐANG NÓNG TAY", "Đơn liên tiếp không lỡ: +3% tốc độ mỗi đơn (tối đa 15%); tới 5, mọi kỹ năng +1");

            // 8. Level hazards and vehicles
            Vi(Special.PortalPro, "CAO THỦ CỔNG", "Ra khỏi cổng dịch chuyển: chạy nhanh hơn 40% và lướt không cần hồi trong 2 s");
            Vi(Special.Cannoneer, "PHÁO THỦ", "Pháo bắn bạn đi ngay khi bấm nút, không phải chờ ngòi");
            Vi(Special.Driver, "TÀI XẾ", "Bệ và xe bạn lái chạy nhanh hơn 50%");
            Vi(Special.Weatherproof, "CHỊU GIÓ", "Gió và sàn di chuyển chỉ đẩy bạn theo hướng bạn nhìn");
            Vi(Special.Lifeguard, "CỨU HỘ", "Đồng đội rơi gần bạn được đưa trở lại ngay");

            // 9. Versus / party
            Vi(Special.Shove, "XÔ ĐẨY", "Cú lướt của bạn đẩy mạnh hơn và làm đầu bếp đánh rơi đồ đang cầm");
            Vi(Special.Pickpocket, "MÓC TÚI", "Lướt vào đầu bếp để lấy đồ họ đang cầm (trừ người có TAY DÍNH)");
            Vi(Special.GreaseTrail, "VỆT DẦU", "Trong 3 s bạn để lại vệt trơn; đầu bếp khác giẫm lên sẽ trượt");
            Vi(Special.SneezeBomb, "BOM HẮT XÌ", "Mọi đầu bếp trong vòng 3 m quanh bạn (cả đồng đội) hắt xì và đứng hình 2 s");
            Vi(Special.Hex, "NGUYỀN RỦA", "Đầu bếp bạn đang nhìn: 5 nhát thái tiếp theo của họ (trong 30 s) không tính");
            Vi(Special.DecoyOrder, "ĐƠN GIẢ", "Thêm một đơn giả vào danh sách của đội đối thủ trong 15 s (không ra điểm)");

            // 10. Quirks
            Vi(Special.HeavyBoots, "GIÀY NẶNG", "Bạn không lướt được");
            Vi(Special.Clumsy, "VỤNG VỀ", "Lướt là đánh rơi đồ đang cầm");
            Vi(Special.WeakArm, "TAY YẾU", "Bạn không ném được");
            Vi(Special.Butterfingers, "TAY TRƠN", "Bạn không bắt được đồ bị ném");
            Vi(Special.Pyrophobe, "SỢ LỬA", "Bình chữa cháy không phun khi bạn dùng");
            Vi(Special.Germaphobe, "SỢ BẨN", "Bạn không rửa được đĩa");
            Vi(Special.Vegetarian, "ĂN CHAY", "Bạn không nhặt thịt hay cá sống");
            Vi(Special.Sleepy, "BUỒN NGỦ", "Cứ 30 s bạn ngủ gật và không đi được 1 s");
            Vi(Special.Fragile, "MONG MANH", "Hồi sinh mất 10 s thay vì 5 s");
            Vi(Special.ShowOff, "THÍCH KHOE", "Sau mỗi món bạn giao, bạn tạo dáng 1 s");
            Vi(Special.TunnelVision, "MỘT VIỆC THÔI", "Bạn chỉ thái hoặc chỉ rửa: việc bạn làm trước");
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

        private static void P(Special id, string name, string description, int group = 0, bool teamWide = false,
            bool versus = false)
        {
            Add(new SpecialInfo { Id = id, NameEn = name, NameVi = name, DescriptionEn = description,
                DescriptionVi = description, Kind = SpecialKind.Passive,
                Group = group, TeamWide = teamWide, Versus = versus });
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
                if (loadout[i] != 0) names.Add(EnglishNameOf((Special)loadout[i]));
            return names.Count > 0 ? string.Join(", ", names.ToArray()) : "không có kỹ năng đặc biệt";
        }
    }
}
