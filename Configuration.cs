using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace AutoRezzer
{
    /// <summary>
    /// Everything the plugin remembers between sessions.
    ///
    /// Deliberately small. The whole feature is one switch; the rest of these exist because the two
    /// numbers that decide whether a raise is a help or a wipe - how long to wait, and how close an
    /// enemy has to be before a body is left alone - are the two things people will actually want to
    /// disagree about.
    /// </summary>
    [Serializable]
    public class Configuration : IPluginConfiguration
    {
        public int Version { get; set; } = 1;

        /// <summary>
        /// The switch. Off on a fresh install, and that is not a formality: a plugin that starts
        /// casting on its own the moment it is enabled is one that surprises somebody in the middle
        /// of a fight they were concentrating on.
        /// </summary>
        public bool Enabled { get; set; } = false;

        /// <summary>
        /// How long a body has to have been on the floor before it is raised, in seconds.
        ///
        /// NOT A THROTTLE - a courtesy. Someone who died two frames ago is often still mid-mechanic,
        /// or about to be raised by the healer whose job it is, and a raise landing instantly means
        /// they stand up inside whatever killed them. It also lets a human beat the plugin to it,
        /// which is the polite default in a party that has its own healer.
        /// </summary>
        public float RaiseDelaySeconds { get; set; } = 2.5f;

        /// <summary>
        /// How close a hostile has to be to a body before that body is left where it is, in yalms.
        ///
        /// This is the "avoiding mobs" rule. Raising someone standing inside an unengaged pack is
        /// how a quiet corpse-run becomes a second wipe: they get up, they have aggro, and now two
        /// people are dead instead of one. Ten yalms is roughly a melee pack's footprint.
        /// </summary>
        public float EnemyNearBodyYalms { get; set; } = 10f;

        /// <summary>Whether to spend Swiftcast on the raise when it is off cooldown.</summary>
        public bool UseSwiftcast { get; set; } = true;

        /// <summary>
        /// As Red Mage, whether to hardcast a throwaway Vercure to proc Dualcast when Swiftcast is
        /// down, so Verraise still goes out instantly. Never hardcasts Verraise.
        /// </summary>
        public bool UseVercureForDualcast { get; set; } = true;

        /// <summary>
        /// Whether to pause Rotation Solver Reborn while raising a player so AutoRezzer gets priority.
        /// </summary>
        public bool PauseRotationSolver { get; set; } = true;

        /// <summary>
        /// Accept a raise cast on you, automatically, when you are the one on the floor.
        ///
        /// SEPARATE FROM Enabled, and separate from being able to raise at all: any job can be
        /// raised, so this is useful on a Warrior who will never cast anything. It is also the one
        /// thing here that acts while you are dead, which is exactly when you are least able to
        /// react to it yourself.
        /// </summary>
        public bool AcceptRaise { get; set; } = true;

        /// <summary>
        /// The gearset to switch to in order to raise somebody, or -1 for "never switch".
        ///
        /// A GEARSET, NOT A JOB, and that is not a detail. The game has no "become a White Mage"
        /// verb - it has "equip this gearset", and the job is a consequence. Storing a job would
        /// mean guessing which of your three White Mage sets was meant every time, and guessing
        /// wrong means putting somebody in their glamour set in the middle of a raid.
        ///
        /// -1 by default: switching jobs on somebody's behalf is a much larger thing to do than
        /// casting a spell they already had, and it should be a decision rather than a surprise.
        /// </summary>
        public int RezGearsetId { get; set; } = -1;

        /// <summary>
        /// Whether to go back to the job you were on once the raise has landed.
        ///
        /// On, and it is most of what makes the switching bearable. Being quietly left as a Sage
        /// twenty minutes after a corpse-run is how a convenience becomes a nuisance.
        /// </summary>
        public bool SwitchBackAfter { get; set; } = true;

        /// <summary>
        /// Whether to raise people who are not in your party or alliance.
        ///
        /// On, because "any dead player around" is the thing this was built for - the open-world
        /// case where somebody is face-down after a FATE and nobody else is going to bother.
        /// </summary>
        public bool RaiseStrangers { get; set; } = true;

        /// <summary>Whether to skip people already carrying Brink of Death. Off: a second raise on
        /// somebody who just got up is usually a waste, but it is occasionally the right call.</summary>
        public bool SkipBrinkOfDeath { get; set; } = true;

        /// <summary>Say in chat what was raised and why one was skipped. Off by default - this is a
        /// background thing, and a line per body turns a wipe recovery into a wall of text.</summary>
        public bool Chatty { get; set; } = false;

        // ── Local Statistics ──

        /// <summary>Total number of raises accepted for the local player.</summary>
        public int AcceptedRaisesCount { get; set; } = 0;

        /// <summary>Total number of raises cast by the plugin.</summary>
        public int TotalRaisesCast { get; set; } = 0;

        /// <summary>Raises cast broken down by Job/Class ID.</summary>
        public Dictionary<uint, int> RaisesCastByJob { get; set; } = new();

        public void RecordAcceptedRaise()
        {
            AcceptedRaisesCount++;
        }

        public void RecordCastRaise(uint jobId)
        {
            TotalRaisesCast++;
            if (!RaisesCastByJob.ContainsKey(jobId))
                RaisesCastByJob[jobId] = 0;
            RaisesCastByJob[jobId]++;
        }

        public void ResetStats()
        {
            AcceptedRaisesCount = 0;
            TotalRaisesCast = 0;
            RaisesCastByJob.Clear();
        }
    }
}
