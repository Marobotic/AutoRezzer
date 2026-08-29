using System;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AutoRezzer.Core
{
    /// <summary>
    /// Gets Rotation Solver Reborn out of the way for the length of a raise, and puts it back.
    ///
    /// ── WHY THIS IS NOT "TURN IT OFF, TURN IT ON" ─────────────────────────
    /// RSR has more than one "on". Off, Auto, TargetOnly, Manual, AutoDuty, Henched and PvP are all
    /// distinct states, and several of them exist precisely because another plugin is driving. The
    /// one that matters here is Henched - rotation only, no targeting - because that is what
    /// BOCCHI's Illegal Mode puts RSR into while it runs FATEs and critical encounters.
    ///
    /// So the naive pause/resume is actively harmful in that setup: sending Auto afterwards hands
    /// RSR back its own targeting in the middle of somebody else's automation, and BOCCHI will not
    /// notice, because it only re-sends a mode when its own desired mode changes. It thinks RSR is
    /// still Henched. It is not, and now two things are choosing targets.
    ///
    /// ── AND WHY THE ANSWER IS A SETTING RATHER THAN A READ ────────────────
    /// RSR's IPC exposes one read - AutorotationActive(), a bool - and nothing that says which mode.
    /// BOCCHI exposes no IPC at all. There is genuinely nothing to query, so the mode to come back
    /// to is decided in advance: AutoDetect looks for BOCCHI among the installed plugins and picks
    /// Henched when it is there, Auto when it is not.
    ///
    /// NOTHING HERE EVER TOUCHES BOCCHI. Illegal Mode stays on; only RSR is moved, and only for as
    /// long as a body is being worked on.
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    internal sealed class RsrManager : IDisposable
    {
        /// <summary>RSR's StateCommandType, by value. Its IPC takes the raw byte.</summary>
        private const byte RsrOff = 0;
        private const byte RsrAuto = 1;
        private const byte RsrTargetOnly = 2;
        private const byte RsrManual = 3;
        private const byte RsrHenched = 5;

        private const string BocchiInternalName = "BOCCHI";

        private readonly IDalamudPluginInterface pluginInterface;
        private readonly IPluginLog log;
        private readonly Configuration config;

        /// <summary>Whether RSR was actually running when we paused it. Nothing is restored if it
        /// was already off - coming back from a raise is not an excuse to switch somebody's
        /// autorotation on for them.</summary>
        private bool wasRsrActive;
        private bool isPaused;

        public RsrManager(IDalamudPluginInterface pluginInterface, IPluginLog log, Configuration config)
        {
            this.pluginInterface = pluginInterface;
            this.log = log;
            this.config = config;
        }

        public bool IsPaused => isPaused;

        /// <summary>Whether RSR is loaded at all, which is what the config window's warning asks.</summary>
        public bool IsRsrInstalled => IsPluginLoaded("RotationSolver") || IsPluginLoaded("RotationSolverReborn");

        /// <summary>Whether BOCCHI is loaded. Not whether Illegal Mode is on - BOCCHI publishes no
        /// IPC, so that is unknowable from here; presence is the closest available signal.</summary>
        public bool IsBocchiInstalled => IsPluginLoaded(BocchiInternalName);

        /// <summary>The mode RSR will be put back into, resolved for display as well as for use.</summary>
        public string ResumeModeName => ResolveResumeMode() switch
        {
            RsrHenched => "Henched",
            RsrManual => "Manual",
            RsrTargetOnly => "Target Only",
            _ => "Auto",
        };

        /// <summary>Whether RSR's autorotation is running right now.</summary>
        public bool IsRsrActive()
        {
            try
            {
                var subscriber = pluginInterface.GetIpcSubscriber<bool>("RotationSolverReborn.AutorotationActive");
                return subscriber.InvokeFunc();
            }
            catch
            {
                // No IPC means no RSR, or an RSR too old to ask. Either way there is nothing running
                // that we could be interrupting - and answering "yes" here would have the plugin
                // send a resume to nobody and log a failure every time somebody died.
                return false;
            }
        }

        /// <summary>Stops RSR for the duration of a raise. Idempotent; safe to call every tick.</summary>
        public void Pause()
        {
            if (!config.PauseRotationSolver || isPaused)
                return;

            if (!IsRsrActive())
                return;

            wasRsrActive = true;
            isPaused = true;

            if (ChangeMode(RsrOff))
                log.Information("[AutoRezzer] Paused Rotation Solver Reborn for raising.");
        }

        /// <summary>Puts RSR back the way this plugin's settings say it was. Idempotent.</summary>
        public void Resume()
        {
            if (!isPaused)
                return;

            isPaused = false;

            if (!wasRsrActive)
                return;

            wasRsrActive = false;

            byte mode = ResolveResumeMode();
            if (ChangeMode(mode))
                log.Information($"[AutoRezzer] Resumed Rotation Solver Reborn ({ResumeModeName}).");
        }

        /// <summary>
        /// Which mode to come back to.
        ///
        /// AutoDetect is the default and picks Henched when BOCCHI is loaded, because that is the
        /// mode BOCCHI drives RSR with and coming back to anything else quietly takes the wheel off
        /// it. With no BOCCHI, Auto - the mode somebody running RSR by hand is nearly always in.
        /// </summary>
        private byte ResolveResumeMode() => config.RsrResume switch
        {
            RsrResumeMode.Auto => RsrAuto,
            RsrResumeMode.Manual => RsrManual,
            RsrResumeMode.TargetOnly => RsrTargetOnly,
            RsrResumeMode.Henched => RsrHenched,
            _ => IsBocchiInstalled ? RsrHenched : RsrAuto,
        };

        /// <summary>
        /// Sends one mode change over IPC.
        ///
        /// IPC ONLY, AND ONCE. The previous version fired the IPC call and then also dispatched
        /// "/rotation Off" as a "fallback", which is not a fallback - it is the same command twice,
        /// and RSR prints a line to chat for each one when the user has toggle messages on.
        /// </summary>
        private bool ChangeMode(byte mode)
        {
            try
            {
                var changeMode = pluginInterface.GetIpcSubscriber<byte, object>("RotationSolverReborn.ChangeOperatingMode");
                changeMode.InvokeAction(mode);
                return true;
            }
            catch (Exception ex)
            {
                // Off is reported as a success even when the call throws. RSR releases a Wrath
                // Combo lease on its way off, and Wrath's LeaseCancelled callback can throw AFTER
                // RSR has already changed mode - so retrying would achieve nothing but a second
                // exception in the log.
                if (mode == RsrOff)
                    return true;

                log.Warning($"[AutoRezzer] Rotation Solver Reborn would not change mode: {ex.Message}");
                return false;
            }
        }

        private bool IsPluginLoaded(string internalName)
        {
            try
            {
                foreach (var plugin in pluginInterface.InstalledPlugins)
                {
                    if (plugin.IsLoaded
                        && string.Equals(plugin.InternalName, internalName, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch (Exception ex)
            {
                log.Debug($"[AutoRezzer] Could not enumerate installed plugins: {ex.Message}");
            }

            return false;
        }

        public void Dispose()
        {
            Resume();
        }
    }
}
