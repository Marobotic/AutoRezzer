using System;
using Dalamud.Game.ClientState.Objects;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace AutoRezzer.Core
{
    /// <summary>
    /// Presses "yes" on the raise prompt when you are the one on the floor.
    ///
    /// The other half of the plugin, and the half that works on every job in the game: anybody can
    /// be raised. A Warrior who will never cast a thing still benefits from this, which is why it
    /// has its own switch rather than living under the job selector - though that switch, like
    /// every other one here, only means anything while the plugin itself is on.
    /// </summary>
    internal sealed class RaiseAccepter
    {
        private readonly IObjectTable objectTable;
        private readonly IGameGui gameGui;
        private readonly IPluginLog log;
        private readonly Configuration config;

        /// <summary>Writes the config out. The accepted-raise count is only worth keeping if it
        /// survives the session it was earned in.</summary>
        private readonly Action saveConfig;

        private long lastPressTick;

        /// <summary>
        /// When the Raise status was first seen on us, so the wait has something to count from.
        ///
        /// Zero means "not currently holding one". Reset the moment the status goes, so a second
        /// death in the same fight waits its own full delay rather than inheriting the first one's
        /// head start.
        /// </summary>
        private long raisePendingSince;

        /// <summary>The prompt does not vanish the instant it is answered, so without a floor this
        /// would press the same button several times before the addon closed. Harmless but noisy,
        /// and noise at the packet level is the thing worth not generating.</summary>
        private const long PressFloorMs = 500;

        public RaiseAccepter(IObjectTable objectTable, IGameGui gameGui, IPluginLog log,
            Configuration config, Action saveConfig)
        {
            this.objectTable = objectTable;
            this.gameGui = gameGui;
            this.log = log;
            this.config = config;
            this.saveConfig = saveConfig;
        }

        /// <summary>
        /// Accepts a pending raise, if there is one.
        ///
        /// ── WHY THIS IS SAFE TO DO BLIND ──────────────────────────────────
        /// It fires a "yes" at SelectYesno, which is the same dialog the game uses for trades,
        /// leaving a duty, abandoning a quest and a hundred other things you would not want pressed
        /// on your behalf. What makes it safe is not reading the prompt text - that would be one
        /// language's wording baked into the plugin - but the state it insists on first:
        ///
        ///   you are dead, AND you are carrying the Raise status.
        ///
        /// The Raise status exists only between somebody's cast landing and you answering the
        /// prompt. In that window there is exactly one question the game asks a corpse, and this is
        /// it. Outside that window nothing here touches the dialog at all.
        ///
        /// Returns true only on the frame the button is actually pressed. Waiting out the delay is
        /// not "handled": the tick has to carry on so the rest of the plugin keeps working while a
        /// corpse counts down.
        /// ─────────────────────────────────────────────────────────────────
        /// </summary>
        public unsafe bool Tick()
        {
            long now = Environment.TickCount64;

            var me = objectTable.LocalPlayer;
            if (me == null || !me.IsDead)
            {
                raisePendingSince = 0;
                return false;
            }

            bool raisePending = false;
            foreach (var status in me.StatusList)
            {
                if (status != null && status.StatusId == RezIds.RaisePending)
                {
                    raisePending = true;
                    break;
                }
            }

            if (!raisePending)
            {
                raisePendingSince = 0;
                return false;
            }

            // ── THE CLOCK STARTS WHERE THE STATUS DOES, NOT WHERE THE SETTING IS READ ──
            //
            // Stamped before the setting is consulted so that switching acceptance on midway
            // through being dead does not restart the wait, and so that turning the delay down while
            // lying there takes effect against the time already served rather than against zero.
            if (raisePendingSince == 0)
                raisePendingSince = now;

            if (!config.AcceptRaise)
                return false;

            if (now - lastPressTick < PressFloorMs)
                return false;

            // ── THE WAIT ──
            //
            // The reason it exists is the wipe. Standing up the instant somebody casts on you puts
            // you back into the mechanic that killed you, with Weakness and a sliver of health, and
            // spends their raise for nothing. Zero by default, because outside a wipe the right
            // answer is always "yes, now".
            long waitMs = (long)(Math.Clamp(config.AcceptRaiseDelaySeconds, 0f, Configuration.AcceptDelayCeiling) * 1000f);
            if (now - raisePendingSince < waitMs)
                return false;

            // GetAddonByName hands back a wrapper now rather than a raw pointer; IsVisible is on
            // the wrapper, the callback is on the struct behind it.
            var handle = gameGui.GetAddonByName("SelectYesno");
            if (handle.IsNull || !handle.IsVisible)
                return false;

            var addon = (AtkUnitBase*)handle.Address;
            if (addon == null)
                return false;

            lastPressTick = now;
            raisePendingSince = 0;
            _ = addon->FireCallbackInt(0);
            config.RecordAcceptedRaise();
            saveConfig();
            log.Information($"[AutoRezzer] Accepted a raise. (Total accepted: {config.AcceptedRaisesCount})");
            return true;
        }
    }
}
