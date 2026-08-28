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

        public RezExecutor(IObjectTable objectTable, ICondition condition, IPluginLog log, Configuration config)
        {
            this.objectTable = objectTable;
            this.condition = condition;
            this.log = log;
            this.config = config;
        }

        /// <summary>What happened on the last attempt, for the window to show.</summary>
        public string LastAction { get; private set; } = string.Empty;

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

            // ── Red Mage: Dualcast or Swiftcast ONLY, never hardcast ──
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

                    config.RecordCastRaise(me.ClassJob.RowId);
                    lastActionTick = now;
                    LastAction = $"Raised {body.Name}";
                    log.Information($"[AutoRezzer] Raising {body.Name} (Instant Verraise). Total cast: {config.TotalRaisesCast}.");
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
                        LastAction = "Swiftcast";
                        log.Debug("[AutoRezzer] Swiftcast used.");
                        return true;
                    }
                }

                // Vercure on self to proc Dualcast when Swiftcast is down.
                if (config.UseVercureForDualcast
                    && me.Level >= RezIds.VercureLevel
                    && actionManager->GetActionStatus(ActionType.Action, RezIds.Verraise, body.GameObjectId) == 0)
                {
                    if (actionManager->GetActionStatus(ActionType.Action, RezIds.Vercure, me.GameObjectId) == 0
                        && actionManager->UseAction(ActionType.Action, RezIds.Vercure, me.GameObjectId))
                    {
                        lastActionTick = now;
                        LastAction = "Vercure (for Dualcast)";
                        log.Debug("[AutoRezzer] Vercure cast to proc Dualcast.");
                        return true;
                    }
                }

                // RDM NEVER hardcasts Verraise. If neither Swiftcast nor Vercure was sent, wait for next tick.
                return false;
            }

            // ── All other raising jobs: Swiftcast first, fallback to hardcasting ──
            if (config.UseSwiftcast
                && me.Level >= RezIds.SwiftcastLevel
                && !HasStatus(me, RezIds.SwiftcastStatus)
                && actionManager->GetActionStatus(ActionType.Action, RezIds.Swiftcast) == 0)
            {
                if (actionManager->UseAction(ActionType.Action, RezIds.Swiftcast))
                {
                    lastActionTick = now;
                    LastAction = "Swiftcast";
                    log.Debug("[AutoRezzer] Swiftcast used.");
                    return true;
                }
            }

            // The raise (instant if Swiftcast is active, otherwise hardcast).
            uint status = actionManager->GetActionStatus(ActionType.Action, raiseAction, body.GameObjectId);
            if (status != 0)
                return false;

            if (!actionManager->UseAction(ActionType.Action, raiseAction, body.GameObjectId))
                return false;

            config.RecordCastRaise(me.ClassJob.RowId);
            lastActionTick = now;
            LastAction = $"Raised {body.Name}";
            log.Information($"[AutoRezzer] Raising {body.Name}. Total cast: {config.TotalRaisesCast}.");
            return true;
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
