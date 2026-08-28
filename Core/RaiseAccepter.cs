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
    /// has its own switch rather than living under the job selector.
    /// </summary>
    internal sealed class RaiseAccepter
    {
        private readonly IObjectTable objectTable;
        private readonly IGameGui gameGui;
        private readonly IPluginLog log;
        private readonly Configuration config;

        private long lastPressTick;

        /// <summary>The prompt does not vanish the instant it is answered, so without a floor this
        /// would press the same button several times before the addon closed. Harmless but noisy,
        /// and noise at the packet level is the thing worth not generating.</summary>
        private const long PressFloorMs = 500;

        public RaiseAccepter(IObjectTable objectTable, IGameGui gameGui, IPluginLog log, Configuration config)
        {
            this.objectTable = objectTable;
            this.gameGui = gameGui;
            this.log = log;
            this.config = config;
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
        /// ─────────────────────────────────────────────────────────────────
        /// </summary>
        public unsafe bool Tick()
        {
            if (!config.AcceptRaise)
                return false;

            long now = Environment.TickCount64;
            if (now - lastPressTick < PressFloorMs)
                return false;

            var me = objectTable.LocalPlayer;
            if (me == null || !me.IsDead)
                return false;

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
            _ = addon->FireCallbackInt(0);
            config.RecordAcceptedRaise();
            log.Information($"[AutoRezzer] Accepted a raise. (Total accepted: {config.AcceptedRaisesCount})");
            return true;
        }
    }
}
