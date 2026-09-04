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
    /// What the last sweep saw, for the window to describe the room with.
    ///
    /// Counted over the same 30 yalms the raise itself works in, so the numbers on screen are the
    /// numbers the plugin is actually reasoning about rather than a zone-wide headcount.
    /// </summary>
    /// <param name="Players">Other players in range, alive or not.</param>
    /// <param name="Dead">How many of them are on the floor.</param>
    /// <param name="Raised">How many of the dead already have a raise inbound or waiting.</param>
    internal readonly record struct Surroundings(int Players, int Dead, int Raised);

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
    /// There used to be an enemy-proximity rule here as well, and it is gone. It shipped disabled,
    /// stayed disabled, and any radius large enough to mean anything declined to raise anybody at
    /// all in the content this plugin is for, where something alive is always nearby.
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    internal sealed class RezTargeting
    {
        private readonly IClientState clientState;
        private readonly IObjectTable objectTable;
        private readonly Configuration config;
        private readonly PhantomJobs phantom;

        /// <summary>When each body was first seen dead, so the raise delay has something to count
        /// from. Keyed by object id and swept whenever somebody stops being dead.</summary>
        private readonly Dictionary<ulong, long> deadSince = new();

        /// <summary>Line-of-sight answers, briefly. A raycast per body per frame is the one thing
        /// here expensive enough to be worth not repeating; a fifth of a second is far shorter than
        /// anything in this plugin reacts to and long enough to make it free.</summary>
        private readonly Dictionary<ulong, (long ExpiresAt, bool Visible)> losCache = new();
        private const long LosTtlMs = 200;

        public RezTargeting(IClientState clientState, IObjectTable objectTable, Configuration config,
            PhantomJobs phantom)
        {
            this.clientState = clientState;
            this.objectTable = objectTable;
            this.config = config;
            this.phantom = phantom;
        }

        /// <summary>
        /// Why nothing is being raised, for the config window to show.
        ///
        /// TWO SOURCES, KEPT APART. "This job cannot raise" comes from CanRaise and "enemies are
        /// standing on the body" comes from the sweep, and the sweep clears its own reason on every
        /// pass. Sharing one field meant the sweep wiped the job reason a fraction of a second after
        /// CanRaise wrote it, so the window flickered between the explanation and nothing at all.
        ///
        /// The third kind - "there is a body, this job could raise it, and the cast still cannot go
        /// out" - is NOT here. It belongs to RezExecutor.HoldReason, because it is the executor's
        /// rules that decide it: MP, Swiftcast, Dualcast. Answering it from two places is how the
        /// MP message and the MP check drifted apart the first time.
        /// </summary>
        public string LastReason => string.IsNullOrEmpty(jobReason) ? holdReason : jobReason;

        private string jobReason = string.Empty;
        private string holdReason = string.Empty;

        /// <summary>What the last sweep saw. Refreshed every tick, whatever the outcome.</summary>
        public Surroundings LastSurroundings { get; private set; }

        /// <summary>Who the last sweep settled on, or empty. Kept as a string rather than the object
        /// so the window can draw it without holding on to a game object between frames.</summary>
        public string LastTargetName { get; private set; } = string.Empty;

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
            {
                jobReason = string.Empty;
                return false;
            }

            // ── Occult Crescent, asked FIRST ──
            //
            // Before the job at all, because the phantom job grants this and your class has nothing
            // to do with it. Asking "can this job raise" first would turn a Warrior holding Occult
            // Raise away at the door.
            if (config.UseOccultRaise && phantom.CanOccultRaise)
            {
                raiseAction = RezIds.OccultRaise;
                jobReason = string.Empty;
                return true;
            }

            uint job = me.ClassJob.RowId;
            uint action = RezIds.RaiseActionFor(job);
            if (action == 0)
            {
                jobReason = "No rez on this job";
                return false;
            }

            int need = RezIds.RaiseLevelFor(job);
            if (me.Level < need)
            {
                jobReason = $"Needs level {need} to rez";
                return false;
            }

            raiseAction = action;
            jobReason = string.Empty;
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
            {
                LastSurroundings = default;
                LastTargetName = string.Empty;
                return null;
            }

            long now = Environment.TickCount64;
            IBattleChara? best = null;
            float bestDistance = float.MaxValue;
            bool sawSomeone = false;

            // The headcount, kept separately from the filter's own answer. The window says "20
            // players around, 2 dead already, 1 rezzed", and every one of those numbers has to be
            // the honest count of what is out there - NOT what survived the filter. A body skipped
            // because it is not in your party is still a body, and reporting it as absent would
            // make the plugin look like it had not noticed.
            int players = 0;
            int dead = 0;
            int raised = 0;

            // Cleared per sweep, or the window keeps reporting the last reason forever - "enemies
            // are standing on the body" would still be on screen minutes after everybody got up.
            holdReason = string.Empty;

            foreach (var obj in objectTable)
            {
                if (obj is not IPlayerCharacter body)
                    continue;

                if (body.GameObjectId == me.GameObjectId)
                    continue;

                // RANGE FIRST, before anything else is asked about this object. It is the cheapest
                // test available and it throws away most of a crowded zone, which matters when the
                // sweep now touches every player rather than only the dead ones.
                float distance = Vector3.Distance(me.Position, body.Position);
                if (distance > 30f)
                    continue;

                players++;

                bool isDown = body.IsDead && body.CurrentHp == 0;
                if (isDown)
                {
                    dead++;
                    if (HasStatus(body, RezIds.RaisePending))
                        raised++;
                }

                if (!IsDeadAndRaisable(body, now))
                    continue;

                // Counted only once in range. A body across the zone is not something the window
                // should be explaining itself about.
                sawSomeone = true;

                if (!HasBeenDeadLongEnough(body, now))
                    continue;

                if (!CanSee(body))
                    continue;

                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = body;
                }
            }

            LastSurroundings = new Surroundings(players, dead, raised);
            LastTargetName = best?.Name.TextValue ?? string.Empty;
            SweepStaleDeaths(now);

            if (best == null && sawSomeone && string.IsNullOrEmpty(holdReason))
                holdReason = "Can't reach them";
            else if (best != null)
                holdReason = string.Empty;

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
            jobReason = string.Empty;
            holdReason = string.Empty;
            LastSurroundings = default;
            LastTargetName = string.Empty;
        }
    }
}
