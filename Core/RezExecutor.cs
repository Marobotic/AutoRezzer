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
            if (!HasMpFor(raiseAction))
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
                if (config.UseSwiftcast
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

                // Vercure on self to proc Dualcast when Swiftcast is down.
                if (me.Level >= RezIds.VercureLevel
                    && actionManager->GetActionStatus(ActionType.Action, RezIds.Verraise, body.GameObjectId) == 0)
                {
                    if (actionManager->GetActionStatus(ActionType.Action, RezIds.Vercure, me.GameObjectId) == 0
                        && actionManager->UseAction(ActionType.Action, RezIds.Vercure, me.GameObjectId))
                    {
                        lastActionTick = now;
                        LastWasRaise = false;
                        LastAction = "Vercure (for Dualcast)";
                        log.Debug("[AutoRezzer] Vercure cast to proc Dualcast.");
                        return true;
                    }
                }

                // RDM NEVER hardcasts Verraise. If neither Swiftcast nor Vercure was sent, wait for next tick.
                return false;
            }

            // ── Every other raising job ──
            bool swiftcastUp = HasStatus(me, RezIds.SwiftcastStatus);

            // "Only rez with Swiftcast" implies spending it, whatever the checkbox above says.
            // The two settings off and on respectively is a coherent-looking pair that means "never
            // use Swiftcast, and never raise without it" - which is not a rule, it is a deadlock,
            // and the plugin would stand over the body forever without ever saying why.
            if ((config.UseSwiftcast || SwiftcastIsRequiredNow())
                && !swiftcastUp
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
                LastAction = "Waiting for Swiftcast";
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
        /// Mirrors RezTargeting.HasMpForRaise deliberately rather than calling it: the executor must
        /// be safe on its own, because the job switcher drives it without going through the
        /// targeting decision.
        /// </summary>
        public bool HasMpFor(uint raiseAction)
        {
            if (RezIds.IsOccult(raiseAction))
                return true;

            var me = objectTable.LocalPlayer;
            if (me == null)
                return false;

            int need = config.MinimumMpForRaise;

            if (raiseAction == RezIds.Verraise
                && !HasStatus(me, RezIds.DualcastStatus)
                && !HasStatus(me, RezIds.SwiftcastStatus))
            {
                need += RezIds.VercureMpCost;
            }

            return me.CurrentMp >= need;
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
            LastAction = $"Raised {body.Name}";
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
