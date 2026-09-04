using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using AutoRezzer.Core;
using Dalamud.Plugin.Services;

namespace AutoRezzer
{
    /// <summary>
    /// Which phantom job to raise with when the character has levelled both far enough.
    /// </summary>
    public enum PhantomRaiser
    {
        /// <summary>Whichever is already equipped; White Mage when neither is, or when swapping.</summary>
        Automatic = 0,

        /// <summary>Phantom White Mage, Occult Raise, from phantom level 4.</summary>
        WhiteMage = 1,

        /// <summary>Phantom Chemist, Revive, from phantom level 3.</summary>
        Chemist = 2,
    }

    /// <summary>
    /// Everything the plugin remembers between sessions.
    ///
    /// ── WHAT IS NOT IN HERE ANY MORE ──────────────────────────────────────
    /// Six settings were deleted in v6 rather than moved, because every one of them had exactly one
    /// right answer and offering the wrong one was the only thing it achieved:
    ///
    ///   RaiseStrangers        the plugin is FOR strangers; a FATE train is strangers
    ///   UseSwiftcast          mandatory whenever the job has it, and OFF paired with
    ///                         SwiftcastOnly was a deadlock rather than a configuration
    ///   EnemyNearBodyYalms    shipped at 0, stayed at 0, and any value big enough to
    ///                         matter raised nobody
    ///   PauseRotationSolver   there is no version of this plugin that should fight a
    ///                         rotation for a GCD it is taking anyway
    ///   RsrResume             the mode is READ off the live plugin now, so a guess
    ///                         chosen in advance has nothing left to do
    ///   StandDownInSanctuary  replaced by RezInSanctuaries, the same question asked the
    ///                         right way round, and a switch rather than a hardcoded rule
    ///
    /// FieldOperationsOnly survives as a legacy field, unused by anything except Migrate, because
    /// deleting it outright would silently widen the plugin's reach for everybody who had it on.
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        /// <summary>
        /// Bumped when a stored config needs touching up on load rather than merely being read.
        ///
        /// 1 -> 2: the two safety numbers moved to 0 by default.
        /// 2 -> 3: nothing needed fixing up.
        /// 3 -> 4: the MP floor gained a hard minimum of one raise.
        /// 4 -> 5: the two scope switches arrived.
        /// 5 -> 6: scope became four content switches, and six settings were deleted. The old
        /// "field operations only" flag decides what the four are set to, so nobody's plugin
        /// quietly starts working somewhere they had switched it off.
        /// 6 -> 7: the Crescent stopped being its own switch and folded into field operations,
        /// which is what it is. Either of the two being on keeps it on.
        /// </summary>
        public const int CurrentVersion = 7;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>
        /// The switch, and it governs everything below it.
        ///
        /// Off on a fresh install, and that is not a formality: a plugin that starts casting on its
        /// own the moment it is enabled is one that surprises somebody in the middle of a fight they
        /// were concentrating on.
        /// </summary>
        public bool Enabled { get; set; } = false;

        // ══════════════════════════════════════════════════════════
        //  WHERE IT WORKS
        // ══════════════════════════════════════════════════════════
        //
        // Four independent switches over the game's own classification of the zone, rather than one
        // "field operations only" rule. The rule was the right instinct expressed as a wall: it
        // answered "should this run in a savage fight" by also answering "should this run in a FATE",
        // and those are not the same question.
        //
        // Sanctuaries sit alongside them rather than inside them, because a rested area cuts across
        // all four: a city is open world, and the Crescent's base camp is the Crescent.

        /// <summary>Overworld zones, FATEs, hunt trains, the Diadem, the Firmament. On by default:
        /// this is the content the plugin was built for.</summary>
        public bool RunInOpenWorld { get; set; } = true;

        /// <summary>
        /// Dungeons, trials, raids, deep dungeons, variant and criterion.
        ///
        /// OFF by default, and it is the single most useful default in the file. Instanced content
        /// has a healer whose job this is, job changes are refused in combat, and a Gunbreaker in a
        /// savage fight can do nothing about the body on the floor except watch. Off is the answer
        /// to every one of those at once.
        /// </summary>
        public bool RunInInstances { get; set; } = false;

        /// <summary>
        /// Eureka, Bozja, Delubrum Reginae and the Occult Crescent. On by default.
        ///
        /// The Crescent used to have a switch of its own, which was one switch too many: it IS a
        /// field operation, and nobody wanting the plugin quiet in Bozja wants it loud in the
        /// Crescent. The Crescent screen still exists, but for phantom jobs rather than for whether
        /// the plugin runs there.
        /// </summary>
        public bool RunInFieldOperations { get; set; } = true;

        /// <summary>DEAD SETTING, read once by Migrate and folded into the line above.</summary>
        public bool RunInOccultCrescent { get; set; } = true;

        /// <summary>
        /// Rez inside sanctuaries: cities, inns, the Bozjan staging area, the Crescent's base camp.
        ///
        /// OFF by default, and it used to be hardcoded off with a footnote claiming nobody can die
        /// in one. That claim was wrong, which is exactly why this is a switch: bodies do turn up in
        /// rested areas, whether from a duty ending, a zone boundary, or somebody being escorted in.
        /// The default stays off because it is the quiet answer, not because the other one is
        /// impossible.
        ///
        /// Sanctuary is a live flag rather than a kind of zone, so it goes true and false as you
        /// walk in and out of base camp without the territory changing.
        /// </summary>
        public bool RezInSanctuaries { get; set; } = false;

        /// <summary>
        /// Whether this character has ever set foot in the Occult Crescent.
        ///
        /// ── WHY A REMEMBERED FLAG AND NOT AN UNLOCK CHECK ─────────────────
        /// The phantom job levels only exist while the zone's own director is up, so outside it
        /// there is nothing to read: a character who has the content unlocked and one who has never
        /// heard of it look identical from anywhere else in the game. The alternative is hardcoding
        /// the unlock quest's id, which is a magic number that goes stale silently.
        ///
        /// So the window shows the Crescent settings once you have been there, and hides them until
        /// then. It self-corrects on the first zone-in and never says anything untrue.
        /// </summary>
        public bool SeenOccultCrescent { get; set; } = false;

        /// <summary>
        /// The phantom job levels last seen in the zone, by MKDSupportJob row.
        ///
        /// ── REMEMBERED BECAUSE THEY CANNOT BE READ FROM OUTSIDE ───────────
        /// SupportJobLevels lives on the Crescent's own content director, and the director only
        /// exists inside the Crescent. Standing in Limsa, the game will not tell this plugin what
        /// phantom jobs you have levelled, which meant the picker showed nothing anywhere else and
        /// looked broken.
        ///
        /// So they are written down on the way past and read back when the live ones are missing.
        /// A remembered level is never wrong for long: it is refreshed every tick in the zone, and
        /// the only way to raise one is to be in there raising it.
        /// </summary>
        public Dictionary<int, int> PhantomJobLevels { get; set; } = new();

        /// <summary>
        /// DEAD SETTING, KEPT FOR ONE JOB.
        ///
        /// Read once by Migrate to decide the four switches above, and never again. Removing it
        /// would mean everybody who had narrowed the plugin to field operations woke up with it
        /// running in the open world again.
        /// </summary>
        public bool FieldOperationsOnly { get; set; } = false;

        // ══════════════════════════════════════════════════════════
        //  REZZING
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// How long a body has to have been on the floor before it is raised, in seconds.
        ///
        /// Zero by default: this is built for Occult Crescent and FATE trains, where the thing that
        /// actually happens is that nobody else is coming. Turn it up in a party that has its own
        /// healer, where the polite thing is to let them get there first.
        /// </summary>
        public float RaiseDelaySeconds { get; set; } = 0f;

        /// <summary>
        /// Never hardcast a raise: wait for Swiftcast instead of standing still for eight seconds.
        ///
        /// The wait costs nothing now. The plugin holds with its hands off and only stops the
        /// rotation at the moment Swiftcast is genuinely available, so a sixty second cooldown is
        /// sixty seconds of fighting rather than sixty seconds of standing there.
        ///
        /// Red Mage is unaffected and always has been: it goes through Dualcast, so it is already
        /// incapable of hardcasting a raise here.
        /// </summary>
        public bool SwiftcastOnly { get; set; } = false;

        /// <summary>
        /// Narrows <see cref="SwiftcastOnly"/> to combat only.
        ///
        /// The hardcast is dangerous because something is hitting you. Out of combat, on a corpse
        /// run, eight seconds is merely eight seconds.
        /// </summary>
        public bool SwiftcastOnlyInCombat { get; set; } = true;

        /// <summary>
        /// Don't start a raise below this much MP.
        ///
        /// A raise is 2400 MP, a quarter of the bar, and spending it is how a healer ends up unable
        /// to heal the people still standing. Below the floor the plugin does not merely decline to
        /// cast, it declines to interfere: the rotation is left alone.
        ///
        /// Red Mage needing Dualcast pays Vercure's 500 on top, so its real floor is 2900. That is
        /// added automatically rather than being a second setting.
        ///
        /// THE FLOOR CANNOT GO BELOW ONE RAISE. Anything under 2400 is not a lower setting, it is a
        /// broken one: the game refuses the cast outright below the cost.
        /// </summary>
        public int MinimumMpForRaise { get; set; } = MinimumMpFloor;

        /// <summary>One raise. The lowest the setting above is allowed to go, and its default.</summary>
        public const int MinimumMpFloor = RezIds.RaiseMpCost;

        /// <summary>A full MP bar at level 100. There is nothing above this to ask for.</summary>
        public const int MinimumMpCeiling = 10_000;

        /// <summary>
        /// Print a line to your own echo channel when a raise goes out.
        ///
        /// ECHO, NOT CHAT. Echo is local to your client and cannot leave it, so there is no version
        /// of this that accidentally announces to a party that something is raising for you.
        /// </summary>
        public bool AnnounceInEcho { get; set; } = false;

        // ══════════════════════════════════════════════════════════
        //  GETTING RAISED
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Accept a raise cast on you, automatically, when you are the one on the floor.
        ///
        /// Works on every job in the game, because anybody can be raised. It is the one thing here
        /// that acts while you are dead, which is exactly when you are least able to react yourself.
        /// </summary>
        public bool AcceptRaise { get; set; } = true;

        /// <summary>
        /// How long to leave the prompt alone first, in seconds.
        ///
        /// Zero by default. The reason to raise it is the wipe: standing up the instant somebody
        /// casts on you puts you back in the mechanic that killed you, with Weakness and a sliver of
        /// health, and burns the raise for nothing. A few seconds lets the floor clear.
        ///
        /// Capped well under the Raise status's own duration, so a delay can never be long enough to
        /// miss the prompt entirely.
        /// </summary>
        public float AcceptRaiseDelaySeconds { get; set; } = 0f;

        /// <summary>The longest wait offered. The prompt does not last forever.</summary>
        public const float AcceptDelayCeiling = 15f;

        // ══════════════════════════════════════════════════════════
        //  SWAPPING TO A JOB THAT CAN RAISE
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The gearset to switch to in order to raise somebody, or -1 for "never switch".
        ///
        /// A GEARSET, NOT A JOB, and that is not a detail. The game has no "become a White Mage"
        /// verb, it has "equip this gearset", and the job is a consequence. Storing a job would mean
        /// guessing which of your three White Mage sets was meant every time.
        /// </summary>
        public int RezGearsetId { get; set; } = -1;

        /// <summary>Whether to go back to the job you were on once the raise has landed. On, and it
        /// is most of what makes the switching bearable.</summary>
        public bool SwitchBackAfter { get; set; } = true;

        /// <summary>
        /// DEAD SETTING, KEPT SO OLD CONFIGS STILL LOAD.
        ///
        /// The Red Mage detour is always on now. It fires out of combat, only when Swiftcast is
        /// down, only when there is a body, and only when Red Mage is past Verraise with a gearset
        /// saved. Those four conditions ARE the decision; a switch on top of them only ever offered
        /// a way to decline the better outcome.
        /// </summary>
        public bool OutOfCombatSwitchToRdm { get; set; } = false;

        /// <summary>
        /// DEAD SETTING, KEPT SO OLD CONFIGS STILL LOAD.
        ///
        /// The Red Mage route takes the first Red Mage gearset it finds and always has by default.
        /// The picker that overrode it is gone: one set is not a choice, and three sets are three
        /// ways to cast the same Verraise.
        /// </summary>
        public int RdmGearsetId { get; set; } = -1;

        // ══════════════════════════════════════════════════════════
        //  OCCULT CRESCENT
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Use the phantom job's own raise instead of everything else, where it is available.
        ///
        /// On by default, and it overrides rather than supplements: Occult Raise is instant, free
        /// and off cooldown every five seconds. Swiftcast, the MP floor, the hardcast rules and the
        /// gearset switching are all answers to costs it does not have, so all of them are skipped.
        ///
        /// It also works on any job at all, because the phantom job grants it rather than your
        /// class. With this on, a Warrior in Occult Crescent raises.
        /// </summary>
        public bool UseOccultRaise { get; set; } = true;

        /// <summary>
        /// Which phantom raise to prefer when both are levelled far enough.
        ///
        /// They are identical in use, so there is no correct answer, only the one whose OTHER four
        /// actions you would rather be holding. Automatic keeps whichever is already equipped.
        /// </summary>
        public PhantomRaiser PreferredPhantomRaiser { get; set; } = PhantomRaiser.Automatic;

        /// <summary>
        /// Out of combat, change phantom job to a raising one, raise, and change back.
        ///
        /// OFF BY DEFAULT, DELIBERATELY. The phantom job is the thing being levelled, that is the
        /// entire point of being in the zone, and silently taking somebody off Phantom Cannoneer
        /// mid-grind is a decision they should make rather than discover.
        /// </summary>
        public bool SwapPhantomJobToRaise { get; set; } = false;

        /// <summary>
        /// Whether to go back to the phantom job you were levelling once the raise lands.
        ///
        /// NOT A SETTING ANY MORE, and kept only so the switcher has something to read. Borrowing a
        /// job and not giving it back is not a preference anybody holds; it is what the feature
        /// failing looks like. The window used to offer it, which meant offering people a way to be
        /// left on Phantom Chemist an hour later wondering why.
        /// </summary>
        public bool SwapPhantomJobBack { get; set; } = true;

        // ══════════════════════════════════════════════════════════
        //  LOCAL STATISTICS
        // ══════════════════════════════════════════════════════════
        //
        // Permanent. There is no reset, deliberately: the number is only interesting because it has
        // been counting the whole time, and a running total that anybody can zero is a scoreboard
        // rather than a record.

        /// <summary>Total number of raises accepted for the local player.</summary>
        public int AcceptedRaisesCount { get; set; } = 0;

        /// <summary>Total number of raises cast by the plugin.</summary>
        public int TotalRaisesCast { get; set; } = 0;

        /// <summary>
        /// Kept, but no longer shown. Dropping the property outright would delete the history of
        /// everybody who already has one the moment their config was next written.
        /// </summary>
        public Dictionary<uint, int> RaisesCastByJob { get; set; } = new();

        /// <summary>
        /// Fixes up a config written by an older version.
        /// </summary>
        public void Migrate(IPluginLog log)
        {
            if (Version >= CurrentVersion)
                return;

            if (Version < 2)
            {
                RaiseDelaySeconds = 0f;
                log.Information("[AutoRezzer] Config upgraded to v2: raise delay reset to 0.");
            }

            if (Version < 4 && MinimumMpForRaise < MinimumMpFloor)
            {
                log.Information($"[AutoRezzer] Config upgraded to v4: MP floor {MinimumMpForRaise} "
                                + $"raised to {MinimumMpFloor}, the cost of one raise.");
                MinimumMpForRaise = MinimumMpFloor;
            }

            if (Version < 6)
            {
                // ── THE OLD RULE DECIDES THE NEW SWITCHES ──
                //
                // Somebody who had narrowed the plugin to field operations gets exactly that back.
                // Everybody else gets the new defaults, which differ from the old behaviour in one
                // place only: instanced duties are now off, because that is the case the plugin
                // could never do anything useful in and the one people reported as a bug.
                if (FieldOperationsOnly)
                {
                    RunInOpenWorld = false;
                    RunInInstances = false;
                    RunInFieldOperations = true;
                    RunInOccultCrescent = true;
                    log.Information("[AutoRezzer] Config upgraded to v6: kept to field operations, "
                                    + "as \"field operations only\" was switched on.");
                }
                else
                {
                    RunInOpenWorld = true;
                    RunInInstances = false;
                    RunInFieldOperations = true;
                    RunInOccultCrescent = true;
                    log.Information("[AutoRezzer] Config upgraded to v6: running in the open world, "
                                    + "field operations and the Crescent. Instanced duties are off; "
                                    + "switch them back on under \"Where it works\" if you want them.");
                }
            }

            if (Version < 7)
            {
                // Either switch being on keeps the Crescent working. Folding two flags into one has
                // to be the forgiving direction: silently switching somebody's plugin off in the
                // zone it was written for would be a strange way to simplify a menu.
                RunInFieldOperations = RunInFieldOperations || RunInOccultCrescent;
            }

            Version = CurrentVersion;
        }

        public void RecordAcceptedRaise()
        {
            AcceptedRaisesCount++;
        }

        public void RecordCastRaise(uint jobId)
        {
            TotalRaisesCast++;
            if (!RaisesCastByJob.ContainsKey(jobId))
                RaisesCastByJob[jobId] = 0;
            RaisesCastByJob[jobId]++;
        }
    }
}
