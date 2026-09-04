using System;
using System.Reflection;
using System.Runtime.Loader;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AutoRezzer.Core
{
    /// <summary>
    /// Gets Rotation Solver Reborn out of the way for the length of a raise, and puts it back the
    /// way it actually was.
    ///
    /// ── WHY THIS IS NOT "TURN IT OFF, TURN IT ON" ─────────────────────────
    /// RSR has more than one "on". Off, Auto, TargetOnly, Manual, AutoDuty, Henched and PvP are all
    /// distinct states, and several of them exist precisely because another plugin is driving. The
    /// one that matters most is Henched - rotation only, no targeting - because that is what
    /// BOCCHI's Illegal Mode puts RSR into while it runs FATEs and critical encounters.
    ///
    /// So the naive pause/resume is actively harmful in that setup: sending Auto afterwards hands
    /// RSR back its own targeting in the middle of somebody else's automation, and BOCCHI will not
    /// notice, because it only re-sends a mode when its own desired mode changes. It thinks RSR is
    /// still Henched. It is not, and now two things are choosing targets.
    ///
    /// ── AND WHY IT IS NOT A SETTING EITHER ────────────────────────────────
    /// It was, and the setting was wrong every time it disagreed with reality. "Resume as Henched"
    /// restores Henched to somebody who was in Auto with low-HP targeting, which is the same class
    /// of bug as resuming Auto over Henched - a guess made in advance, applied to a state it never
    /// looked at.
    ///
    /// RSR's IPC cannot be asked: it offers one read, AutorotationActive(), a bool. But RSR's mode
    /// is not hidden - it is seven public statics on RotationSolver.Basic.DataCenter, set by
    /// RSCommands.UpdateState and by nothing else - so the mode is READ off the live plugin at the
    /// moment of pausing and sent back verbatim at the moment of resuming. Reflection into another
    /// plugin, deliberately, because the alternative is being wrong on purpose.
    ///
    /// There is nothing to configure. The read answers it, and when the read fails the fallback is
    /// Henched with BOCCHI loaded and Auto without, which is right often enough that offering a
    /// dropdown for it only ever gave people a way to be wrong on purpose.
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
        private const byte RsrAutoDuty = 4;
        private const byte RsrHenched = 5;
        private const byte RsrPvP = 6;

        private const string BocchiInternalName = "BOCCHI";

        /// <summary>Where RSR keeps the mode. Its own assembly, not the plugin assembly.</summary>
        private const string RsrCoreAssembly = "RotationSolver.Basic";
        private const string RsrDataCenter = "RotationSolver.Basic.DataCenter";

        private readonly IDalamudPluginInterface pluginInterface;
        private readonly IPluginLog log;

        /// <summary>The mode RSR was actually in when we paused it, or null when it could not be
        /// read and the configured fallback has to answer for it.</summary>
        private byte? capturedMode;

        /// <summary>Whether RSR was running at all when we paused it. Nothing is restored if it was
        /// already off - coming back from a raise is not an excuse to switch somebody's
        /// autorotation on for them.</summary>
        private bool wasRsrActive;
        private bool isPaused;

        /// <summary>
        /// The window's copy of the live mode, refreshed on a timer.
        ///
        /// Draw runs every frame and the read walks every assembly in the process. A pause reads
        /// fresh - a stale answer there would restore the wrong mode - but nobody needs a label
        /// repainted at 120Hz.
        /// </summary>
        private byte? peekedMode;
        private long peekedAt;
        private const long PeekTtlMs = 500;

        public RsrManager(IDalamudPluginInterface pluginInterface, IPluginLog log)
        {
            this.pluginInterface = pluginInterface;
            this.log = log;
        }

        public bool IsPaused => isPaused;

        /// <summary>Whether RSR is loaded at all, which is what the config window's warning asks.</summary>
        public bool IsRsrInstalled => IsPluginLoaded("RotationSolver") || IsPluginLoaded("RotationSolverReborn");

        /// <summary>Whether BOCCHI is loaded. Not whether Illegal Mode is on - BOCCHI publishes no
        /// IPC, so that is unknowable from here; presence is the closest available signal.</summary>
        public bool IsBocchiInstalled => IsPluginLoaded(BocchiInternalName);

        /// <summary>
        /// RSR's mode right now, for the window, or empty when it cannot be read.
        ///
        /// Throttled. See <see cref="peekedMode"/>.
        /// </summary>
        public string LiveModeName
        {
            get
            {
                long now = Environment.TickCount64;
                if (now - peekedAt > PeekTtlMs)
                {
                    peekedMode = ReadLiveMode();
                    peekedAt = now;
                }

                return peekedMode.HasValue ? NameOf(peekedMode.Value) : string.Empty;
            }
        }

        /// <summary>Whether the live read is working, which is what decides whether the fallback
        /// setting matters at all.</summary>
        public bool CanReadLiveMode => !string.IsNullOrEmpty(LiveModeName);

        /// <summary>Whether RSR's autorotation is running right now, over IPC.
        ///
        /// Only used when the live read has failed. It answers a coarser question - Manual and
        /// Henched both report true, Off reports false - which is enough to decide whether there is
        /// anything to pause, and not enough to decide what to restore.</summary>
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
            if (isPaused)
                return;

            // Read BEFORE anything is sent, or the mode read back is the one we just imposed.
            byte? live = ReadLiveMode();

            if (live.HasValue)
            {
                if (live.Value == RsrOff)
                    return;
            }
            else if (!IsRsrActive())
            {
                return;
            }

            capturedMode = live;
            wasRsrActive = true;
            isPaused = true;

            if (ChangeMode(RsrOff))
            {
                log.Information(live.HasValue
                    ? $"[AutoRezzer] Paused Rotation Solver Reborn for raising (was {NameOf(live.Value)})."
                    : "[AutoRezzer] Paused Rotation Solver Reborn for raising.");
            }
        }

        /// <summary>Puts RSR back into the mode it was in. Idempotent.</summary>
        public void Resume()
        {
            if (!isPaused)
                return;

            isPaused = false;

            if (!wasRsrActive)
                return;

            wasRsrActive = false;

            byte mode = capturedMode ?? ResolveFallbackMode();
            bool guessed = !capturedMode.HasValue;
            capturedMode = null;

            // The peek is stale the instant this lands, and the window would otherwise spend half a
            // second telling the user RSR is off while it is not.
            peekedAt = 0;

            if (ChangeMode(mode))
            {
                log.Information(guessed
                    ? $"[AutoRezzer] Resumed Rotation Solver Reborn ({NameOf(mode)}, from the fallback setting)."
                    : $"[AutoRezzer] Resumed Rotation Solver Reborn ({NameOf(mode)}).");
            }
        }

        // ══════════════════════════════════════════════════════════
        //  READING RSR'S ACTUAL MODE
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// RSR's mode, read off the live plugin, or null when that is not possible.
        ///
        /// ── THE FLAGS ARE NOT INDEPENDENT, AND THE ORDER IS THE POINT ─────
        /// RSCommands.UpdateState sets several of them per mode, so testing them in the wrong order
        /// gives a wrong answer that looks right: Henched sets IsManual as well as IsHenched, so
        /// asking about Manual first reports Manual for every BOCCHI user - which is exactly the
        /// class of mistake this whole file exists to stop. The order below is narrowest first.
        ///
        /// ── AND NOTHING IS CACHED ACROSS CALLS ────────────────────────────
        /// Not a Type, not a PropertyInfo. Holding either would keep RSR's assembly load context
        /// alive and stop RSR unloading cleanly on its own update - this plugin would become the
        /// reason somebody's RSR update needed a game restart. Resolution is a walk over the loaded
        /// assemblies, which happens once per raise, not once per frame.
        /// </summary>
        private byte? ReadLiveMode()
        {
            try
            {
                var dataCenter = FindRsrDataCenter();
                if (dataCenter == null)
                    return null;

                bool? state = ReadStaticBool(dataCenter, "State");
                if (!state.HasValue)
                    return null;

                if (!state.Value)
                    return RsrOff;

                if (ReadStaticBool(dataCenter, "IsHenched") == true)
                    return RsrHenched;

                if (ReadStaticBool(dataCenter, "IsAutoDuty") == true)
                    return RsrAutoDuty;

                if (ReadStaticBool(dataCenter, "IsPvPStateEnabled") == true)
                    return RsrPvP;

                if (ReadStaticBool(dataCenter, "IsTargetOnly") == true)
                    return RsrTargetOnly;

                if (ReadStaticBool(dataCenter, "IsManual") == true)
                    return RsrManual;

                return RsrAuto;
            }
            catch (Exception ex)
            {
                log.Debug($"[AutoRezzer] Could not read Rotation Solver Reborn's mode: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// RSR's DataCenter type, found among the assemblies loaded in this process.
        ///
        /// Every plugin gets its own load context, so the type is not reachable by name from here -
        /// the contexts have to be walked. Returns null rather than throwing when RSR is absent,
        /// which is the ordinary case for anybody not running it.
        /// </summary>
        private static Type? FindRsrDataCenter()
        {
            foreach (var context in AssemblyLoadContext.All)
            {
                foreach (var assembly in context.Assemblies)
                {
                    if (!string.Equals(assembly.GetName().Name, RsrCoreAssembly, StringComparison.Ordinal))
                        continue;

                    var type = assembly.GetType(RsrDataCenter, throwOnError: false);
                    if (type != null)
                        return type;
                }
            }

            return null;
        }

        /// <summary>
        /// One static bool off that type, or null when it is not there.
        ///
        /// NonPublic as well as Public: DataCenter is an internal class, so its members are only
        /// public to RSR. A null return is what makes a renamed field degrade into "use the
        /// fallback" instead of an exception every time somebody dies.
        /// </summary>
        private static bool? ReadStaticBool(Type type, string name)
        {
            const BindingFlags flags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;

            var property = type.GetProperty(name, flags);
            if (property?.GetValue(null) is bool fromProperty)
                return fromProperty;

            var field = type.GetField(name, flags);
            if (field?.GetValue(null) is bool fromField)
                return fromField;

            return null;
        }

        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The mode to fall back to when RSR's own state could not be read.
        ///
        /// NOT A SETTING ANY MORE, and it should never have been one: a mode chosen in advance is
        /// applied to a state nobody looked at, which is how "resume as Henched" used to restore
        /// Henched to people who had been in Auto. The live read answers it properly, and this is
        /// only the guess for the day RSR renames a field.
        ///
        /// Henched when BOCCHI is loaded, because that is the mode BOCCHI drives RSR with and coming
        /// back to anything else quietly takes the wheel off it. With no BOCCHI, Auto, the mode
        /// somebody running RSR by hand is nearly always in.
        /// </summary>
        private byte ResolveFallbackMode() => IsBocchiInstalled ? RsrHenched : RsrAuto;

        private static string NameOf(byte mode) => mode switch
        {
            RsrOff => "Off",
            RsrAuto => "Auto",
            RsrTargetOnly => "Target Only",
            RsrManual => "Manual",
            RsrAutoDuty => "AutoDuty",
            RsrHenched => "Henched",
            RsrPvP => "PvP",
            _ => "Auto",
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
