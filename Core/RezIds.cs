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
