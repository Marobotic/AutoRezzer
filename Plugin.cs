using System;
using AutoRezzer.Core;
using AutoRezzer.UI;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

namespace AutoRezzer
{
    /// <summary>
    /// AutoRezzer - raises dead players around you while it is switched on.
    ///
    /// Derived in part from RotationSolverReborn (GPLv3, FFXIV-CombatReborn); see Core/RezTargeting
    /// for exactly which parts and README.md for the licence. This plugin is GPLv3 accordingly.
    /// </summary>
    public sealed class Plugin : IDalamudPlugin
    {
        private const string Command = "/autorez";
        private const string ShortCommand = "/az";

        private readonly IDalamudPluginInterface pluginInterface;
        private readonly ICommandManager commandManager;
        private readonly IFramework framework;
        private readonly IClientState clientState;
        private readonly IObjectTable objectTable;
        private readonly IChatGui chatGui;
        private readonly IPluginLog log;

        private readonly Configuration config;
        private readonly RezTargeting targeting;
        private readonly RezExecutor executor;
        private readonly RaiseAccepter accepter;
        private readonly JobSwitcher switcher;
        private readonly RsrManager rsrManager;
        private readonly IDtrBarEntry dtr;
        private readonly WindowSystem windows = new("AutoRezzer");
        private readonly ConfigWindow configWindow;

        /// <summary>
        /// How often the whole decision runs.
        ///
        /// Not every frame. Nothing here changes meaningfully inside 200ms - a body does not become
        /// raisable between two frames - and the work is an object-table sweep plus a raycast per
        /// candidate, which is exactly the kind of thing that shows up in somebody's frametimes if
        /// it runs at 120Hz for no reason.
        /// </summary>
        private const long TickMs = 200;
        private long lastTick;

        public Plugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IFramework framework,
            IClientState clientState,
            IObjectTable objectTable,
            ICondition condition,
            IDataManager dataManager,
            IGameGui gameGui,
            IDtrBar dtrBar,
            IChatGui chatGui,
            IPluginLog log)
        {
            this.pluginInterface = pluginInterface;
            this.commandManager = commandManager;
            this.framework = framework;
            this.clientState = clientState;
            this.objectTable = objectTable;
            this.chatGui = chatGui;
            this.log = log;

            config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

            targeting = new RezTargeting(clientState, objectTable, config);
            executor = new RezExecutor(objectTable, condition, log, config);
            accepter = new RaiseAccepter(objectTable, gameGui, log, config);
            switcher = new JobSwitcher(objectTable, condition, log, config);
            rsrManager = new RsrManager(pluginInterface, commandManager, log, config);

            configWindow = new ConfigWindow(
                config, targeting, executor, switcher,
                setEnabled: SetEnabled,
                save: () => pluginInterface.SavePluginConfig(config));
            windows.AddWindow(configWindow);

            pluginInterface.UiBuilder.Draw += windows.Draw;
            pluginInterface.UiBuilder.OpenConfigUi += OpenConfig;
            pluginInterface.UiBuilder.OpenMainUi += OpenConfig;
            framework.Update += OnUpdate;

            commandManager.AddHandler(Command, new CommandInfo(OnCommand)
            {
                HelpMessage = "Open AutoRezzer. \"/autorez on\" and \"/autorez off\" toggle it without opening anything.",
            });

            commandManager.AddHandler(ShortCommand, new CommandInfo(OnCommand)
            {
                HelpMessage = "Short for /autorez.",
            });

            // ── The server bar ──
            //
            // The whole point of it is that the switch is reachable without opening anything: this
            // plugin acts on its own, and the thing you want at the moment you notice it acting is
            // an off switch, not a window. Clicking the entry toggles; the text says which way it is.
            dtr = dtrBar.Get("AutoRezzer");
            dtr.OnClick = _ => Toggle();
            RefreshDtr();

            VerifyIds(dataManager);
        }

        /// <summary>
        /// Reads the hardcoded action ids back out of the game's own sheet and logs what they are.
        ///
        /// See the note on RezIds. A wrong id here fails silently - the plugin would just never cast
        /// - so this turns a mystery into one line in the log on the first load.
        /// </summary>
        private void VerifyIds(IDataManager dataManager)
        {
            try
            {
                // English explicitly: the names below are compared literally, and the client's own
                // language would make every non-English install warn about nothing.
                var sheet = dataManager.GetExcelSheet<Lumina.Excel.Sheets.Action>(Dalamud.Game.ClientLanguage.English);
                if (sheet == null)
                    return;

                // Paired with the name each id is supposed to have. Logging the name alone was not
                // enough: Vercure shipped as 7519, which is Contre Sixte - an enemy-only ability -
                // and the log dutifully said so while Red Mage stood there aiming an attack at
                // itself and eating "Invalid target" once a second. A mismatch is now a warning.
                foreach (var (id, expected) in new[]
                {
                    (RezIds.Raise, "Raise"),
                    (RezIds.Resurrection, "Resurrection"),
                    (RezIds.Ascend, "Ascend"),
                    (RezIds.Verraise, "Verraise"),
                    (RezIds.Egeiro, "Egeiro"),
                    (RezIds.Swiftcast, "Swiftcast"),
                    (RezIds.Vercure, "Vercure"),
                })
                {
                    if (!sheet.TryGetRow(id, out var row))
                    {
                        log.Warning($"[AutoRezzer] action {id} should be \"{expected}\" but that row does not exist.");
                        continue;
                    }

                    string name = row.Name.ExtractText();
                    if (!string.Equals(name, expected, StringComparison.Ordinal))
                        log.Warning($"[AutoRezzer] action {id} should be \"{expected}\" but the game calls it \"{name}\". Casting it will misfire.");
                    else
                        log.Information($"[AutoRezzer] action {id} = \"{name}\"");
                }
            }
            catch (Exception ex)
            {
                log.Warning($"[AutoRezzer] Could not verify action ids: {ex.Message}");
            }
        }

        private void OnUpdate(IFramework _)
        {
            // NOT BEHIND config.Enabled. Accepting a raise is about being dead, not about rezzing,
            // and somebody who turned the casting half off has not asked to lie on the floor.
            if (!config.Enabled && !config.AcceptRaise)
                return;

            long now = Environment.TickCount64;
            if (now - lastTick < TickMs)
                return;
            lastTick = now;

            try
            {
                Tick();
            }
            catch (Exception ex)
            {
                // A raise is a convenience. It is never worth taking the plugin - or the game - down.
                log.Error($"[AutoRezzer] Tick failed: {ex.Message}");
            }
        }

        private void Tick()
        {
            if (!clientState.IsLoggedIn || objectTable.LocalPlayer == null)
                return;

            // Ours first. A corpse cannot raise anybody, so if we are the one down there is nothing
            // else this tick could usefully do.
            if (accepter.Tick())
                return;

            if (!config.Enabled)
            {
                rsrManager.Resume();
                return;
            }

            // ── Already on a trip ──
            //
            // Driven before anything else and returned from unconditionally: while a switch is in
            // flight the ordinary path must not also be making decisions, or the two would fight
            // over whether to be casting or changing clothes.
            if (switcher.Busy)
            {
                rsrManager.Pause();
                switcher.Tick(
                    stillWantsRaising: () => targeting.FindBody() != null,
                    tryRaise: () =>
                    {
                        if (!targeting.CanRaise(out uint action))
                            return false;

                        var target = targeting.FindBody();
                        if (target == null)
                            return false;

                        bool sent = executor.TryRaise(target, action);
                        if (sent && config.Chatty)
                            chatGui.Print($"[AutoRezzer] {executor.LastAction}.");

                        return sent;
                    });

                return;
            }

            // ── The ordinary path: already on a job that can do this ──
            if (targeting.CanRaise(out uint raiseAction))
            {
                var body = targeting.FindBody();
                if (body == null)
                {
                    rsrManager.Resume();
                    return;
                }

                rsrManager.Pause();
                if (executor.TryRaise(body, raiseAction) && config.Chatty)
                    chatGui.Print($"[AutoRezzer] {executor.LastAction}.");

                return;
            }

            // ── Wrong job ──
            //
            // Only worth changing clothes if there is somebody to change them for, so the body is
            // found FIRST. Asking the other way round would have the plugin switching to White Mage
            // on the strength of a corpse thirty yalms behind a wall that it would then decline to
            // raise anyway.
            if (config.RezGearsetId < 0)
            {
                rsrManager.Resume();
                return;
            }

            if (targeting.FindBody() == null)
            {
                rsrManager.Resume();
                return;
            }

            rsrManager.Pause();
            _ = switcher.Begin();
        }

        private void OnCommand(string _, string args)
        {
            switch (args.Trim().ToLowerInvariant())
            {
                case "on":
                    SetEnabled(true);
                    chatGui.Print("[AutoRezzer] On.");
                    break;

                case "off":
                    SetEnabled(false);
                    chatGui.Print("[AutoRezzer] Off.");
                    break;

                case "toggle":
                    Toggle();
                    chatGui.Print(config.Enabled ? "[AutoRezzer] On." : "[AutoRezzer] Off.");
                    break;

                default:
                    OpenConfig();
                    break;
            }
        }

        private void OpenConfig() => configWindow.IsOpen = true;

        /// <summary>
        /// THE ONLY PLACE Enabled IS EVER WRITTEN.
        ///
        /// It used to be written in four: the window's checkbox, "/autorez on", "/autorez off", and
        /// the server bar. Three of them remembered to repaint the bar afterwards and the checkbox
        /// did not, so turning the plugin on from its own window left the bar reading "Rez: off" -
        /// two answers to one question, and the wrong one was the one on screen.
        ///
        /// Everything that wants to change it comes through here now, so the switch, the bar and the
        /// saved config cannot say different things.
        /// </summary>
        private void SetEnabled(bool value)
        {
            if (config.Enabled == value)
                return;

            config.Enabled = value;

            if (!value)
            {
                targeting.Reset();
                switcher.Cancel();
                rsrManager.Resume();
            }

            pluginInterface.SavePluginConfig(config);
            RefreshDtr();
        }

        private void Toggle() => SetEnabled(!config.Enabled);

        /// <summary>
        /// Repaints the server bar entry.
        ///
        /// Called on every change rather than every frame. The entry is a string the game re-lays
        /// out when it is assigned, and assigning the same text sixty times a second is a
        /// measurable amount of nothing.
        /// </summary>
        private void RefreshDtr()
        {
            bool on = config.Enabled;
            dtr.Text = on ? "Rez: on" : "Rez: off";
            dtr.Tooltip = on
                ? "AutoRezzer is raising. Click to stop."
                : "AutoRezzer is off. Click to start.";
        }

        public void Dispose()
        {
            framework.Update -= OnUpdate;
            pluginInterface.UiBuilder.Draw -= windows.Draw;
            pluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;
            pluginInterface.UiBuilder.OpenMainUi -= OpenConfig;
            commandManager.RemoveHandler(Command);
            commandManager.RemoveHandler(ShortCommand);
            dtr.Remove();
            windows.RemoveAllWindows();
            rsrManager.Dispose();
        }
    }
}
