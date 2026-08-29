using System;
using Dalamud.Plugin.Services;

namespace AutoRezzer.Core
{
    /// <summary>
    /// Borrows a raising phantom job long enough to pick somebody up, then gives it back.
    ///
    /// ── WHY THIS IS SEPARATE FROM JobSwitcher ─────────────────────────────
    /// They rhyme - remember, change, raise, change back - but they are changing different things
    /// under different rules, and folding them together would mean one state machine with two sets
    /// of timeouts and a flag saying which it is. A gearset change is a request the game answers
    /// several frames later and refuses outright in combat; a support job change is a direct call
    /// that reports nothing and has to be read back, refused in combat AND for four seconds after
    /// it ends.
    ///
    /// The pair is also legitimately nestable in principle - wrong class AND wrong phantom job - and
    /// keeping them apart is what stops that becoming a question anyone has to think about. Plugin
    /// simply prefers this one: it is instant and free where the gearset swap costs a loading pause
    /// and a trip back.
    ///
    /// ── WHAT IT IS PROTECTING ────────────────────────────────────────────
    /// The phantom job IS the thing being levelled. That is what the zone is for. So the worst case
    /// here is not "failed to raise", it is "left them on Chemist an hour into a Cannoneer grind",
    /// and every path out of every state leads back to the job they started on.
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    internal sealed class PhantomJobSwitcher
    {
        internal enum Phase
        {
            Idle,

            /// <summary>Asked for the raising job, waiting for the game to agree it happened.</summary>
            Switching,

            /// <summary>On the raising job, trying to land the cast.</summary>
            Raising,

            /// <summary>Done, or given up; putting the levelling job back.</summary>
            Returning,
        }

        private readonly PhantomJobs phantom;
        private readonly Configuration config;
        private readonly IPluginLog log;

        /// <summary>The job to give back, or -1. Written before the first change request, because
        /// afterwards the game's own answer is already the new one.</summary>
        private int returnToJob = -1;

        private int targetJob = -1;
        private long phaseSince;

        /// <summary>
        /// Ceilings on each phase.
        ///
        /// Much tighter than the gearset switcher's. A support job change either takes within a
        /// frame or two or is being refused, so five seconds is already generous; raising is the
        /// long one only because the recast is five seconds and the body may be moving into range.
        /// </summary>
        private const long SwitchTimeoutMs = 5_000;
        private const long RaiseTimeoutMs = 15_000;
        private const long ReturnTimeoutMs = 30_000;

        public PhantomJobSwitcher(PhantomJobs phantom, Configuration config, IPluginLog log)
        {
            this.phantom = phantom;
            this.config = config;
            this.log = log;
        }

        public Phase Current { get; private set; } = Phase.Idle;

        public bool Busy => Current != Phase.Idle;

        /// <summary>The job being borrowed, or -1.</summary>
        public int TargetJob => targetJob;

        /// <summary>The job waiting to be given back, or -1. Read by the window so a manual switch
        /// can say what it is cancelling.</summary>
        public int ReturnToJob => returnToJob;

        public string Describe() => Current switch
        {
            Phase.Switching => $"Changing phantom job to {phantom.NameOf(targetJob)}...",
            Phase.Raising => "Raising.",
            Phase.Returning => $"Changing phantom job back to {phantom.NameOf(returnToJob)}...",
            _ => string.Empty,
        };

        public string ShortPhase => Current switch
        {
            Phase.Switching => "changing phantom job",
            Phase.Raising => "raising",
            Phase.Returning => "changing phantom job back",
            _ => string.Empty,
        };

        /// <summary>
        /// Starts the borrow. Returns false when it could not, which is ordinary rather than an
        /// error - in combat, inside the post-combat settle, or already on a job that can raise.
        /// </summary>
        public bool Begin(int jobRow)
        {
            if (Busy || jobRow < 0)
                return false;

            if (!phantom.CanRaiseAs(jobRow) || !phantom.CanChangeJobNow())
                return false;

            int current = phantom.CurrentJobRow;
            if (current == jobRow)
                return false;

            returnToJob = current;
            targetJob = jobRow;

            log.Information($"[AutoRezzer] Borrowing phantom job {phantom.NameOf(jobRow)} to raise "
                            + $"(will return to {phantom.NameOf(current)}).");

            _ = phantom.TryChangeJob(jobRow);
            Enter(Phase.Switching);
            return true;
        }

        /// <summary>
        /// One step. <paramref name="stillWantsRaising"/> is asked fresh every tick rather than
        /// captured, because somebody else may raise the body while we are changing job - and
        /// finishing the trip to cast at a corpse already standing up is exactly the sort of thing
        /// that makes an automated plugin look stupid.
        /// </summary>
        public void Tick(Func<bool> stillWantsRaising, Func<bool> tryRaise)
        {
            if (Current == Phase.Idle)
                return;

            long elapsed = Environment.TickCount64 - phaseSince;

            switch (Current)
            {
                case Phase.Switching:
                {
                    if (phantom.CurrentJobRow == targetJob)
                    {
                        Enter(Phase.Raising);
                        return;
                    }

                    if (!stillWantsRaising() || elapsed > SwitchTimeoutMs)
                    {
                        log.Debug("[AutoRezzer] Phantom job change abandoned; returning.");
                        Enter(Phase.Returning);
                        return;
                    }

                    // Re-asked rather than fired once. TryChangeJob throttles itself, so this is a
                    // no-op most ticks, and the change is genuinely sometimes refused the first time.
                    _ = phantom.TryChangeJob(targetJob);
                    return;
                }

                case Phase.Raising:
                {
                    // Somebody changed phantom job underneath us - the manual buttons on the tab can
                    // do exactly this. Their choice wins; stop pretending we are mid-errand.
                    if (phantom.CurrentJobRow != targetJob)
                    {
                        log.Debug("[AutoRezzer] Phantom job changed during a raise; standing down.");
                        Cancel();
                        return;
                    }

                    if (!stillWantsRaising() || elapsed > RaiseTimeoutMs)
                    {
                        Enter(Phase.Returning);
                        return;
                    }

                    // NOT returned from on success. tryRaise true means the action was sent, not
                    // that it resolved - the body has to actually pick up the Raise status first,
                    // and changing job out from under that is how the plugin would cancel its own
                    // raise. The trip home waits for stillWantsRaising to go false.
                    _ = tryRaise();
                    return;
                }

                case Phase.Returning:
                {
                    if (!config.SwapPhantomJobBack || returnToJob < 0)
                    {
                        Finish();
                        return;
                    }

                    if (phantom.CurrentJobRow == returnToJob)
                    {
                        log.Information($"[AutoRezzer] Back on phantom job {phantom.NameOf(returnToJob)}.");
                        Finish();
                        return;
                    }

                    if (elapsed > ReturnTimeoutMs)
                    {
                        log.Warning($"[AutoRezzer] Gave up returning to phantom job "
                                    + $"{phantom.NameOf(returnToJob)}; still on {phantom.CurrentJobName}.");
                        Finish();
                        return;
                    }

                    _ = phantom.TryChangeJob(returnToJob);
                    return;
                }
            }
        }

        /// <summary>
        /// Abandons the errand WITHOUT changing job back.
        ///
        /// For when the player has taken the wheel - a manual switch on the tab, or the plugin being
        /// switched off. Undoing a choice somebody just made by hand would be the rudest thing this
        /// class could do.
        /// </summary>
        public void Cancel() => Finish();

        /// <summary>Gives up on the raise but still goes home, if there is a home to go to.</summary>
        public void Abort()
        {
            if (Busy && returnToJob >= 0 && config.SwapPhantomJobBack)
            {
                Enter(Phase.Returning);
                return;
            }

            Finish();
        }

        private void Finish()
        {
            returnToJob = -1;
            targetJob = -1;
            Enter(Phase.Idle);
        }

        private void Enter(Phase phase)
        {
            Current = phase;
            phaseSince = Environment.TickCount64;
        }
    }
}
