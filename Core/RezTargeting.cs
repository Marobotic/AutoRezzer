using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.Framework;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace AutoRezzer.Core
{
    /// <summary>
    /// Decides who, if anybody, should be raised right now.
    ///
    /// ── WHERE THIS CAME FROM ──────────────────────────────────────────────
    /// The filter below is a port of RotationSolverReborn's TargetFilter.GetDeath and
    /// DataCenter.CanRaise (GPLv3, FFXIV-CombatReborn). It is worth saying which parts are theirs,
    /// because most of them are not obvious and every one of them is a bug somebody already had:
    ///
    ///   - a corpse that is MOVING has already accepted a raise and is on its way back up
    ///   - a corpse with the Raise status already has one inbound: someone else got there first
    ///   - IsDead alone is not enough; CurrentHp must be 0 and the object must be targetable
    ///   - line of sight has to be raycast, because the game will refuse the cast otherwise
    ///
    /// What is NOT theirs is the enemy-proximity rule. RotationSolver has no mob-avoidance logic of
    /// any kind - one unrelated config slider in 107,000 lines - so IsBodySafeToRaise below is new.
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    internal sealed class RezTargeting
    {
        private readonly IClientState clientState;
        private readonly IObjectTable objectTable;
        private readonly Configuration config;

        /// <summary>When each body was first seen dead, so the raise delay has something to count
        /// from. Keyed by object id and swept whenever somebody stops being dead.</summary>
        private readonly Dictionary<ulong, long> deadSince = new();

        /// <summary>Line-of-sight answers, briefly. A raycast per body per frame is the one thing
        /// here expensive enough to be worth not repeating; a fifth of a second is far shorter than
        /// anything in this plugin reacts to and long enough to make it free.</summary>
        private readonly Dictionary<ulong, (long ExpiresAt, bool Visible)> losCache = new();
        private const long LosTtlMs = 200;

        public RezTargeting(IClientState clientState, IObjectTable objectTable, Configuration config)
        {
            this.clientState = clientState;
            this.objectTable = objectTable;
            this.config = config;
        }

        /// <summary>Why nothing is being raised, for the config window to show. Empty when the
        /// plugin is simply idle with nobody on the floor.</summary>
        public string LastReason { get; private set; } = string.Empty;

        /// <summary>
        /// Whether this character can raise at all: the right job, at the right level.
        ///
        /// SYNCED LEVEL, not the level on the character sheet. A level 90 Sage synced to 15 for a
        /// levelling roulette still has Egeiro; one synced to 8 does not, and casting into that is
        /// a "you cannot use this action" error every tick.
        /// </summary>
        public bool CanRaise(out uint raiseAction)
        {
            raiseAction = 0;

            var me = objectTable.LocalPlayer;
            if (me == null)
                return false;

            uint job = me.ClassJob.RowId;
            uint action = RezIds.RaiseActionFor(job);
            if (action == 0)
            {
                LastReason = "This job cannot raise.";
                return false;
            }

            int need = RezIds.RaiseLevelFor(job);
            if (me.Level < need)
            {
                LastReason = $"Level {me.Level}; this job raises from {need}.";
                return false;
            }

            raiseAction = action;
            return true;
        }

        /// <summary>
        /// The best body to raise right now, or null.
        ///
        /// Nearest first among everything that survives the filter. Priority by role would be the
        /// more clever answer - RotationSolver sorts healers up - but "clever" here means standing
        /// over one corpse casting at a further one, and nearest is what a person actually does.
        /// </summary>
        public IBattleChara? FindBody()
        {
            var me = objectTable.LocalPlayer;
            if (me == null)
                return null;

            long now = Environment.TickCount64;
            IBattleChara? best = null;
            float bestDistance = float.MaxValue;
            bool sawSomeone = false;

            // Cleared per sweep, or the window keeps reporting the last reason forever - "enemies
            // are standing on the body" would still be on screen minutes after everybody got up.
            LastReason = string.Empty;

            foreach (var obj in objectTable)
            {
                if (obj is not IPlayerCharacter body)
                    continue;

                if (body.GameObjectId == me.GameObjectId)
                    continue;

                if (!IsDeadAndRaisable(body, now))
                    continue;

                float distance = Vector3.Distance(me.Position, body.Position);
                if (distance > 30f)
                    continue;

                // Counted only once in range. A body across the zone is not something the window
                // should be explaining itself about.
                sawSomeone = true;

                if (!HasBeenDeadLongEnough(body, now))
                    continue;

                if (!IsBodySafeToRaise(body))
                    continue;

                if (!CanSee(body))
                    continue;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = body;
                }
            }

            SweepStaleDeaths(now);

            if (best == null && sawSomeone && string.IsNullOrEmpty(LastReason))
                LastReason = "Someone is down, but not safe or not reachable yet.";
            else if (best != null)
                LastReason = string.Empty;

            return best;
        }

        // ══════════════════════════════════════════════════════════
        //  THE FILTER  (ported from RSR's TargetFilter.GetDeath)
        // ══════════════════════════════════════════════════════════

        private bool IsDeadAndRaisable(IPlayerCharacter body, long now)
        {
            if (!body.IsDead || body.CurrentHp != 0)
            {
                deadSince.Remove(body.GameObjectId);
                return false;
            }

            if (!body.IsTargetable)
                return false;

            // Someone already cast on them. This is the check that keeps two rezzers - or this
            // plugin and the party's actual healer - from both spending a cast on one corpse.
            if (HasStatus(body, RezIds.RaisePending))
                return false;

            if (config.SkipBrinkOfDeath && HasStatus(body, RezIds.BrinkOfDeath))
                return false;

            if (!config.RaiseStrangers && !IsPartyOrAlliance(body))
                return false;

            if (!deadSince.ContainsKey(body.GameObjectId))
                deadSince[body.GameObjectId] = now;

            return true;
        }

        private bool HasBeenDeadLongEnough(IPlayerCharacter body, long now)
        {
            if (!deadSince.TryGetValue(body.GameObjectId, out long since))
                return false;

            return now - since >= (long)(config.RaiseDelaySeconds * 1000f);
        }

        // ══════════════════════════════════════════════════════════
        //  THE MOB RULE  (not from RSR - it has none)
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Whether raising this body would drop somebody into a fight.
        ///
        /// A raise puts a player back on their feet with a sliver of health, no buffs and Weakness.
        /// Doing that inside a pack that is still up is how one death becomes two: they stand, the
        /// pack notices, and the person who raised them is next. So a body with anything hostile
        /// standing on it is left where it is until the area around it is clear.
        ///
        /// ENGAGEMENT IS NOT CHECKED, deliberately. "Is this enemy in combat with somebody" reads
        /// well and behaves badly: mid-pull an enemy flickers in and out of combat, and a rule that
        /// samples that on one frame will happily raise into a pack that is three seconds from
        /// resetting onto the new arrival. Proximity is the blunt version and it is the one that
        /// does not get people killed.
        /// </summary>
        private bool IsBodySafeToRaise(IPlayerCharacter body)
        {
            float radius = config.EnemyNearBodyYalms;
            if (radius <= 0f)
                return true;

            foreach (var obj in objectTable)
            {
                if (obj is not IBattleNpc npc)
                    continue;

                if (npc.IsDead || npc.CurrentHp == 0 || !npc.IsTargetable)
                    continue;

                // Cheap test first: most objects in a busy zone are nowhere near, and asking the
                // game about every one of them is the expensive half.
                if (Vector3.Distance(npc.Position, body.Position) > radius)
                    continue;

                if (!IsHostile(npc))
                    continue;

                LastReason = "Holding: enemies are standing on the body.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// Whether the game would let us attack this thing - which is the same question as whether
        /// it is an enemy, asked in the one way that is always right.
        ///
        /// Ported from RotationSolverReborn's IsEnemy. BattleNpcKind looks like the obvious answer
        /// and is not: friendly NPCs, striking dummies and escort targets all enumerate the same way
        /// real enemies do, and treating a quest NPC as a threat would mean never raising anybody
        /// standing near one.
        /// </summary>
        private static unsafe bool IsHostile(IBattleNpc npc)
        {
            try
            {
                return FFXIVClientStructs.FFXIV.Client.Game.ActionManager
                    .CanUseActionOnTarget(RezIds.Blizzard, (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)npc.Address);
            }
            catch (Exception)
            {
                // If the game will not say, assume hostile: the safe failure for a rule whose whole
                // job is deciding when NOT to cast.
                return true;
            }
        }

        // ══════════════════════════════════════════════════════════
        //  LINE OF SIGHT  (ported from RSR's ObjectHelper.CanSee)
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Whether there is clear air between us and the body.
        ///
        /// The game refuses the cast through geometry, so without this the plugin would sit there
        /// re-issuing an action that can never land on somebody behind a wall. Raycast from eye
        /// height to eye height - both offset up two yalms, because a corpse's origin is on the
        /// floor and the floor is exactly the surface the ray would otherwise clip.
        /// </summary>
        private unsafe bool CanSee(IBattleChara body)
        {
            var me = objectTable.LocalPlayer;
            if (me == null)
                return false;

            long now = Environment.TickCount64;
            ulong id = body.GameObjectId;

            if (losCache.TryGetValue(id, out var cached) && now <= cached.ExpiresAt)
                return cached.Visible;

            var eye = me.Position with { Y = me.Position.Y + 2.0f };
            var target = body.Position with { Y = body.Position.Y + 2.0f };

            var offset = target - eye;
            float maxDist = offset.Length();
            if (maxDist < 0.01f)
                return true;

            var direction = offset / maxDist;

            bool visible;
            try
            {
                RaycastHit hit;
                int* materialFilter = stackalloc int[] { 0x4000, 0, 0x4000, 0 };
                var module = Framework.Instance()->BGCollisionModule;
                visible = module == null
                    || !module->RaycastMaterialFilter(&hit, &eye, &direction, maxDist, 1, materialFilter);
            }
            catch (Exception)
            {
                // A convenience check is never worth taking the plugin down. Assume visible and let
                // the game refuse the cast if it disagrees.
                visible = true;
            }

            losCache[id] = (now + LosTtlMs, visible);
            return visible;
        }

        // ══════════════════════════════════════════════════════════

        private static bool HasStatus(IBattleChara chara, uint statusId)
        {
            foreach (var status in chara.StatusList)
            {
                if (status != null && status.StatusId == statusId)
                    return true;
            }

            return false;
        }

        private static bool IsPartyOrAlliance(IPlayerCharacter body)
            => body.StatusFlags.HasFlag(Dalamud.Game.ClientState.Objects.Enums.StatusFlags.PartyMember)
               || body.StatusFlags.HasFlag(Dalamud.Game.ClientState.Objects.Enums.StatusFlags.AllianceMember);

        /// <summary>Drops bodies nobody is tracking any more, so the dictionary cannot grow for a
        /// whole session on a busy zone.</summary>
        private void SweepStaleDeaths(long now)
        {
            if (deadSince.Count < 64 && losCache.Count < 64)
                return;

            var expired = new List<ulong>();
            foreach (var kv in losCache)
            {
                if (now > kv.Value.ExpiresAt + 5000)
                    expired.Add(kv.Key);
            }

            foreach (var id in expired)
                losCache.Remove(id);

            if (deadSince.Count > 256)
                deadSince.Clear();
        }

        public void Reset()
        {
            deadSince.Clear();
            losCache.Clear();
            LastReason = string.Empty;
        }
    }
}
