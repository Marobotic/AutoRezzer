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

        public string Describe() => Current switch
        {
            Phase.Switching => "Switching job to raise...",
            Phase.Raising => "Raising.",
            Phase.Returning => "Switching back...",
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
        public bool CanSwitchNow()
        {
            var me = objectTable.LocalPlayer;
            if (me == null || me.IsDead)
                return false;

            return !condition[ConditionFlag.InCombat]
                   && !condition[ConditionFlag.BetweenAreas]
                   && !condition[ConditionFlag.Casting]
                   && !condition[ConditionFlag.Occupied]
                   && !condition[ConditionFlag.Occupied33]
                   && !condition[ConditionFlag.Occupied38]
                   && !condition[ConditionFlag.Mounted]
                   && !condition[ConditionFlag.Unconscious]
                   && !condition[ConditionFlag.InDutyQueue];
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
        public unsafe bool Begin()
        {
            if (Busy || config.RezGearsetId < 0)
                return false;

            if (!CanSwitchNow())
                return false;

            var module = RaptureGearsetModule.Instance();
            if (module == null || !module->IsValidGearset(config.RezGearsetId))
                return false;

            // Written before the equip, or we would be recording the gearset we are moving TO.
            returnToGearset = module->CurrentGearsetIndex;
            if (returnToGearset == config.RezGearsetId)
                return false;

            log.Information($"[AutoRezzer] Switching to gearset {config.RezGearsetId} to raise.");
            _ = module->EquipGearset(config.RezGearsetId);

            Enter(Phase.Switching);
            return true;
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
                    uint want = JobOfGearset(config.RezGearsetId);
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
                        Enter(Phase.Idle);
                        return;
                    }

                    var me = objectTable.LocalPlayer;
                    uint home = JobOfGearset(returnToGearset);

                    if (me != null && home != 0 && me.ClassJob.RowId == home)
                    {
                        log.Information("[AutoRezzer] Back on the original job.");
                        returnToGearset = -1;
                        Enter(Phase.Idle);
                        return;
                    }

                    if (elapsed > ReturnTimeoutMs)
                    {
                        log.Warning("[AutoRezzer] Gave up switching back; still on the raising job.");
                        returnToGearset = -1;
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

        /// <summary>Abandons whatever is in flight. Called when the plugin is switched off, so it
        /// does not silently resume a trip nobody wants any more.</summary>
        public void Cancel()
        {
            returnToGearset = -1;
            Enter(Phase.Idle);
        }

        private void Enter(Phase phase)
        {
            Current = phase;
            phaseSince = Environment.TickCount64;
        }
    }
}
