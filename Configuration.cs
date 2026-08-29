using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using AutoRezzer.Core;
using Dalamud.Plugin.Services;

namespace AutoRezzer
{
    /// <summary>
    /// How Rotation Solver Reborn should be put back once a raise is done with.
    ///
    /// THIS EXISTS BECAUSE RSR CANNOT BE ASKED. Its IPC surface offers exactly one read - a bool
    /// saying whether autorotation is on at all - and nothing that reports WHICH mode it is in. So
    /// "restore what was there" has to be a decision made in advance rather than a value read back,
    /// and getting it wrong is not cosmetic: putting RSR into Auto when it was in Henched hands it
    /// back its own targeting, and a plugin that was driving it (BOCCHI, AutoDuty, Henchman) then
    /// finds something else steering.
    /// </summary>
    public enum RsrResumeMode
    {
        /// <summary>Henched when BOCCHI is loaded, Auto otherwise. See RsrManager for the why.</summary>
        AutoDetect = 0,

        Auto = 1,
        Manual = 2,
        TargetOnly = 3,

        /// <summary>Rotation only, no targeting. What BOCCHI's Illegal Mode drives RSR with.</summary>
        Henched = 4,
    }

    /// <summary>
    /// Which phantom job to raise with when the character has levelled both far enough.
    /// </summary>
    public enum PhantomRaiser
    {
        /// <summary>Whichever is already equipped; White Mage when neither is, or when swapping.</summary>
        Automatic = 0,

        /// <summary>Phantom White Mage - Occult Raise, from phantom level 4.</summary>
        WhiteMage = 1,

        /// <summary>Phantom Chemist - Revive, from phantom level 3.</summary>
        Chemist = 2,
    }

    /// <summary>
    /// Everything the plugin remembers between sessions.
    /// </summary>
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        /// <summary>
        /// Bumped when a stored config needs touching up on load rather than merely being read.
        ///
        /// 1 -> 2: the two safety numbers moved to 0 by default, "skip Brink of Death" and the Red
        /// Mage Vercure toggle stopped existing.
        /// 2 -> 3: nothing needs fixing up; the MP floor and the Occult Crescent switch both have
        /// defaults that are right for an existing config as they stand.
        /// 3 -> 4: the MP floor gained a hard minimum of one raise, so anything stored below that
        /// is raised to it.
        /// </summary>
        public const int CurrentVersion = 4;

        public int Version { get; set; } = CurrentVersion;

        /// <summary>
        /// The switch, and it governs everything below it.
        ///
        /// Off on a fresh install, and that is not a formality: a plugin that starts casting on its
        /// own the moment it is enabled is one that surprises somebody in the middle of a fight they
        /// were concentrating on.
        ///
        /// OFF MEANS OFF - raising, accepting, job switching, all of it. Every setting further down
        /// is a setting inside a plugin that is running, not an independent switch of its own.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// How long a body has to have been on the floor before it is raised, in seconds.
        ///
        /// Zero by default: this is built for Occult Crescent and FATE trains, where the thing that
        /// actually happens is that nobody else is coming, and every second of hesitation is a
        /// second somebody spends face-down. Turn it up in a party that has its own healer, where
        /// the polite thing is to let them get there first.
        /// </summary>
        public float RaiseDelaySeconds { get; set; } = 0f;

        /// <summary>
        /// How close a hostile has to be to a body before that body is left where it is, in yalms.
        ///
        /// Zero disables the check entirely, which is the default for the same reason as above: in
        /// the content this is for there is nearly always something alive nearby, and a radius large
        /// enough to matter is a radius that declines to raise anybody at all.
        /// </summary>
        public float EnemyNearBodyYalms { get; set; } = 0f;

        /// <summary>Whether to spend Swiftcast on the raise when it is off cooldown.</summary>
        public bool UseSwiftcast { get; set; } = true;

        /// <summary>
        /// Never hardcast a raise - wait for Swiftcast instead of standing still for eight seconds.
        ///
        /// Red Mage is unaffected and always has been: it goes through Dualcast, so it is already
        /// incapable of hardcasting a raise here.
        /// </summary>
        public bool SwiftcastOnly { get; set; } = false;

        /// <summary>
        /// Narrows <see cref="SwiftcastOnly"/> to combat only.
        ///
        /// The hardcast is dangerous because something is hitting you. Out of combat - the
        /// corpse-run case - eight seconds is merely eight seconds, so this lets the rule apply
        /// where it earns its keep and get out of the way where it does not.
        /// </summary>
        public bool SwiftcastOnlyInCombat { get; set; } = true;

        /// <summary>
        /// Out of combat, with Swiftcast on cooldown: change to Red Mage and raise through Dualcast.
        ///
        /// The one job that can raise instantly without Swiftcast. Costs a gearset swap and a
        /// throwaway Vercure, and is only reachable out of combat because the game refuses job
        /// changes in a fight - which is exactly the window where waiting out a 60 second cooldown
        /// is the alternative.
        /// </summary>
        public bool OutOfCombatSwitchToRdm { get; set; } = false;

        /// <summary>Which Red Mage gearset the above uses, or -1 for "the first one found".</summary>
        public int RdmGearsetId { get; set; } = -1;

        /// <summary>
        /// Don't start a raise below this much MP.
        ///
        /// A raise is 2400 MP - a quarter of the bar - and spending it is how a healer ends up
        /// unable to heal the people still standing. Below the floor the plugin does not merely
        /// decline to cast, it declines to *interfere*: Rotation Solver is left alone, because
        /// stopping somebody's rotation for a raise that was never going to happen is worse than
        /// not raising.
        ///
        /// Red Mage needing Dualcast pays Vercure's 500 on top, so its real floor is 2900. That is
        /// added automatically rather than being a second setting - it is the same rule, and the
        /// arithmetic is the game's rather than the user's.
        ///
        /// Occult Raise is exempt entirely. It costs nothing, so there is nothing to be short of.
        ///
        /// THE FLOOR CANNOT GO BELOW ONE RAISE. Anything under 2400 is not a lower setting, it is a
        /// broken one: the game refuses the cast outright below the cost, so a "floor" of 1000 does
        /// not buy more raises, it buys a plugin that keeps pausing Rotation Solver to attempt a
        /// spell it cannot afford. 2400 is the smallest number that means anything.
        /// </summary>
        public int MinimumMpForRaise { get; set; } = MinimumMpFloor;

        /// <summary>One raise. The lowest the setting above is allowed to go, and its default.</summary>
        public const int MinimumMpFloor = RezIds.RaiseMpCost;

        /// <summary>A full MP bar at level 100 - there is nothing above this to ask for.</summary>
        public const int MinimumMpCeiling = 10_000;

        /// <summary>
        /// Whether to pause Rotation Solver Reborn while raising a player so AutoRezzer gets priority.
        /// </summary>
        public bool PauseRotationSolver { get; set; } = true;

        /// <summary>What to put RSR back into afterwards. See <see cref="RsrResumeMode"/>.</summary>
        public RsrResumeMode RsrResume { get; set; } = RsrResumeMode.AutoDetect;

        /// <summary>
        /// Accept a raise cast on you, automatically, when you are the one on the floor.
        ///
        /// Separate from being able to raise - any job can be raised, so this is useful on a Warrior
        /// who will never cast anything - and it is the one thing here that acts while you are dead,
        /// which is exactly when you are least able to react to it yourself.
        ///
        /// It is NOT separate from <see cref="Enabled"/>. Nothing is.
        /// </summary>
        public bool AcceptRaise { get; set; } = true;

        /// <summary>
        /// The gearset to switch to in order to raise somebody, or -1 for "never switch".
        ///
        /// A GEARSET, NOT A JOB, and that is not a detail. The game has no "become a White Mage"
        /// verb - it has "equip this gearset", and the job is a consequence. Storing a job would
        /// mean guessing which of your three White Mage sets was meant every time, and guessing
        /// wrong means putting somebody in their glamour set in the middle of a raid.
        /// </summary>
        public int RezGearsetId { get; set; } = -1;

        /// <summary>
        /// Whether to go back to the job you were on once the raise has landed.
        ///
        /// On, and it is most of what makes the switching bearable. Being quietly left as a Sage
        /// twenty minutes after a corpse-run is how a convenience becomes a nuisance.
        /// </summary>
        public bool SwitchBackAfter { get; set; } = true;

        /// <summary>
        /// Whether to raise people who are not in your party or alliance.
        ///
        /// On, because "any dead player around" is the thing this was built for - the open-world
        /// case where somebody is face-down after a FATE and nobody else is going to bother.
        /// </summary>
        public bool RaiseStrangers { get; set; } = true;

        // ── Occult Crescent ──

        /// <summary>
        /// Use Occult Raise instead of everything else, when the zone and the phantom job allow it.
        ///
        /// On by default, and it overrides rather than supplements: inside Occult Crescent on
        /// Phantom White Mage at level 4, Occult Raise is instant, free and off cooldown every five
        /// seconds. Swiftcast, the MP floor, the hardcast rules and the job switching are all
        /// answers to costs it does not have, so all of them are skipped rather than consulted.
        ///
        /// It also works on any job at all - the phantom job is what grants it, not your class - so
        /// with this on, a Warrior in Occult Crescent raises.
        /// </summary>
        public bool UseOccultRaise { get; set; } = true;

        /// <summary>
        /// Which phantom raise to prefer when both are levelled far enough.
        ///
        /// Two support jobs grant one: White Mage's Occult Raise at phantom level 4, and Chemist's
        /// Revive at 3. They are identical in use - instant, free, five second recast - so there is
        /// no correct answer, only the one whose OTHER four actions you would rather be holding.
        /// Automatic keeps whichever is already equipped and never swaps for the sake of it.
        /// </summary>
        public PhantomRaiser PreferredPhantomRaiser { get; set; } = PhantomRaiser.Automatic;

        /// <summary>
        /// Out of combat, change phantom job to a raising one, raise, and change back.
        ///
        /// OFF BY DEFAULT, DELIBERATELY. The phantom job is the thing being levelled - that is the
        /// entire point of being in the zone - and silently taking somebody off Phantom Cannoneer
        /// mid-grind to pick a stranger up is a decision they should make rather than discover.
        /// The manual buttons on the Occult Crescent tab are there for the one-off case.
        ///
        /// Out of combat only: the game refuses a support job change in combat, and BOCCHI's own
        /// gate waits a further four seconds after combat clears before it believes one will take.
        /// </summary>
        public bool SwapPhantomJobToRaise { get; set; } = false;

        /// <summary>Whether to go back to the phantom job you were levelling once the raise lands.
        /// On, for the same reason the gearset switcher does it: being quietly left on Chemist an
        /// hour later is how a convenience becomes a nuisance.</summary>
        public bool SwapPhantomJobBack { get; set; } = true;

        /// <summary>
        /// Print a line to your own echo channel when a raise goes out.
        ///
        /// ECHO, NOT CHAT. Echo is local to your client and cannot leave it, so there is no version
        /// of this that accidentally announces to a party that something is raising for you.
        /// </summary>
        public bool AnnounceInEcho { get; set; } = false;

        // ── Local Statistics ──
        //
        // Permanent. There is no reset, deliberately: the number is only interesting because it has
        // been counting the whole time, and a running total that anybody can zero is a scoreboard
        // rather than a record.

        /// <summary>Total number of raises accepted for the local player.</summary>
        public int AcceptedRaisesCount { get; set; } = 0;

        /// <summary>Total number of raises cast by the plugin.</summary>
        public int TotalRaisesCast { get; set; } = 0;

        /// <summary>
        /// Kept, but no longer shown.
        ///
        /// Dropping the property outright would delete the history of everybody who already has one
        /// the moment their config was next written, and the per-job breakdown is the part that
        /// would be impossible to reconstruct. It costs a dictionary to keep.
        /// </summary>
        public Dictionary<uint, int> RaisesCastByJob { get; set; } = new();

        /// <summary>
        /// Fixes up a config written by an older version.
        ///
        /// The two safety numbers are moved rather than left alone: their old defaults were the
        /// whole reason this plugin would sit and watch somebody stay dead in the content it was
        /// built for, and a default change that only reaches people who never touched the setting
        /// is a default change that reaches nobody.
        /// </summary>
        public void Migrate(IPluginLog log)
        {
            if (Version >= CurrentVersion)
                return;

            if (Version < 2)
            {
                RaiseDelaySeconds = 0f;
                EnemyNearBodyYalms = 0f;
                log.Information("[AutoRezzer] Config upgraded to v2: raise delay and enemy distance reset to 0.");
            }

            if (Version < 4 && MinimumMpForRaise < MinimumMpFloor)
            {
                log.Information($"[AutoRezzer] Config upgraded to v4: MP floor {MinimumMpForRaise} "
                                + $"raised to {MinimumMpFloor}, the cost of one raise.");
                MinimumMpForRaise = MinimumMpFloor;
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
