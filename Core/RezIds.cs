namespace AutoRezzer.Core
{
    /// <summary>
    /// The raw game ids this plugin needs.
    ///
    /// HARDCODED ON PURPOSE, AND VERIFIED AT LOAD. RotationSolverReborn generates these from the
    /// game's own sheets with a source generator, which is the right call for a plugin that knows
    /// about every action in the game and the wrong one for a plugin that knows about six. The cost
    /// of a table this small is that a wrong number fails silently - the plugin would simply never
    /// cast - so Plugin.VerifyIds reads each of these back out of Lumina at startup and logs the
    /// name it actually found. If one of them ever moves, the log says so on the first login rather
    /// than leaving somebody wondering why nobody is getting up.
    /// </summary>
    internal static class RezIds
    {
        // ── Jobs ──
        public const uint Conjurer = 6;
        public const uint WhiteMage = 24;
        public const uint Arcanist = 26;
        public const uint Summoner = 27;
        public const uint Scholar = 28;
        public const uint Astrologian = 33;
        public const uint RedMage = 35;
        public const uint Sage = 40;

        // ── Raise actions ──
        public const uint Raise = 125;          // WHM / CNJ
        public const uint Resurrection = 173;   // SCH / SMN / ACN
        public const uint Ascend = 3603;        // AST
        public const uint Verraise = 7523;      // RDM
        public const uint Egeiro = 24287;       // SGE

        /// <summary>
        /// Blizzard, and it is never cast - it is asked about.
        ///
        /// Borrowed from RotationSolverReborn's IsEnemy: the sturdiest way to ask "is this thing
        /// hostile" is to ask the game whether a damage spell could legally be aimed at it. Reading
        /// BattleNpcKind instead gets it wrong in both directions - friendly event NPCs and struck
        /// dummies both enumerate as Combatant, and neither is something to hide a corpse from.
        /// </summary>
        public const uint Blizzard = 142;

        // ══════════════════════════════════════════════════════════
        //  OCCULT CRESCENT
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Occult Raise. Instant, five second recast, and free.
        ///
        /// EVERY RULE ELSEWHERE EXISTS BECAUSE OF THE COSTS THIS ONE DOES NOT HAVE. The Swiftcast
        /// dance is to avoid an eight second cast; the MP floor is because a raise is 2400 MP and
        /// spending it is what leaves a healer unable to heal; the Red Mage detour buys an instant
        /// cast with a gearset swap. Occult Raise is instant, costs nothing, and comes back in five
        /// seconds - so inside Occult Crescent, on Phantom White Mage, none of that machinery has
        /// anything to decide and all of it is skipped.
        ///
        /// It is also job-agnostic: a Warrior with the phantom job equipped can raise with it. That
        /// is why the "this job cannot raise" gate has to know about this action rather than being
        /// asked first.
        /// </summary>
        public const uint OccultRaise = 49070;

        /// <summary>
        /// Revive, Phantom Chemist's raise. The same deal as Occult Raise in every respect that
        /// matters: instant, free, five second recast, thirty yalms.
        ///
        /// Two jobs with the same capability is why the raiser is chosen rather than assumed - see
        /// PhantomRaiseActionFor. Occult Potion and Occult Ether beside it on the same bar cost an
        /// ITEM rather than MP, which is why "free" here is checked per action and not per job.
        /// </summary>
        public const uint Revive = 41634;

        /// <summary>Phantom White Mage's row in the MKDSupportJob sheet. The support-job level array
        /// on OccultCrescentState is indexed by this row directly.</summary>
        public const byte PhantomWhiteMage = 17;

        /// <summary>Phantom Chemist's row in the same sheet.</summary>
        public const byte PhantomChemist = 10;

        /// <summary>The phantom level Occult Raise unlocks at. Phantom White Mage caps at 5.</summary>
        public const int OccultRaiseLevel = 4;

        /// <summary>The phantom level Revive unlocks at. Phantom Chemist caps at 4.</summary>
        public const int ReviveLevel = 3;

        /// <summary>The phantom raise a support job grants, or 0 for the twenty-two that grant none.</summary>
        public static uint PhantomRaiseActionFor(int jobRow) => jobRow switch
        {
            PhantomWhiteMage => OccultRaise,
            PhantomChemist => Revive,
            _ => 0u,
        };

        /// <summary>The phantom level that raise appears at, or 0.</summary>
        public static int PhantomRaiseLevelFor(int jobRow) => jobRow switch
        {
            PhantomWhiteMage => OccultRaiseLevel,
            PhantomChemist => ReviveLevel,
            _ => 0,
        };

        /// <summary>Every support job that can raise, in the order they are offered.</summary>
        public static readonly byte[] PhantomRaisers = { PhantomWhiteMage, PhantomChemist };

        // ── What a raise costs ──
        //
        // Read out of the game's own Action sheet rather than remembered: PrimaryCostValue is in
        // units of 100 MP, so Raise's "24" is 2400 and Vercure's "5" is 500.

        /// <summary>Every hardcastable raise in the game costs this. Verraise included.</summary>
        public const int RaiseMpCost = 2400;

        /// <summary>Vercure, which Red Mage pays on top of the raise when it needs Dualcast.</summary>
        public const int VercureMpCost = 500;

        // ── Swiftcast ──
        public const uint Swiftcast = 7561;
        public const uint SwiftcastStatus = 167;
        public const int SwiftcastLevel = 18;

        // ── Red Mage's other way in ──

        /// <summary>
        /// Vercure, cast on yourself purely to proc Dualcast.
        ///
        /// RDM is the one job that can raise without Swiftcast. Verraise is a ten second hardcast
        /// bare, which is not a raise so much as an intention - but Dualcast makes the NEXT spell
        /// instant, and Dualcast comes from having cast anything with a cast time. Vercure is the
        /// cheapest thing on the bar that qualifies, which is why every Red Mage does exactly this
        /// by hand: a throwaway Vercure, then an instant Verraise.
        /// </summary>
        public const uint Vercure = 7514;
        public const int VercureLevel = 54;

        /// <summary>The proc Vercure exists to buy. While it is up, Verraise is instant.</summary>
        public const uint DualcastStatus = 1249;

        // ── Statuses on the body ──
        /// <summary>Set the moment a raise is cast on someone and cleared when they accept or it
        /// lapses. The single most important check here: it is what stops two rezzers, or a rezzer
        /// and a human healer, both burning a cast on the same corpse.</summary>
        public const uint RaisePending = 148;

        public const uint Weakness = 43;
        public const uint BrinkOfDeath = 44;
        public const uint Transcendent = 418;

        /// <summary>The level each job can first raise at. RDM is the odd one out at 64; everyone
        /// else who can do it at all can do it at 12.</summary>
        public const int RaiseLevel = 12;
        public const int VerraiseLevel = 64;

        /// <summary>Whether an action is one of the free, instant, job-agnostic phantom raises.</summary>
        public static bool IsOccult(uint action) => action is OccultRaise or Revive;

        /// <summary>A job's name, for the gearset picker.</summary>
        public static string JobName(uint job) => job switch
        {
            Conjurer => "Conjurer",
            WhiteMage => "White Mage",
            Arcanist => "Arcanist",
            Summoner => "Summoner",
            Scholar => "Scholar",
            Astrologian => "Astrologian",
            Sage => "Sage",
            RedMage => "Red Mage",
            _ => $"Job {job}",
        };

        /// <summary>The raise action for a job, or 0 when that job cannot raise at all.</summary>
        public static uint RaiseActionFor(uint job) => job switch
        {
            Conjurer or WhiteMage => Raise,
            Arcanist or Summoner or Scholar => Resurrection,
            Astrologian => Ascend,
            Sage => Egeiro,
            RedMage => Verraise,
            _ => 0u,
        };

        /// <summary>The level that job needs before its raise exists.</summary>
        public static int RaiseLevelFor(uint job) => job == RedMage ? VerraiseLevel : RaiseLevel;
    }
}
