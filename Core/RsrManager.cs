using System;
using Dalamud.Game.Command;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AutoRezzer.Core
{
    /// <summary>
    /// Coordinates with Rotation Solver Reborn (RSR) to give AutoRezzer priority when dead allies are present.
    ///
    /// When an ally dies and AutoRezzer is preparing to raise, RSR can consume GCDs, trigger auto-movement,
    /// or clip spells if left running. RsrManager pauses RSR (/rotation Off) during the raise window and
    /// restores it (/rotation Auto) once the raise resolves or when no bodies remain.
    /// </summary>
    internal sealed class RsrManager : IDisposable
    {
        private readonly IDalamudPluginInterface pluginInterface;
        private readonly ICommandManager commandManager;
        private readonly IPluginLog log;
        private readonly Configuration config;

        private bool wasRsrActive;
        private bool isPaused;

        public RsrManager(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IPluginLog log,
            Configuration config)
        {
            this.pluginInterface = pluginInterface;
            this.commandManager = commandManager;
            this.log = log;
            this.config = config;
        }

        public bool IsPaused => isPaused;

        /// <summary>
        /// Checks if Rotation Solver Reborn's autorotation is currently active.
        /// </summary>
        public bool IsRsrActive()
        {
            try
            {
                var subscriber = pluginInterface.GetIpcSubscriber<bool>("RotationSolverReborn.AutorotationActive");
                return subscriber.InvokeFunc();
            }
            catch
            {
                // If IPC fails (not loaded or older version), check if the command exists as fallback.
                return commandManager.Commands.ContainsKey("/rotation") || commandManager.Commands.ContainsKey("/rsr");
            }
        }

        /// <summary>
        /// Pauses Rotation Solver Reborn while raising to grant AutoRezzer exclusive priority.
        /// </summary>
        public void Pause()
        {
            if (!config.PauseRotationSolver || isPaused)
                return;

            bool active = IsRsrActive();
            if (!active)
                return;

            wasRsrActive = true;
            isPaused = true;

            try
            {
                // Attempt IPC first, followed by command execution to ensure state is set to Off.
                try
                {
                    var changeMode = pluginInterface.GetIpcSubscriber<byte, object>("RotationSolverReborn.ChangeOperatingMode");
                    changeMode.InvokeAction(0); // 0 = StateCommandType.Off
                }
                catch
                {
                    // Fallback to command dispatch.
                }

                commandManager.ProcessCommand("/rotation Off");
                log.Information("[AutoRezzer] Paused Rotation Solver Reborn for raising.");
            }
            catch (Exception ex)
            {
                log.Warning($"[AutoRezzer] Failed to pause Rotation Solver Reborn: {ex.Message}");
            }
        }

        /// <summary>
        /// Resumes Rotation Solver Reborn if it was previously paused by AutoRezzer.
        /// </summary>
        public void Resume()
        {
            if (!isPaused)
                return;

            isPaused = false;

            if (!wasRsrActive)
                return;

            wasRsrActive = false;

            try
            {
                try
                {
                    var changeMode = pluginInterface.GetIpcSubscriber<byte, object>("RotationSolverReborn.ChangeOperatingMode");
                    changeMode.InvokeAction(1); // 1 = StateCommandType.Auto
                }
                catch
                {
                    // Fallback to command dispatch.
                }

                commandManager.ProcessCommand("/rotation Auto");
                log.Information("[AutoRezzer] Resumed Rotation Solver Reborn after raising.");
            }
            catch (Exception ex)
            {
                log.Warning($"[AutoRezzer] Failed to resume Rotation Solver Reborn: {ex.Message}");
            }
        }

        public void Dispose()
        {
            Resume();
        }
    }
}
