using System;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoRezzer.Core
{
    /// <summary>
    /// Casts the thing.
    ///
    /// Everything that decides WHETHER lives in RezTargeting; this only knows how. The split is
    /// worth keeping because the two fail in completely different ways - a targeting bug raises the
    /// wrong person, an execution bug spams the server with actions it will refuse.
    /// </summary>
    internal sealed class RezExecutor
    {
        private readonly IObjectTable objectTable;
        private readonly ICondition condition;
        private readonly IPluginLog log;
        private readonly Configuration config;

        /// <summary>Writes the config to disk. Called the moment a raise is counted, because a
        /// statistic that only survives if something else happens to save afterwards is not a
        /// statistic - it is whatever was in memory when the game last closed cleanly.</summary>
        private readonly Action saveConfig;

        /// <summary>
        /// The last moment an action was sent, and the floor on how often that may happen.
        ///
        /// THE SINGLE MOST IMPORTANT NUMBER IN THIS FILE. UseAction returns false for a long list
        /// of ordinary reasons - out of range, still casting, animation lock, target just stood up -
        /// and none of them are fixed by asking again this frame. Without a floor, a body the game
        /// will not accept becomes sixty action requests a second for as long as it lies there,
        /// which is indistinguishable from a bot at the packet level and is the one thing here that
        /// could actually get somebody actioned.
        /// </summary>
        private long lastActionTick;
        private const long ActionFloorMs = 1000;

        public RezExecutor(IObjectTable objectTable, ICondition condition, IPluginLog log,
            Configuration config, Action saveConfig)
        {
            this.objectTable = objectTable;
            this.condition = condition;
            this.log = log;
            this.config = config;
            this.saveConfig = saveConfig;
        }

        /// <summary>What happened on the last attempt, for the window to show.</summary>
        public string LastAction { get; private set; } = string.Empty;

        /// <summary>
        /// Whether the last successful attempt was the raise itself rather than a step towards one.
        ///
        /// TryRaise returns true for Swiftcast and for Vercure as well, because both mean an action
        /// went out and the caller has to back off. The announcement must not: "Swiftcast." followed
        /// by "Vercure." followed by "Raised Someone." is three lines where one was wanted.
        /// </summary>
        public bool LastWasRaise { get; private set; }

        /// <summary>
        /// Whether Swiftcast could be used this instant.
        ///
        /// Asked from outside as well as in - the out-of-combat Red Mage switch turns on exactly
        /// this question, and the answer has to be the game's rather than a guess at the cooldown.
        /// </summary>
        public unsafe bool SwiftcastReady()
        {
            var me = objectTable.LocalPlayer;
            if (me == null || me.Level < RezIds.SwiftcastLevel)
                return false;

            if (HasStatus(me, RezIds.SwiftcastStatus))
                return true;

            var actionManager = ActionManager.Instance();
            return actionManager != null
                   && actionManager->GetActionStatus(ActionType.Action, RezIds.Swiftcast) == 0;
        }

        /// <summary>Why the plugin is standing over a body doing nothing, or empty. Written by
        /// <see cref="CanRaiseThisInstant"/> and read by the window.</summary>
        public string HoldReason { get; private set; } = string.Empty;

        /// <summary>
        /// Seconds left on Swiftcast, rounded up, or 0 when it is ready or unlearnable.
        ///
        /// For the window's one-line answer to "why is it just standing there". "Swiftcast in 34s"
        /// is a fact somebody can act on; "waiting" is a shrug.
        /// </summary>
        public unsafe int SwiftcastSecondsLeft()
        {
            var me = objectTable.LocalPlayer;
            if (me == null || me.Level < RezIds.SwiftcastLevel)
                return 0;

            if (HasStatus(me, RezIds.SwiftcastStatus))
                return 0;

            try
            {
                var actionManager = ActionManager.Instance();
                if (actionManager == null)
                    return 0;

                float total = actionManager->GetRecastTime(ActionType.Action, RezIds.Swiftcast);
                float done = actionManager->GetRecastTimeElapsed(ActionType.Action, RezIds.Swiftcast);
                float left = total - done;

                return left <= 0f ? 0 : (int)Math.Ceiling(left);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>
        /// Whether Thin Air is available right now: held, or castable this instant.
        ///
        /// White Mage only. It is what lets a rez go out on an empty bar, so the MP floor asks about
        /// it before deciding to hold.
        /// </summary>
        public unsafe bool ThinAirAvailable()
        {
            var me = objectTable.LocalPlayer;
            if (me == null || me.ClassJob.RowId != RezIds.WhiteMage || me.Level < RezIds.ThinAirLevel)
                return false;

            if (HasStatus(me, RezIds.ThinAirStatus))
                return true;

            try
            {
                var actionManager = ActionManager.Instance();
                return actionManager != null
                       && actionManager->GetActionStatus(ActionType.Action, RezIds.ThinAir) == 0;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// Puts Lucid Dreaming up when the reason nobody is being rezzed is an empty MP bar.
        ///
        /// ── CALLED FROM THE HOLD, NOT FROM THE CAST ───────────────────────
        /// Lucid is off-GCD. It costs no cast, no global cooldown and no weave slot that mattered,
        /// which is exactly why this can fire while the plugin is standing back with its hands off.
        /// Nothing is paused, nothing is interrupted, and the bar that is the whole problem starts
        /// refilling. Waiting for MP without doing the one thing that produces MP is just waiting.
        ///
        /// Returns whether it went out, which the caller uses for nothing except not spamming it.
        /// </summary>
        public unsafe bool TryRestoreMp()
        {
            long now = Environment.TickCount64;
            if (now - lastLucidTick < LucidFloorMs)
                return false;

            var me = objectTable.LocalPlayer;
            if (me == null || me.IsDead || me.Level < RezIds.LucidDreamingLevel)
                return false;

            // Only when it would actually help. Lucid at full MP is a cooldown spent on nothing.
            if (me.CurrentMp >= config.MinimumMpForRaise)
                return false;

            if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.Unconscious])
                return false;

            try
            {
                var actionManager = ActionManager.Instance();
                if (actionManager == null
                    || actionManager->GetActionStatus(ActionType.Action, RezIds.LucidDreaming) != 0)
                {
                    return false;
                }

                lastLucidTick = now;

                if (!actionManager->UseAction(ActionType.Action, RezIds.LucidDreaming))
                    return false;

                LastAction = "Lucid Dreaming";
                log.Debug("[AutoRezzer] Lucid Dreaming used to pay for a rez.");
                return true;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private long lastLucidTick;
        private const long LucidFloorMs = 3000;

        /// <summary>Forgets the last hold. Called the moment there is nobody to hold FOR - the
        /// reason is only true of a body, and "waiting for Swiftcast" left on screen in an empty
        /// field describes nothing that is happening.</summary>
        public void ClearHold() => HoldReason = string.Empty;

        /// <summary>
        /// ── THE QUESTION THE WHOLE PLUGIN TURNS ON ────────────────────────
        /// "If I stop this player's rotation right now, does a raise actually follow?"
        ///
        /// It has to be answerable BEFORE anything is paused, and that is the entire reason this
        /// method exists rather than being left implicit inside TryRaise. TryRaise decides the same
        /// things - it has to, it is the last line of defence and the job switcher reaches it
        /// without coming past here - but it decides them one step too late: by the time it declines
        /// to cast, Rotation Solver has already been switched off, and it stays off for as long as
        /// the body lies there.
        ///
        /// That was the bug. "Only rez with Swiftcast" plus a Swiftcast on a fifty second cooldown
        /// meant fifty seconds of no rotation and no raise - strictly worse than never having got
        /// involved, because at least a plugin that does nothing leaves you fighting.
        ///
        /// ── WHAT COUNTS AS "NO" ───────────────────────────────────────────
        /// Only the blockers this plugin cannot act its way out of. Being mid-cast is NOT one of
        /// them - pausing the rotation is precisely what ends that - so casting is deliberately
        /// absent from the list below, exactly as it is absent from JobSwitcher.CanSwitchOnceQuiet.
        /// Everything here is a state where the next tick, and the fifty after it, would send
        /// nothing.
        ///
        /// A recast timer is not on the list either. Occult Raise's five seconds is the tail of the
        /// raise before it, not a wait - handing the rotation back and taking it again twice in ten
        /// seconds is worse for everybody than holding through it.
        /// ─────────────────────────────────────────────────────────────────
        /// </summary>
        public unsafe bool CanRaiseThisInstant(uint raiseAction)
        {
            HoldReason = string.Empty;

            var me = objectTable.LocalPlayer;
            if (me == null)
                return false;

            // Said plainly rather than folded into the list below. A corpse holding a rotation
            // hostage is the one hold that would look most like the plugin having crashed.
            if (me.IsDead)
            {
                HoldReason = "You're down";
                return false;
            }

            // States where no action can be sent at all, and no amount of waiting inside this
            // plugin changes that. Casting is NOT here; see the note above.
            if (condition[ConditionFlag.BetweenAreas]
                || condition[ConditionFlag.Occupied]
                || condition[ConditionFlag.Occupied33]
                || condition[ConditionFlag.Occupied38]
                || condition[ConditionFlag.CarryingObject]
                || condition[ConditionFlag.Unconscious])
            {
                HoldReason = "Can't cast right now";
                return false;
            }

            if (condition[ConditionFlag.Mounted])
            {
                HoldReason = "You're mounted";
                return false;
            }

            // Free, instant, and granted by the phantom job rather than the class. Nothing below
            // this line applies to it - there is no MP to be short of and no cast to shorten.
            if (RezIds.IsOccult(raiseAction))
                return true;

            // Thin Air makes the next spell free, so a White Mage holding one is never short of MP
            // for a rez however empty the bar is.
            if (!HasMpFor(raiseAction) && !ThinAirAvailable())
            {
                HoldReason = "Not enough MP";
                return false;
            }

            var actionManager = ActionManager.Instance();
            if (actionManager == null)
                return false;

            bool swiftcastUp = HasStatus(me, RezIds.SwiftcastStatus);
            bool swiftcastCastable = me.Level >= RezIds.SwiftcastLevel
                                     && actionManager->GetActionStatus(ActionType.Action, RezIds.Swiftcast) == 0;

            // ── Red Mage ──
            //
            // Never hardcasts, so its three routes to an instant Verraise are the whole of the
            // question: already holding the proc, able to buy it with Swiftcast, or able to buy it
            // with Vercure. None of the three and there is nothing to interrupt anybody for.
            if (raiseAction == RezIds.Verraise)
            {
                if (swiftcastUp || HasStatus(me, RezIds.DualcastStatus))
                    return true;

                if (swiftcastCastable)
                    return true;

                if (me.Level >= RezIds.VercureLevel
                    && actionManager->GetActionStatus(ActionType.Action, RezIds.Vercure, me.GameObjectId) == 0)
                {
                    return true;
                }

                HoldReason = "Waiting on Dualcast";
                return false;
            }

            // ── Every other raising job ──
            if (swiftcastUp)
                return true;

            // The hardcast is allowed, so the raise goes out whether Swiftcast is up or not.
            if (!SwiftcastIsRequiredNow())
                return true;

            // From here the raise is Swiftcast or nothing, which makes Swiftcast's cooldown the
            // plugin's cooldown. Note this is asked even when "use Swiftcast" is off: the pair of
            // settings means "never use Swiftcast, never raise without it", and the only honest
            // reading of that is to spend it.
            if (swiftcastCastable)
                return true;

            HoldReason = me.Level < RezIds.SwiftcastLevel
                ? $"Swiftcast isn't learned until level {RezIds.SwiftcastLevel}"
                : "Waiting on Swiftcast";

            return false;
        }

        /// <summary>
        /// Tries to raise this body. Returns true when an action was actually sent.
        ///
        /// Swiftcast first when it is up and the raise is a hardcast: an eight second cast standing
        /// still is the part of raising that gets interrupted, and spending a 60s cooldown to skip
        /// it is what any healer does by hand. The raise itself waits for the next tick rather than
        /// being queued behind it in the same frame - the status has to actually land first, and
        /// the game is the only thing that can say when.
        /// </summary>
        public unsafe bool TryRaise(IBattleChara body, uint raiseAction)
        {
            long now = Environment.TickCount64;
            if (now - lastActionTick < ActionFloorMs)
                return false;

            var me = objectTable.LocalPlayer;
            if (me == null)
                return false;

            // Anything that makes casting impossible. Asked before the action rather than relying on
            // UseAction to refuse, because a refusal still costs a request.
            if (condition[ConditionFlag.BetweenAreas]
                || condition[ConditionFlag.Casting]
                || condition[ConditionFlag.Occupied]
                || condition[ConditionFlag.Occupied33]
                || condition[ConditionFlag.Occupied38]
                || condition[ConditionFlag.CarryingObject]
                || condition[ConditionFlag.Mounted]
                || condition[ConditionFlag.Unconscious])
            {
                return false;
            }

            var actionManager = ActionManager.Instance();
            if (actionManager == null)
                return false;

            // ── Occult Crescent: the short path, and it returns ──
            //
            // Instant, free, five second recast. Everything below this line is machinery for
            // avoiding a cast time or an MP bill, and Occult Raise has neither, so none of it is
            // consulted - no Swiftcast, no MP floor, no job. The recast is the only limit, and
            // GetActionStatus is what enforces it.
            if (raiseAction == RezIds.OccultRaise)
            {
                if (actionManager->GetActionStatus(ActionType.Action, RezIds.OccultRaise, body.GameObjectId) != 0)
                    return false;

                if (!actionManager->UseAction(ActionType.Action, RezIds.OccultRaise, body.GameObjectId))
                    return false;

                RecordRaise(me.ClassJob.RowId, body, now, "Occult Raise");
                return true;
            }

            // ── The MP floor ──
            //
            // A second gate, after RezTargeting has already asked the same question before deciding
            // to interrupt anything. Duplicated on purpose: that one decides whether to get
            // involved, this one is the guarantee that no cast ever goes out under the floor, on
            // any path - including the job-switch state machine, which reaches here without passing
            // through the decision above.
            if (!HasMpFor(raiseAction) && !ThinAirAvailable())
            {
                LastWasRaise = false;
                return false;
            }

            // ── Red Mage: Dualcast or Swiftcast ONLY, never hardcast ──
            //
            // The Vercure route is not optional and never was, in the sense that mattered: the
            // alternative to it is a ten second cast Red Mage is forbidden from starting, so a
            // Red Mage with the toggle off simply stood there. It is the job's raise now.
            if (raiseAction == RezIds.Verraise)
            {
                bool hasInstantBuff = HasStatus(me, RezIds.DualcastStatus) || HasStatus(me, RezIds.SwiftcastStatus);

                if (hasInstantBuff)
                {
                    // Instant Verraise via Dualcast or Swiftcast.
                    uint raiseStatus = actionManager->GetActionStatus(ActionType.Action, RezIds.Verraise, body.GameObjectId);
                    if (raiseStatus != 0)
                        return false;

                    if (!actionManager->UseAction(ActionType.Action, RezIds.Verraise, body.GameObjectId))
                        return false;

                    RecordRaise(me.ClassJob.RowId, body, now, "Instant Verraise");
                    return true;
                }

                // Swiftcast if available.
                if (me.Level >= RezIds.SwiftcastLevel
                    && actionManager->GetActionStatus(ActionType.Action, RezIds.Swiftcast) == 0)
                {
                    if (actionManager->UseAction(ActionType.Action, RezIds.Swiftcast))
                    {
                        lastActionTick = now;
                        LastWasRaise = false;
                        LastAction = "Swiftcast";
                        log.Debug("[AutoRezzer] Swiftcast used.");
                        return true;
                    }
                }

                // Vercure on self to proc Dualcast when Swiftcast is down.
                if (me.Level >= RezIds.VercureLevel
                    && actionManager->GetActionStatus(ActionType.Action, RezIds.Verraise, body.GameObjectId) == 0)
                {
                    if (actionManager->GetActionStatus(ActionType.Action, RezIds.Vercure, me.GameObjectId) == 0
                        && actionManager->UseAction(ActionType.Action, RezIds.Vercure, me.GameObjectId))
                    {
                        lastActionTick = now;
                        LastWasRaise = false;
                        LastAction = "Vercure, for Dualcast";
                        log.Debug("[AutoRezzer] Vercure cast to proc Dualcast.");
                        return true;
                    }
                }

                // RDM NEVER hardcasts Verraise. If neither Swiftcast nor Vercure was sent, wait for next tick.
                return false;
            }

            // ── Every other raising job ──
            bool swiftcastUp = HasStatus(me, RezIds.SwiftcastStatus);

            // ── THIN AIR GOES FIRST, AND ONLY WHEN IT IS PAYING FOR SOMETHING ──
            //
            // It is off-GCD, so it costs nothing to weave ahead of the rez, and the buff lasts long
            // enough to survive Swiftcast going out in between. Thin Air, Swiftcast, Raise is an
            // instant free rez; Thin Air then a hardcast is a free slow one.
            //
            // Spent ONLY when the bar cannot cover the rez otherwise. Burning a charge on a rez that
            // was affordable anyway is a charge not there for the one that is not.
            if (me.ClassJob.RowId == RezIds.WhiteMage
                && !HasMpFor(raiseAction)
                && !HasStatus(me, RezIds.ThinAirStatus)
                && me.Level >= RezIds.ThinAirLevel
                && actionManager->GetActionStatus(ActionType.Action, RezIds.ThinAir) == 0
                && actionManager->UseAction(ActionType.Action, RezIds.ThinAir))
            {
                lastActionTick = now;
                LastWasRaise = false;
                LastAction = "Thin Air";
                log.Debug("[AutoRezzer] Thin Air used to pay for the rez.");
                return true;
            }

            // "Only rez with Swiftcast" implies spending it, whatever the checkbox above says.
            // The two settings off and on respectively is a coherent-looking pair that means "never
            // use Swiftcast, and never raise without it" - which is not a rule, it is a deadlock,
            // and the plugin would stand over the body forever without ever saying why.
            // MANDATORY, not a setting. There was never a reason to hold Swiftcast through an
            // eight second cast that anything can interrupt, and the old toggle paired with
            // "never hardcast" was a deadlock rather than a configuration: never spend it, never
            // raise without it.
            if (!swiftcastUp
                && me.Level >= RezIds.SwiftcastLevel
                && actionManager->GetActionStatus(ActionType.Action, RezIds.Swiftcast) == 0)
            {
                if (actionManager->UseAction(ActionType.Action, RezIds.Swiftcast))
                {
                    lastActionTick = now;
                    LastWasRaise = false;
                    LastAction = "Swiftcast";
                    log.Debug("[AutoRezzer] Swiftcast used.");
                    return true;
                }
            }

            // ── The hardcast gate ──
            //
            // Reached only when Swiftcast is not up and could not be used, so the raise from here on
            // is an eight second cast standing still. Whether that is acceptable is the whole of the
            // "only rez with Swiftcast" setting: in combat it is how the caster becomes the next
            // body, out of combat it is merely slow. Red Mage never gets here - it returned above.
            if (!swiftcastUp && SwiftcastIsRequiredNow())
            {
                LastWasRaise = false;
                LastAction = "Waiting on Swiftcast";
                return false;
            }

            // The raise (instant if Swiftcast is active, otherwise hardcast).
            uint status = actionManager->GetActionStatus(ActionType.Action, raiseAction, body.GameObjectId);
            if (status != 0)
                return false;

            if (!actionManager->UseAction(ActionType.Action, raiseAction, body.GameObjectId))
                return false;

            RecordRaise(me.ClassJob.RowId, body, now, swiftcastUp ? "Swiftcast" : "hardcast");
            return true;
        }

        /// <summary>
        /// Whether there is MP for this raise, including Vercure's share when Red Mage needs it.
        ///
        /// A second gate behind CanRaiseThisInstant rather than a replacement for it: the job
        /// switcher drives TryRaise without coming past the decision, so the executor has to be safe
        /// on its own.
        /// </summary>
        public bool HasMpFor(uint raiseAction)
        {
            if (RezIds.IsOccult(raiseAction))
                return true;

            var me = objectTable.LocalPlayer;
            return me != null && me.CurrentMp >= MpNeededFor(raiseAction);
        }

        /// <summary>
        /// What this raise actually costs from here, floor included.
        ///
        /// Split out so the number in the window's "needs 2,900" is the same number the check used,
        /// rather than a second piece of arithmetic that agrees with it until one of them is edited.
        /// </summary>
        public int MpNeededFor(uint raiseAction)
        {
            int need = config.MinimumMpForRaise;

            var me = objectTable.LocalPlayer;
            if (me != null
                && raiseAction == RezIds.Verraise
                && !HasStatus(me, RezIds.DualcastStatus)
                && !HasStatus(me, RezIds.SwiftcastStatus))
            {
                need += RezIds.VercureMpCost;
            }

            return need;
        }

        /// <summary>
        /// Whether a hardcast raise is forbidden right this second.
        ///
        /// Two settings, and the second one narrows the first rather than repeating it: "only with
        /// Swiftcast" is the rule, "in combat only" is where it applies. Off and off means the old
        /// behaviour - hardcast whenever Swiftcast is unavailable.
        /// </summary>
        public bool SwiftcastIsRequiredNow()
        {
            if (!config.SwiftcastOnly)
                return false;

            return !config.SwiftcastOnlyInCombat || condition[ConditionFlag.InCombat];
        }

        /// <summary>Counts a landed raise and gets it onto disk before anything else can go wrong.</summary>
        private void RecordRaise(uint job, IBattleChara body, long now, string how)
        {
            config.RecordCastRaise(job);
            saveConfig();

            lastActionTick = now;
            LastWasRaise = true;
            LastAction = $"Rezzed {body.Name}";
            log.Information($"[AutoRezzer] Raising {body.Name} ({how}). Total cast: {config.TotalRaisesCast}.");
        }

        private static bool HasStatus(IBattleChara chara, uint statusId)
        {
            foreach (var status in chara.StatusList)
            {
                if (status != null && status.StatusId == statusId)
                    return true;
            }

            return false;
        }
    }
}
