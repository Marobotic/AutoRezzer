using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AutoRezzer.Core
{
    /// <summary>How a line of the readout should be coloured. Meaning, not colour.</summary>
    internal enum Tone
    {
        /// <summary>Working, and nothing is in the way.</summary>
        Good,

        /// <summary>Working, but waiting on something that will pass on its own.</summary>
        Warn,

        /// <summary>Cannot rez from here at all.</summary>
        Bad,

        /// <summary>Doing something other than casting: swapping, counting down.</summary>
        Info,

        /// <summary>Switched off, or deliberately sitting this content out.</summary>
        Idle,
    }

    /// <summary>
    /// Everything the top card says, worked out in one place.
    ///
    /// ── WHY THIS IS A TYPE AND NOT FOUR CALLS FROM THE WINDOW ─────────────
    /// The card answers one question with two lines: what can you rez with, and what is happening.
    /// Both are composed from five different objects (the job, the sync, the phantom job, the zone,
    /// the executor's hold) and the composition is the hard part, not the drawing. Leaving it in the
    /// window meant the window knew about level sync, which is how a UI ends up with rules in it.
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    /// <param name="JobName">The job doing the rezzing, phantom or otherwise.</param>
    /// <param name="Level">"Lv 100", or "Lv 50 of 100" when the level is synced down.</param>
    /// <param name="IconId">Game icon to draw, or 0 when there is nothing to draw.</param>
    /// <param name="Ability">What this job can rez with, or why it cannot.</param>
    /// <param name="AbilityTone">How to colour the above.</param>
    /// <param name="State">What the plugin is doing right now.</param>
    /// <param name="Detail">One clause under it, or empty.</param>
    /// <param name="StateTone">How to colour the state.</param>
    internal readonly record struct Readout(
        string JobName,
        string Level,
        uint IconId,
        string Ability,
        Tone AbilityTone,
        string State,
        string Detail,
        Tone StateTone);

    /// <summary>
    /// Composes the readout from everything else.
    ///
    /// Read-only over the plugin's live state, and called from Draw rather than from the tick, so
    /// nothing in here may cost more than a few pointer reads and a sheet lookup.
    /// </summary>
    internal sealed class StatusReporter
    {
        /// <summary>The icon set for jobs. 62100 is the framed round set the party list uses; the
        /// plain set sits at 62000 if these ever look wrong against the rest of the window.</summary>
        private const uint JobIconBase = 62100;

        private readonly IObjectTable objectTable;
        private readonly IDataManager dataManager;
        private readonly Configuration config;
        private readonly RezTargeting targeting;
        private readonly RezExecutor executor;
        private readonly JobSwitcher switcher;
        private readonly PhantomJobSwitcher phantomSwitcher;
        private readonly PhantomJobs phantom;
        private readonly Zone zone;

        public StatusReporter(IObjectTable objectTable, IDataManager dataManager,
            Configuration config, RezTargeting targeting, RezExecutor executor, JobSwitcher switcher,
            PhantomJobSwitcher phantomSwitcher, PhantomJobs phantom, Zone zone)
        {
            this.objectTable = objectTable;
            this.dataManager = dataManager;
            this.config = config;
            this.targeting = targeting;
            this.executor = executor;
            this.switcher = switcher;
            this.phantomSwitcher = phantomSwitcher;
            this.phantom = phantom;
            this.zone = zone;
        }

        /// <summary>
        /// Whether the phantom job in hand is going to be the thing that casts the rez.
        ///
        /// Asked by the status card and by the window's phantom panel, so that "Active" on screen
        /// and the plugin's own behaviour can never disagree about which job is doing the work.
        /// </summary>
        public bool PhantomRezReady()
            => config.UseOccultRaise && phantom.InOccultCrescent && phantom.CanOccultRaise;

        /// <summary>Whether the four content switches allow this kind of place.</summary>
        public bool RunsHere(ContentKind kind) => kind switch
        {
            ContentKind.Instance => config.RunInInstances,

            // The Crescent is a field operation. It answers to the same switch, because a player who
            // wants the plugin quiet in Bozja does not want it loud one zone over.
            ContentKind.FieldOperation or ContentKind.OccultCrescent => config.RunInFieldOperations,

            _ => config.RunInOpenWorld,
        };

        public Readout Build()
        {
            var (jobName, level, icon, ability, abilityTone) = DescribeAbility();
            var (state, detail, stateTone) = DescribeState(abilityTone);
            return new Readout(jobName, level, icon, ability, abilityTone, state, detail, stateTone);
        }

        /// <summary>
        /// ── IS THERE ANY ROUTE TO A REZ FROM HERE AT ALL? ─────────────────
        /// The question that decides whether the plugin watches, and it is asked before the sweep
        /// rather than after it.
        ///
        /// A Gunbreaker with no rez gearset and no phantom job cannot rez anybody, will not be able
        /// to in a minute, and there is nothing for a sweep to find out. Counting bodies for them
        /// is work done to reach a conclusion already known, and worse, it makes the window say
        /// "watching" about a situation nothing is watching for.
        ///
        /// Four routes, and any one of them is enough:
        ///
        ///   the phantom job you are on has a rez
        ///   your class has one, and the MP for it
        ///   a phantom job that has one can be borrowed
        ///   a gearset that has one can be equipped
        ///
        /// MP counts here rather than being a hold, because "no MP, no phantom rez" is the same
        /// dead end as the wrong job: there is nothing to do until the bar refills, and the plugin
        /// should be quiet until it does.
        /// </summary>
        public bool AnyRouteToRez()
        {
            // The phantom job in hand. Free, instant, and it does not care what class you are.
            if (config.UseOccultRaise && phantom.InOccultCrescent && phantom.CanOccultRaise)
                return true;

            // Borrowing one. Only worth counting where the setting allows it and a levelled raiser
            // exists to borrow.
            if (config.UseOccultRaise && config.SwapPhantomJobToRaise
                && phantom.InOccultCrescent && phantom.AnyRaiserAvailable)
            {
                return true;
            }

            var me = objectTable.LocalPlayer;
            if (me == null)
                return false;

            bool canRezNow = targeting.CanRaise(out uint action);

            // ── A NON-REZ JOB INSIDE A DUTY IS A DEAD END, FULL STOP ──────
            //
            // Not a hold, not "watching in case": there is genuinely no way out of it. The game
            // refuses gearset changes in combat, a dungeon has no phantom jobs to borrow, and the
            // fight ends when the duty does. A Gunbreaker in a raid should never have been told the
            // plugin was watching, because there was nothing it could ever have done.
            //
            // Field operations are the exception, and that is why they are not covered here: Bozja
            // and the Crescent are open enough to leave combat in, and the Crescent hands out a rez
            // to any job at all.
            if (!canRezNow && zone.Kind == ContentKind.Instance)
                return false;

            if (canRezNow
                && (executor.HasMpFor(action) || executor.ThinAirAvailable()))
            {
                return true;
            }

            // Changing clothes. The gearset has to be a real one on a job that can rez, or this is
            // a route to standing in a healer's gear looking at a body.
            if (config.RezGearsetId >= 0)
            {
                uint job = JobSwitcher.JobOfGearset(config.RezGearsetId);
                if (job != 0 && RezIds.RaiseActionFor(job) != 0)
                    return true;
            }

            // Red Mage, which needs no setting: a levelled one with a gearset saved is always a
            // route, because the detour that uses it is always on.
            return JobSwitcher.LevelOfJob(RezIds.RedMage, dataManager) >= RezIds.VerraiseLevel
                   && JobSwitcher.FirstGearsetForJob(RezIds.RedMage) >= 0;
        }

        // ══════════════════════════════════════════════════════════
        //  WHAT CAN YOU REZ WITH
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The top half of the card.
        ///
        /// ── THE LEVEL RULES LIVE HERE, ON SCREEN, RATHER THAN IN A README ──
        /// Three of them, and every one is a real question somebody has asked out loud:
        ///
        ///   A level 100 Red Mage synced to 50 has no Verraise. It unlocks at 64, sync takes it
        ///   away, and the plugin looks broken until something says so.
        ///
        ///   A White Mage between 12 and 17 has Raise but no Swiftcast, which does not arrive until
        ///   18. It can rez; it just has to stand still for eight seconds to do it. Every other
        ///   healer is spared this because Scholar and Astrologian start at 30 and Sage at 70.
        ///
        ///   A Gunbreaker has nothing at any level, and saying "no rez on this job" is kinder than
        ///   letting somebody hunt through settings for the reason.
        /// ─────────────────────────────────────────────────────────────────
        /// </summary>
        private (string Job, string Level, uint Icon, string Ability, Tone Tone) DescribeAbility()
        {
            var me = objectTable.LocalPlayer;
            if (me == null)
                return ("Not logged in", string.Empty, 0, string.Empty, Tone.Idle);

            uint job = me.ClassJob.RowId;
            string jobName = RezIds.JobName(job, dataManager);
            uint jobIcon = job == 0 ? 0u : JobIconBase + job;

            // ── THE SYNCED LEVEL IS THE ONLY ONE SHOWN ──
            //
            // The real level is still read, because "Verraise needs Lv 64, synced too low" is a very
            // different sentence from "Verraise unlocks at Lv 64" and only the comparison can tell
            // them apart. But it is never printed on its own: "Lv 50 of 100" is bookkeeping, and the
            // pill is there to say what this character can do right now, not to narrate arithmetic.
            int synced = me.Level;
            int real = TrueLevelOf(me);
            string levelText = $"Lv {synced}";

            // ── THE CARD IS ABOUT YOU, NOT ABOUT YOUR PHANTOM JOB ────────
            //
            // It used to swap its whole identity to the phantom job whenever one was equipped, which
            // produced the worst possible reading of a very ordinary situation: a White Mage on
            // Phantom Dragoon saw "Phantom Dragoon, this phantom job can't rez", when they were
            // standing there perfectly able to rez as a White Mage.
            //
            // Worse, that verdict was Bad, and a Bad verdict was what the state line turned into
            // "Paused". So the window said paused while the server bar said on, because the bar was
            // asking the right question and the card was answering a different one.
            //
            // Your job, your icon, your rez. The phantom job gets its own card underneath, which is
            // where "and this is what will actually cast it" belongs.
            if (PhantomRezReady())
                return (jobName, levelText, jobIcon, "Ready to rez", Tone.Good);

            uint raise = RezIds.RaiseActionFor(job);
            if (raise == 0)
                return (jobName, levelText, jobIcon, string.Empty, Tone.Bad);

            int need = RezIds.RaiseLevelFor(job);

            if (synced < need)
            {
                // The sync case reads differently even though the number is the same: one is a
                // character who has not got there yet, the other is a character who has and had it
                // taken away at the duty's door.
                string why = real > synced
                    ? $"Synced too low, needs {need}"
                    : $"Needs level {need}";

                return (jobName, levelText, jobIcon, why, Tone.Bad);
            }

            // ── Red Mage never hardcasts, so Swiftcast is not its story ──
            if (raise == RezIds.Verraise)
                return (jobName, levelText, jobIcon, "Ready to rez", Tone.Good);

            if (synced < RezIds.SwiftcastLevel)
            {
                return (jobName, levelText, jobIcon,
                        $"Ready, no Swiftcast until {RezIds.SwiftcastLevel}", Tone.Warn);
            }

            int left = executor.SwiftcastSecondsLeft();
            if (left > 0)
                return (jobName, levelText, jobIcon, $"Swiftcast in {left}s", Tone.Warn);

            return (jobName, levelText, jobIcon, "Ready to rez", Tone.Good);
        }

        /// <summary>
        /// The level on the character sheet, as opposed to the one sync has left you with.
        ///
        /// Only used to say "of 100" after a synced level. Failing to read it is not an error worth
        /// reporting: the readout simply stops mentioning the sync.
        /// </summary>
        private static unsafe int TrueLevelOf(Dalamud.Game.ClientState.Objects.SubKinds.IPlayerCharacter me)
        {
            try
            {
                if (!me.ClassJob.IsValid)
                    return 0;

                sbyte slot = me.ClassJob.Value.ExpArrayIndex;
                if (slot < 0)
                    return 0;

                var state = PlayerState.Instance();
                if (state == null)
                    return 0;

                var levels = state->ClassJobLevels;
                return slot >= levels.Length ? 0 : levels[slot];
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // ══════════════════════════════════════════════════════════
        //  WHAT IS HAPPENING
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The bottom half of the card, in the order the plugin itself decides things.
        ///
        /// Deliberately the same order as Plugin.Tick, so the card can never describe a branch that
        /// is not the one being taken. Anything that reads state out of order eventually says
        /// "watching" during a job swap.
        /// </summary>
        private (string State, string Detail, Tone Tone) DescribeState(Tone abilityTone)
        {
            if (!config.Enabled)
                // Off says off. There is no second line, because there is nothing a second line
                // could add: the switch above it is right there and already says the same thing.
                return ("Off", string.Empty, Tone.Idle);

            if (!config.RezInSanctuaries && zone.InSanctuary)
                return ("Paused", string.Empty, Tone.Idle);

            var kind = zone.Kind;
            if (!RunsHere(kind))
                return ("Paused", $"{Zone.NameOf(kind)} is off", Tone.Idle);

            var me = objectTable.LocalPlayer;
            if (me == null)
                return ("Waiting", string.Empty, Tone.Idle);

            if (me.IsDead)
            {
                if (!config.AcceptRaise)
                    return ("You're down", "Accepting rezzes is switched off", Tone.Info);

                float wait = config.AcceptRaiseDelaySeconds;
                return ("You're down", wait > 0f ? $"Accepting after {Fmt(wait)}" : "Accepting any rez cast on you", Tone.Info);
            }

            if (phantomSwitcher.Busy)
                return (phantomSwitcher.Describe(), string.Empty, Tone.Info);

            // Nothing this character could do about a body even if one turned up. Said plainly and
            // early, because it is the difference between "idle" and "idle for a reason".
            if (!AnyRouteToRez())
            {
                // Two different dead ends, and they deserve different sentences. One is permanent
                // for this character until they level something; the other clears itself in a few
                // seconds of standing still.
                return abilityTone == Tone.Bad
                    ? ("Paused", config.AcceptRaise ? "Still accepting rezzes on you" : string.Empty, Tone.Idle)
                    : ("Not enough MP", string.Empty, Tone.Idle);
            }

            if (switcher.Busy)
                return (switcher.Describe(), NameOfGearset(switcher.TargetGearset), Tone.Info);

            var seen = targeting.LastSurroundings;
            string target = targeting.LastTargetName;

            if (!string.IsNullOrEmpty(target))
                return (Downs(seen.Dead), $"Rezzing {target}", Tone.Good);

            // A hold is only interesting while there is somebody to hold for. The executor's reason
            // outranks the sweep's: "waiting on Swiftcast" is the specific answer, and "can't reach
            // them" is what is left when nothing more precise applies.
            if (seen.Dead > seen.Raised)
            {
                string why = targeting.LastReason;
                if (string.IsNullOrEmpty(why))
                    why = executor.HoldReason;

                if (!string.IsNullOrEmpty(why))
                    return (why, "Your rotation is still running", Tone.Warn);

                return (Downs(seen.Dead), "Working on it", Tone.Good);
            }

            if (seen.Players == 0)
                return ("Watching", "Nobody around", Tone.Idle);

            return ("Watching", seen.Players == 1 ? "1 nearby, all up" : $"{seen.Players} nearby, all up", Tone.Good);
        }

        private static string Downs(int dead)
            => dead == 1 ? "1 down nearby" : $"{dead} down nearby";

        private static string Fmt(float seconds)
            => Math.Abs(seconds - MathF.Round(seconds)) < 0.05f
                ? $"{(int)MathF.Round(seconds)}s"
                : $"{seconds:0.0}s";

        // ══════════════════════════════════════════════════════════
        //  ICONS, FROM THE GAME'S OWN SHEETS
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The icon the game draws for an action, straight off the Action sheet.
        ///
        /// USED INSTEAD OF A PHANTOM JOB ICON ID, and on purpose. Phantom jobs have no icon column
        /// anywhere in the MKD sheets, so the alternative was a hardcoded icon range found by
        /// guesswork, which is exactly the sort of magic number that stops being true on a patch
        /// day. The raise action's icon is the picture the player already has on their hotbar for
        /// this, it comes from data, and it cannot go stale.
        /// </summary>
        public uint IconOfAction(uint actionId)
        {
            if (actionId == 0)
                return 0;

            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>();
                if (sheet != null && sheet.TryGetRow(actionId, out var row))
                    return row.Icon;
            }
            catch (Exception)
            {
                // An icon is decoration. Nothing here is worth a log line, let alone a throw.
            }

            return 0;
        }

        /// <summary>The framed job icon for a class, or 0.</summary>
        public static uint IconOfJob(uint job) => job == 0 ? 0u : JobIconBase + job;

        private string NameOfGearset(int gearsetId)
        {
            if (gearsetId < 0)
                return string.Empty;

            uint job = JobSwitcher.JobOfGearset(gearsetId);
            return job == 0 ? string.Empty : $"To {RezIds.JobName(job, dataManager)}";
        }
    }
}
