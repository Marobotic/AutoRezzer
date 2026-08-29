using System;
using System.Collections.Generic;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.InstanceContent;
using Lumina.Excel.Sheets;

namespace AutoRezzer.Core
{
    /// <summary>
    /// Whether we are in Occult Crescent, and if so which phantom job is equipped and at what level.
    ///
    /// ── WHY NOT A TERRITORY ID ────────────────────────────────────────────
    /// The obvious way to ask "am I in Occult Crescent" is to compare the territory against a list,
    /// and it is the wrong one twice over: the list grows every time they add a zone (South Horn
    /// shipped alone, North Horn followed), and being in the territory is not the same as the
    /// content director being up - which is what actually decides whether phantom actions exist.
    ///
    /// So the question is asked of the director itself. PublicContentOccultCrescent.GetInstance()
    /// returns null anywhere else in the game, which makes "is this null" both the zone check and
    /// the readiness check in one, and makes it correct in zones that do not exist yet.
    ///
    /// ── AND WHY THE LEVEL COMES FROM AN ARRAY INDEXED BY SHEET ROW ────────
    /// OccultCrescentState carries a flat array of 24 support-job levels. It is indexed by the
    /// MKDSupportJob row id, NOT by the sheet's own JobIndex column - the two disagree (Phantom
    /// White Mage is row 17 but JobIndex 10) and using the wrong one silently reads somebody else's
    /// level. Row id is what CurrentSupportJob holds, and what BOCCHI indexes with.
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    internal sealed class PhantomJobs
    {
        private readonly IDataManager dataManager;
        private readonly ICondition condition;
        private readonly IPluginLog log;

        /// <summary>Names, once each. The config window asks for one every frame it is open, and
        /// ExtractText allocates a string each time it is called.</summary>
        private readonly Dictionary<int, string> nameCache = new();

        /// <summary>
        /// How long after combat ends before a support job change is believed to take.
        ///
        /// Not invented here - it is BOCCHI's number, from its own PhantomJobChangeGate, and it is
        /// there because the game keeps refusing the change for a moment after the combat flag has
        /// already cleared. Without the settle the call silently no-ops and the plugin thinks it
        /// changed job.
        /// </summary>
        private const long PostCombatSettleMs = 4000;

        /// <summary>Floor between change attempts. The call is fire-and-forget with a read-back, so
        /// without this a refused change is retried every tick.</summary>
        private const long ChangeFloorMs = 750;

        private bool wasInCombat;
        private long combatClearedAt;
        private long lastChangeAttempt;

        public PhantomJobs(IDataManager dataManager, ICondition condition, IPluginLog log)
        {
            this.dataManager = dataManager;
            this.condition = condition;
            this.log = log;
        }

        /// <summary>Whether the Occult Crescent content director is up and its state has loaded.</summary>
        public unsafe bool InOccultCrescent
        {
            get
            {
                try
                {
                    var instance = PublicContentOccultCrescent.GetInstance();
                    return instance != null
                           && instance->StateLoaded
                           && PublicContentOccultCrescent.GetState() != null;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>The equipped phantom job's MKDSupportJob row, or -1 for none/not in the zone.</summary>
        public unsafe int CurrentJobRow
        {
            get
            {
                try
                {
                    var state = PublicContentOccultCrescent.GetState();
                    return state == null ? -1 : state->CurrentSupportJob;
                }
                catch (Exception)
                {
                    return -1;
                }
            }
        }

        /// <summary>The level of a phantom job by its sheet row, or 0.</summary>
        public unsafe int LevelOf(int jobRow)
        {
            if (jobRow < 0)
                return 0;

            try
            {
                var state = PublicContentOccultCrescent.GetState();
                if (state == null)
                    return 0;

                // Bounds-checked against the array rather than against 24: the array grows when they
                // add phantom jobs, and a hardcoded length is a buffer overrun waiting for a patch.
                var levels = state->SupportJobLevels;
                return jobRow >= levels.Length ? 0 : levels[jobRow];
            }
            catch (Exception)
            {
                return 0;
            }
        }

        /// <summary>The equipped phantom job's level, or 0.</summary>
        public int CurrentJobLevel => LevelOf(CurrentJobRow);

        /// <summary>
        /// Whether a given support job is levelled far enough to raise with.
        ///
        /// Answered for jobs you are NOT on as well as the one you are, because that is the question
        /// the swap and the tab's buttons both turn on.
        /// </summary>
        public bool CanRaiseAs(int jobRow)
        {
            int need = RezIds.PhantomRaiseLevelFor(jobRow);
            return need > 0 && InOccultCrescent && LevelOf(jobRow) >= need;
        }

        /// <summary>
        /// Whether a phantom raise is available on the job currently equipped.
        ///
        /// Recomputed every tick and never cached: people change phantom job at a knowledge crystal
        /// mid-run, and a cached "yes" would have the plugin aiming an action it no longer owns at
        /// every body it saw.
        /// </summary>
        public bool CanOccultRaise => CanRaiseAs(CurrentJobRow);

        /// <summary>The phantom raise available right now, or 0.</summary>
        public uint CurrentRaiseAction
            => CanOccultRaise ? RezIds.PhantomRaiseActionFor(CurrentJobRow) : 0u;

        /// <summary>Whether any support job on this character could raise, equipped or not.</summary>
        public bool AnyRaiserAvailable
        {
            get
            {
                foreach (byte row in RezIds.PhantomRaisers)
                {
                    if (CanRaiseAs(row))
                        return true;
                }

                return false;
            }
        }

        /// <summary>
        /// Which support job to raise as, honouring the preference and falling back when it is not
        /// levelled. Returns -1 when neither can.
        ///
        /// STAYING PUT BEATS SWAPPING. Automatic returns whatever is already equipped if it can
        /// raise, so a character with both levelled never changes job for a distinction that does
        /// not exist - the two raises are identical.
        /// </summary>
        public int PickRaiser(PhantomRaiser preference)
        {
            int current = CurrentJobRow;

            switch (preference)
            {
                case PhantomRaiser.WhiteMage:
                    if (CanRaiseAs(RezIds.PhantomWhiteMage))
                        return RezIds.PhantomWhiteMage;
                    break;

                case PhantomRaiser.Chemist:
                    if (CanRaiseAs(RezIds.PhantomChemist))
                        return RezIds.PhantomChemist;
                    break;

                default:
                    if (CanRaiseAs(current))
                        return current;
                    break;
            }

            // The preferred one is not levelled, or Automatic is on a job that cannot raise. Take
            // whichever can - declining to raise because the preference was unavailable would be
            // honouring a tie-break as though it were a requirement.
            foreach (byte row in RezIds.PhantomRaisers)
            {
                if (CanRaiseAs(row))
                    return row;
            }

            return -1;
        }

        // ══════════════════════════════════════════════════════════
        //  CHANGING SUPPORT JOB
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Whether the game would accept a support job change this instant.
        ///
        /// Ported from BOCCHI's PhantomJobChangeGate, including the four second post-combat settle,
        /// which is the non-obvious half: the change is refused for a moment after ConditionFlag
        /// .InCombat has already gone false, and ChangeSupportJob reports nothing when it is refused.
        /// </summary>
        public bool CanChangeJobNow()
        {
            bool inCombat = condition[ConditionFlag.InCombat];
            if (wasInCombat && !inCombat)
                combatClearedAt = Environment.TickCount64;

            wasInCombat = inCombat;

            if (!InOccultCrescent || inCombat)
                return false;

            if (Environment.TickCount64 - combatClearedAt < PostCombatSettleMs)
                return false;

            return !condition[ConditionFlag.BetweenAreas]
                   && !condition[ConditionFlag.BetweenAreas51]
                   && !condition[ConditionFlag.Casting]
                   && !condition[ConditionFlag.Casting87]
                   && !condition[ConditionFlag.Jumping]
                   && !condition[ConditionFlag.Jumping61]
                   && !condition[ConditionFlag.Occupied]
                   && !condition[ConditionFlag.OccupiedInEvent]
                   && !condition[ConditionFlag.OccupiedInQuestEvent]
                   && !condition[ConditionFlag.OccupiedInCutSceneEvent]
                   && !condition[ConditionFlag.Unconscious];
        }

        /// <summary>
        /// Asks the game to change support job. Returns whether it had taken by the time we looked.
        ///
        /// The call itself reports nothing useful, so the answer is read back out of
        /// CurrentSupportJob - the same field everything else here trusts. A false return is
        /// ordinary rather than an error: the change often lands a frame or two later, and the
        /// callers all retry.
        /// </summary>
        public unsafe bool TryChangeJob(int jobRow)
        {
            if (jobRow < 0 || !CanChangeJobNow())
                return false;

            if (CurrentJobRow == jobRow)
                return true;

            long now = Environment.TickCount64;
            if (now - lastChangeAttempt < ChangeFloorMs)
                return false;

            lastChangeAttempt = now;

            try
            {
                PublicContentOccultCrescent.ChangeSupportJob((byte)jobRow);
                log.Information($"[AutoRezzer] Asked for phantom job {jobRow} ({NameOf(jobRow)}).");
            }
            catch (Exception ex)
            {
                log.Warning($"[AutoRezzer] Could not change phantom job: {ex.Message}");
                return false;
            }

            return CurrentJobRow == jobRow;
        }

        /// <summary>The equipped phantom job's name, for the window. Empty outside the zone.</summary>
        public string CurrentJobName => NameOf(CurrentJobRow);

        /// <summary>
        /// A phantom job's name from the game's own sheet.
        ///
        /// Read rather than listed, because the roster is not stable - Mystic Knight, Gladiator,
        /// Dancer, Ninja, White Mage, Black Mage, Dragoon, Summoner, Blue Mage, Red Mage and
        /// Necromancer were all added after the first one - and a hardcoded table would go stale
        /// while looking perfectly correct.
        /// </summary>
        public string NameOf(int jobRow)
        {
            if (jobRow < 0)
                return string.Empty;

            if (nameCache.TryGetValue(jobRow, out string? cached))
                return cached;

            try
            {
                var sheet = dataManager.GetExcelSheet<MKDSupportJob>();
                if (sheet != null && sheet.TryGetRow((uint)jobRow, out var row))
                {
                    string name = row.Name.ExtractText();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        nameCache[jobRow] = name;
                        return name;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[AutoRezzer] Could not read phantom job {jobRow}: {ex.Message}");
            }

            string fallback = $"Phantom job {jobRow}";
            nameCache[jobRow] = fallback;
            return fallback;
        }
    }
}
