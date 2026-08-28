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

            // Swiftcast, if it is worth it and available.
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

            // ── Red Mage's fallback ──
            //
            // Swiftcast was either not wanted, not available, or already spent. RDM does not need it:
            // Dualcast makes the next spell instant, and any hardcast buys Dualcast. So throw a
            // Vercure at ourselves and let the next tick fire an instant Verraise.
            //
            // ONLY WHEN THERE IS NO OTHER WAY IN. Checked after Swiftcast rather than before, because
            // Swiftcast is free and this costs a GCD and a little MP; and skipped entirely if
            // Dualcast is already up, or the Vercure would be the thing that overwrote it.
            if (raiseAction == RezIds.Verraise
                && config.UseVercureForDualcast
                && me.Level >= RezIds.VercureLevel
                && !HasStatus(me, RezIds.DualcastStatus)
                && !HasStatus(me, RezIds.SwiftcastStatus)
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

            // The raise. GetActionStatus is the game's own "may I" - 0 means yes, and anything else
            // is a reason we would only have discovered by being told no.
            uint status = actionManager->GetActionStatus(ActionType.Action, raiseAction, body.GameObjectId);
            if (status != 0)
                return false;

            if (!actionManager->UseAction(ActionType.Action, raiseAction, body.GameObjectId))
                return false;

            lastActionTick = now;
            LastAction = $"Raised {body.Name}";
            log.Information($"[AutoRezzer] Raising {body.Name}.");
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
