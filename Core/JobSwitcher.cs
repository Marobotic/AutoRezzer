using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.UI.Misc;

namespace AutoRezzer.Core
{
    /// <summary>
    /// Switches to the raising job, raises, and switches back.
    ///
    /// ── WHY THIS IS A STATE MACHINE AND NOT THREE LINES ───────────────────
    /// Every step of it is asynchronous and can fail in the middle. Equipping a gearset is a request,
    /// not an assignment: the job changes some frames later, or does not change at all because the
    /// player is in combat, or changes and then the body gets raised by somebody else while the gear
    /// is still swapping. Written as a sequence of "do this, then that" it would strand people on
    /// the wrong job, which is the single most annoying way this plugin could misbehave.
    ///
    /// So each state does one thing, checks one condition, and has a timeout. The worst case is
    /// always "give up and go back", never "sit here forever as a Sage".
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    internal sealed class JobSwitcher
    {
        internal enum Phase
        {
            /// <summary>Nothing going on. The normal state.</summary>
            Idle,

            /// <summary>Gearset equipped, waiting for the job to actually change.</summary>
            Switching,

            /// <summary>On the raising job, trying to land the cast.</summary>
            Raising,

            /// <summary>Done (or given up); putting the original gearset back.</summary>
            Returning,
        }

        private readonly IObjectTable objectTable;
        private readonly ICondition condition;
        private readonly IPluginLog log;
        private readonly Configuration config;

        /// <summary>The gearset we were on when this started, to go back to. -1 when nothing is in
        /// flight - and it is written BEFORE the first equip, because once the swap begins the
        /// game's own idea of the current gearset is already the new one.</summary>
        private int returnToGearset = -1;

        /// <summary>The gearset this trip is heading for. Held rather than read back out of the
        /// config each tick, because there are now two reasons to switch - the wrong job, and the
        /// out-of-combat Red Mage route - and a trip that changed its mind halfway would equip one
        /// gearset and then wait forever for a different job to appear.</summary>
        private int targetGearset = -1;

        private long phaseSince;

        /// <summary>
        /// Ceilings on each phase.
        ///
        /// Generous, because a gear swap in a busy zone is genuinely slow, and short enough that a
        /// switch that is never going to complete does not hold the machine open. Raising is the
        /// long one: it has to cover a hardcast plus the delay before the status appears.
        /// </summary>
        private const long SwitchTimeoutMs = 10_000;
        private const long RaiseTimeoutMs = 25_000;
        private const long ReturnTimeoutMs = 30_000;

        public JobSwitcher(IObjectTable objectTable, ICondition condition, IPluginLog log, Configuration config)
        {
            this.objectTable = objectTable;
            this.condition = condition;
            this.log = log;
            this.config = config;
        }

        public Phase Current { get; private set; } = Phase.Idle;

        /// <summary>Whether anything is mid-flight, so the ordinary path knows to keep its hands
        /// off and the window knows to say what is happening.</summary>
        public bool Busy => Current != Phase.Idle;

        /// <summary>Which gearset the trip in flight is heading for, or -1 when idle.</summary>
        public int TargetGearset => targetGearset;

        public string Describe() => Current switch
        {
            Phase.Switching => "Switching job to raise...",
            Phase.Raising => "Raising.",
            Phase.Returning => "Switching back...",
            _ => string.Empty,
        };

        /// <summary>The same thing as a clause, for the middle of the window's status sentence.</summary>
        public string ShortPhase => Current switch
        {
            Phase.Switching => "switching job",
            Phase.Raising => "raising",
            Phase.Returning => "switching back",
            _ => string.Empty,
        };

        /// <summary>
        /// Whether a switch could be started right now.
        ///
        /// COMBAT IS THE REAL ONE. The game simply refuses a gearset change in combat, so without
        /// this the plugin would fire an equip request every tick of a fight and achieve nothing but
        /// noise. The rest are the states where a swap either cannot happen or would cancel
        /// something the player was doing.
        /// </summary>
        public bool CanSwitchNow() => CanSwitchOnceQuiet() && !condition[ConditionFlag.Casting];

        /// <summary>
        /// The same question minus the one blocker that stopping a rotation actually clears.
        ///
        /// ── WHY THE TWO ARE SEPARATE ──────────────────────────────────────
        /// Casting is different in kind from everything else on the list. In combat is a fact about
        /// the fight and no amount of pausing changes it; mid-cast, three hundred milliseconds from
        /// now, is a fact about Rotation Solver, and pausing Rotation Solver is precisely what ends
        /// it.
        ///
        /// The caller needs both, and needs them in this order: pausing on CanSwitchNow alone
        /// deadlocks - RSR is casting, so the switch is refused, so nothing pauses RSR, so it is
        /// still casting - while pausing on nothing at all was the old bug, where a Gunbreaker in a
        /// raid lost their rotation for a gearset change the game was never going to allow.
        ///
        /// So this is the "worth interrupting a rotation for" question, and CanSwitchNow is the
        /// "the game will take it this instant" one.
        /// ─────────────────────────────────────────────────────────────────
        /// </summary>
        public bool CanSwitchOnceQuiet()
        {
            var me = objectTable.LocalPlayer;
            if (me == null || me.IsDead)
                return false;

            return !condition[ConditionFlag.InCombat]
                   && !condition[ConditionFlag.BetweenAreas]
                   && !condition[ConditionFlag.Occupied]
                   && !condition[ConditionFlag.Occupied33]
                   && !condition[ConditionFlag.Occupied38]
                   && !condition[ConditionFlag.Mounted]
                   && !condition[ConditionFlag.Unconscious]
                   && !condition[ConditionFlag.InDutyQueue];
        }

        /// <summary>
        /// What level a job is on this character, without being on it.
        ///
        /// Needed because the Red Mage detour has to know whether Red Mage can actually rez before
        /// it takes anybody's gear off. Verraise unlocks at 64, so swapping a level 40 Red Mage in
        /// buys a gearset change and a job that still cannot do the thing.
        /// </summary>
        public static unsafe int LevelOfJob(uint job, IDataManager dataManager)
        {
            try
            {
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.ClassJob>();
                if (sheet == null || !sheet.TryGetRow(job, out var row))
                    return 0;

                sbyte slot = row.ExpArrayIndex;
                if (slot < 0)
                    return 0;

                var state = FFXIVClientStructs.FFXIV.Client.Game.UI.PlayerState.Instance();
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

        /// <summary>The job a gearset will put you on, or 0 if it is not a real gearset.</summary>
        public static unsafe uint JobOfGearset(int gearsetId)
        {
            if (gearsetId < 0)
                return 0;

            var module = RaptureGearsetModule.Instance();
            if (module == null || !module->IsValidGearset(gearsetId))
                return 0;

            var entry = module->GetGearset(gearsetId);
            return entry == null ? 0u : entry->ClassJob;
        }

        /// <summary>
        /// One entry per JOB that this character can actually rez on, with the gearset to get there.
        ///
        /// ── THREE FILTERS, AND EVERY ONE OF THEM REMOVES A DEAD OPTION ────
        /// Deduplicated by job, because three White Mage gearsets are one choice presented three
        /// times: the picker is answering "which job should I become", and the gearset is the
        /// mechanism, not the question. The first one found wins, which is also what the Red Mage
        /// route has always done.
        ///
        /// Filtered by level, because a Red Mage at 40 has no Verraise and a gearset that puts you
        /// on one is a gearset that puts you in front of a body you still cannot pick up.
        ///
        /// And filtered by existing at all, which is what listing gearsets rather than jobs already
        /// gives us: a job with no gearset is a job the game has no verb for becoming.
        /// </summary>
        public static unsafe List<(int Id, string Name, uint Job)> RezJobChoices(IDataManager dataManager)
        {
            var byJob = new Dictionary<uint, (int, string, uint)>();

            foreach (var (id, name, job) in RaiseCapableGearsets())
            {
                if (byJob.ContainsKey(job))
                    continue;

                if (LevelOfJob(job, dataManager) < RezIds.RaiseLevelFor(job))
                    continue;

                byJob[job] = (id, name, job);
            }

            return new List<(int, string, uint)>(byJob.Values);
        }

        /// <summary>
        /// Whether this character could rez on anything at all.
        ///
        /// Levels only, deliberately: a White Mage with no gearset saved still rezzes perfectly well
        /// while they are playing White Mage. This answers "is rezzing possible for this character",
        /// which is what decides whether the window shows any rezzing settings.
        /// </summary>
        public static bool CanRezOnAnyJob(IDataManager dataManager)
        {
            foreach (uint job in RezIds.RaisingJobs)
            {
                if (LevelOfJob(job, dataManager) >= RezIds.RaiseLevelFor(job))
                    return true;
            }

            return false;
        }

        /// <summary>Every gearset that would put you on something able to raise, for the picker.</summary>
        public static unsafe List<(int Id, string Name, uint Job)> RaiseCapableGearsets()
        {
            var found = new List<(int, string, uint)>();

            var module = RaptureGearsetModule.Instance();
            if (module == null)
                return found;

            for (int i = 0; i < 100; i++)
            {
                if (!module->IsValidGearset(i))
                    continue;

                var entry = module->GetGearset(i);
                if (entry == null)
                    continue;

                uint job = entry->ClassJob;
                if (RezIds.RaiseActionFor(job) == 0)
                    continue;

                found.Add((i, entry->NameString, job));
            }

            return found;
        }

        /// <summary>
        /// Begins the trip: remembers where we came from and asks for the raising gearset.
        ///
        /// Returns false when it could not start, which is the common case and not an error - in
        /// combat, no gearset chosen, or already on the right job.
        /// </summary>
        public unsafe bool Begin(int gearsetId)
        {
            if (Busy || gearsetId < 0)
                return false;

            if (!CanSwitchNow())
                return false;

            var module = RaptureGearsetModule.Instance();
            if (module == null || !module->IsValidGearset(gearsetId))
                return false;

            // Written before the equip, or we would be recording the gearset we are moving TO.
            returnToGearset = module->CurrentGearsetIndex;
            if (returnToGearset == gearsetId)
                return false;

            targetGearset = gearsetId;

            log.Information($"[AutoRezzer] Switching to gearset {gearsetId} to raise.");
            _ = module->EquipGearset(gearsetId);

            Enter(Phase.Switching);
            return true;
        }

        /// <summary>
        /// The first gearset that would put you on a given job, or -1.
        ///
        /// The default for the Red Mage route, so somebody who owns exactly one Red Mage set - which
        /// is most people who own any - never has to pick it out of a list to switch the option on.
        /// </summary>
        public static unsafe int FirstGearsetForJob(uint job)
        {
            var module = RaptureGearsetModule.Instance();
            if (module == null)
                return -1;

            for (int i = 0; i < 100; i++)
            {
                if (!module->IsValidGearset(i))
                    continue;

                var entry = module->GetGearset(i);
                if (entry != null && entry->ClassJob == job)
                    return i;
            }

            return -1;
        }

        /// <summary>Every gearset on one particular job, for the Red Mage picker.</summary>
        public static unsafe List<(int Id, string Name)> GearsetsForJob(uint job)
        {
            var found = new List<(int, string)>();

            var module = RaptureGearsetModule.Instance();
            if (module == null)
                return found;

            for (int i = 0; i < 100; i++)
            {
                if (!module->IsValidGearset(i))
                    continue;

                var entry = module->GetGearset(i);
                if (entry != null && entry->ClassJob == job)
                    found.Add((i, entry->NameString));
            }

            return found;
        }

        /// <summary>
        /// Moves the machine along one step.
        ///
        /// <paramref name="stillWantsRaising"/> is asked fresh every tick rather than captured at the
        /// start: somebody else may have raised the body while our gear was swapping, and finishing
        /// the trip to cast at a corpse that is already standing up is exactly the sort of thing
        /// that makes an automated plugin look stupid.
        ///
        /// <paramref name="tryRaise"/> returns true once the raise has actually landed.
        /// </summary>
        public unsafe void Tick(Func<bool> stillWantsRaising, Func<bool> tryRaise)
        {
            if (Current == Phase.Idle)
                return;

            long now = Environment.TickCount64;
            long elapsed = now - phaseSince;

            var module = RaptureGearsetModule.Instance();
            if (module == null)
            {
                Enter(Phase.Idle);
                return;
            }

            switch (Current)
            {
                case Phase.Switching:
                {
                    uint want = JobOfGearset(targetGearset);
                    var me = objectTable.LocalPlayer;

                    if (me != null && want != 0 && me.ClassJob.RowId == want)
                    {
                        Enter(Phase.Raising);
                        return;
                    }

                    // Nobody left to raise, or it took too long. Either way, go home.
                    if (!stillWantsRaising() || elapsed > SwitchTimeoutMs)
                    {
                        log.Debug("[AutoRezzer] Switch abandoned; returning.");
                        Enter(Phase.Returning);
                    }

                    return;
                }

                case Phase.Raising:
                {
                    if (!stillWantsRaising() || elapsed > RaiseTimeoutMs)
                    {
                        Enter(Phase.Returning);
                        return;
                    }

                    // THE RETURN IS NOT TRIGGERED HERE. tryRaise returning true means the action was
                    // sent, not that it resolved - and equipping a gearset mid-cast cancels the cast,
                    // which would make this plugin reliably interrupt its own raise. The trip home
                    // waits for stillWantsRaising to go false, which happens once the body carries
                    // the Raise status, or for the timeout.
                    _ = tryRaise();
                    return;
                }

                case Phase.Returning:
                {
                    if (!config.SwitchBackAfter || returnToGearset < 0)
                    {
                        targetGearset = -1;
                        Enter(Phase.Idle);
                        return;
                    }

                    var me = objectTable.LocalPlayer;
                    uint home = JobOfGearset(returnToGearset);

                    if (me != null && home != 0 && me.ClassJob.RowId == home)
                    {
                        log.Information("[AutoRezzer] Back on the original job.");
                        returnToGearset = -1;
                        targetGearset = -1;
                        Enter(Phase.Idle);
                        return;
                    }

                    if (elapsed > ReturnTimeoutMs)
                    {
                        log.Warning("[AutoRezzer] Gave up switching back; still on the raising job.");
                        returnToGearset = -1;
                        targetGearset = -1;
                        Enter(Phase.Idle);
                        return;
                    }

                    // Re-asked rather than fired once: the first attempt may have landed while the
                    // player was still in combat from the pull that killed everybody, and the equip
                    // is cheap enough to repeat every half second until it takes.
                    if (CanSwitchNow() && elapsed % 500 < 250)
                        _ = module->EquipGearset(returnToGearset);

                    return;
                }
            }
        }

        /// <summary>
        /// Gives up on the raise but still goes home, if there is a home to go to.
        ///
        /// The difference from Cancel matters when the plugin is switched off mid-swap: Cancel
        /// abandons the trip where it stands, which means abandoning the player on the raising
        /// gearset. Turning a plugin off should not cost somebody their gear.
        /// </summary>
        public void Abort()
        {
            if (Busy && returnToGearset >= 0 && config.SwitchBackAfter)
            {
                Enter(Phase.Returning);
                return;
            }

            Cancel();
        }

        /// <summary>Abandons whatever is in flight, where it stands. For when the player has taken
        /// the wheel themselves and undoing their choice would be the rude thing to do.</summary>
        public void Cancel()
        {
            returnToGearset = -1;
            targetGearset = -1;
            Enter(Phase.Idle);
        }

        private void Enter(Phase phase)
        {
            Current = phase;
            phaseSince = Environment.TickCount64;
        }
    }
}
