using System;
using AutoRezzer.Core;
using AutoRezzer.UI;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Command;
using Dalamud.Game.Gui.Dtr;
using Dalamud.Game.Text;
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
        private readonly ICondition condition;
        private readonly IChatGui chatGui;
        private readonly IPluginLog log;

        private readonly Configuration config;
        private readonly RezTargeting targeting;
        private readonly RezExecutor executor;
        private readonly RaiseAccepter accepter;
        private readonly JobSwitcher switcher;
        private readonly RsrManager rsrManager;
        private readonly PhantomJobs phantom;
        private readonly PhantomJobSwitcher phantomSwitcher;
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
            this.condition = condition;
            this.chatGui = chatGui;
            this.log = log;

            config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            config.Migrate(log);

            void Save() => pluginInterface.SavePluginConfig(config);

            phantom = new PhantomJobs(dataManager, condition, log);
            phantomSwitcher = new PhantomJobSwitcher(phantom, config, log);
            targeting = new RezTargeting(clientState, objectTable, config, phantom);
            executor = new RezExecutor(objectTable, condition, log, config, Save);
            accepter = new RaiseAccepter(objectTable, gameGui, log, config, Save);
            switcher = new JobSwitcher(objectTable, condition, log, config);
            rsrManager = new RsrManager(pluginInterface, log, config);

            configWindow = new ConfigWindow(
                config, targeting, executor, switcher, rsrManager, phantom, phantomSwitcher,
                setEnabled: SetEnabled,
                save: Save);
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
                    (RezIds.OccultRaise, "Occult Raise"),
                    (RezIds.Revive, "Revive"),
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
            // ── THE MASTER SWITCH IS MASTER ──
            //
            // Everything is behind it, accepting a raise included. This used to be the other way
            // round - accepting was exempt, on the reasoning that being raised has nothing to do
            // with being able to raise - and the reasoning was fine while the answer to "is this
            // plugin doing anything" was still yes. It is a worse trade than it looks: a switch
            // that leaves something running is a switch you cannot trust to stop it, and this
            // plugin's whole job is acting on its own.
            //
            // Off means off. "Accept raises cast on me" is still its own setting; it is simply a
            // setting inside a plugin that is running.
            //
            // A switch still in flight keeps the tick alive, or turning everything off halfway
            // through a job change would strand the trip home in whatever state it was in.
            if (!config.Enabled && !switcher.Busy && !phantomSwitcher.Busy)
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

            if (!config.Enabled)
            {
                // Driven home first. Switching the plugin off is not a reason to leave somebody
                // wearing a healer's gearset or levelling a phantom job they did not pick - both
                // machines are told nobody needs raising, which walks them through Returning.
                //
                // This is the ONLY thing that still happens with the switch off, and it is not a
                // feature - it is finishing something the plugin already started.
                DriveSwitchersHome();
                rsrManager.Resume();
                return;
            }

            // Ours first. A corpse cannot raise anybody, so if we are the one down there is nothing
            // else this tick could usefully do.
            if (accepter.Tick())
                return;

            // ── One sweep, at the top, whatever happens next ──
            //
            // It used to be called from four places and skipped entirely on two of the paths, which
            // was fine while the only consumer was "is there a body". It is not fine now that the
            // window reports the room: a status line that says "solo gaming" because the code took
            // the branch that never counted anybody is worse than no status line.
            bool canRaiseNow = targeting.CanRaise(out uint raiseAction);
            var body = targeting.FindBody();

            // ── Borrowing a phantom job ──
            //
            // Before the gearset machine, and returned from unconditionally for the same reason it
            // is: while either switch is in flight the ordinary path must not also be making
            // decisions, or the two would fight over whether to be casting or changing job.
            if (phantomSwitcher.Busy)
            {
                rsrManager.Pause();
                phantomSwitcher.Tick(
                    stillWantsRaising: () => body != null,
                    tryRaise: () =>
                    {
                        // Re-asked rather than reusing raiseAction from the top of the tick: the
                        // whole point of this errand is that the phantom job changed underneath us,
                        // so the action that was right when the tick started is not the one to send.
                        if (body == null || !targeting.CanRaise(out uint action))
                            return false;

                        return Raise(body, action);
                    });

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
                    stillWantsRaising: () => body != null,
                    tryRaise: () =>
                    {
                        if (!canRaiseNow || body == null)
                            return false;

                        return Raise(body, raiseAction);
                    });

                return;
            }

            if (body == null)
            {
                rsrManager.Resume();
                return;
            }

            // ── The ordinary path: already on a job that can do this ──
            if (canRaiseNow)
            {
                // ── THE MP FLOOR GOES BEFORE Pause(), NOT AFTER ──
                //
                // This is the whole reason it is a separate question. Pausing Rotation Solver and
                // then declining to cast costs the player their rotation AND leaves the body on the
                // floor - strictly worse than never having got involved. Below the floor the plugin
                // takes its hands off entirely and lets RSR keep working.
                //
                // Occult Raise is free, so HasMpForRaise waves it through and none of this applies.
                if (!targeting.HasMpForRaise(raiseAction))
                {
                    // Before giving up: a phantom raise costs nothing at all, so being out of MP is
                    // not a reason to stay on a job whose raise you cannot pay for.
                    if (WantsPhantomSwap(out int mplessJob))
                    {
                        rsrManager.Pause();
                        _ = phantomSwitcher.Begin(mplessJob);
                        return;
                    }

                    rsrManager.Resume();
                    return;
                }

                rsrManager.Pause();

                // The Red Mage detour, asked BEFORE the raise rather than after it fails. TryRaise
                // failing is not a signal - it fails for a dozen transient reasons every tick - so
                // the condition it would have to be inferred from is checked directly instead.
                if (WantsRedMageDetour(out int rdmGearset) && switcher.Begin(rdmGearset))
                    return;

                _ = Raise(body, raiseAction);
                return;
            }

            // ── Wrong job ──
            //
            // Only worth changing anything if there is somebody to change it for, and by here we
            // know there is. Asking the other way round would have the plugin switching to White
            // Mage on the strength of a corpse thirty yalms behind a wall it would then decline to
            // raise anyway.
            //
            // The phantom job goes first because it is strictly cheaper: instant, free, and it does
            // not take your gear off in the middle of a zone full of people.
            if (WantsPhantomSwap(out int borrowJob))
            {
                rsrManager.Pause();
                _ = phantomSwitcher.Begin(borrowJob);
                return;
            }

            if (config.RezGearsetId < 0)
            {
                rsrManager.Resume();
                return;
            }

            rsrManager.Pause();
            _ = switcher.Begin(config.RezGearsetId);
        }

        /// <summary>
        /// Walks both switchers back to where they started, with no raise to distract them.
        ///
        /// Both are asked, not just the busy one, because they are independent - a gearset trip and
        /// a phantom job trip can be outstanding at the same time in principle, and "go home" is
        /// the correct instruction to either regardless.
        /// </summary>
        private void DriveSwitchersHome()
        {
            if (phantomSwitcher.Busy)
                phantomSwitcher.Tick(stillWantsRaising: () => false, tryRaise: () => false);

            if (switcher.Busy)
                switcher.Tick(stillWantsRaising: () => false, tryRaise: () => false);
        }

        /// <summary>
        /// Whether to borrow a raising phantom job, and which one.
        ///
        /// Every condition is a reason somebody would be annoyed otherwise: the setting is off by
        /// default because the phantom job is the thing being levelled; the zone has to be up; a
        /// raising job has to actually be levelled far enough; and the change has to be one the game
        /// will accept, which out of combat plus BOCCHI's four second settle is what decides.
        /// </summary>
        private bool WantsPhantomSwap(out int jobRow)
        {
            jobRow = -1;

            if (!config.UseOccultRaise || !config.SwapPhantomJobToRaise)
                return false;

            if (!phantom.InOccultCrescent || phantom.CanOccultRaise)
                return false;

            if (!phantom.CanChangeJobNow())
                return false;

            jobRow = phantom.PickRaiser(config.PreferredPhantomRaiser);
            return jobRow >= 0 && jobRow != phantom.CurrentJobRow;
        }

        /// <summary>
        /// Whether to go and be a Red Mage about this.
        ///
        /// The narrow case the setting describes and nothing wider: out of combat (the game refuses
        /// job changes in a fight anyway), on a job that is not already Red Mage, with Swiftcast
        /// genuinely unavailable. Dualcast is then the only instant raise left in the game, and a
        /// gearset swap is cheaper than the sixty seconds of waiting it replaces.
        /// </summary>
        private bool WantsRedMageDetour(out int gearsetId)
        {
            gearsetId = -1;

            if (!config.OutOfCombatSwitchToRdm || condition[ConditionFlag.InCombat])
                return false;

            // A phantom raise is already instant and already free. Swapping to Red Mage to buy an
            // instant cast we are holding for nothing would be pure ceremony.
            if (config.UseOccultRaise && phantom.CanOccultRaise)
                return false;

            var me = objectTable.LocalPlayer;
            if (me == null || me.ClassJob.RowId == RezIds.RedMage)
                return false;

            if (executor.SwiftcastReady())
                return false;

            gearsetId = config.RdmGearsetId >= 0
                ? config.RdmGearsetId
                : JobSwitcher.FirstGearsetForJob(RezIds.RedMage);

            return gearsetId >= 0;
        }

        /// <summary>Casts, and says so if asked to. Returns whether an action went out.</summary>
        private bool Raise(Dalamud.Game.ClientState.Objects.Types.IBattleChara body, uint raiseAction)
        {
            if (!executor.TryRaise(body, raiseAction))
                return false;

            // Only the raise itself. Swiftcast and Vercure both come back true here, and announcing
            // those would turn one line per body into three.
            if (config.AnnounceInEcho && executor.LastWasRaise)
                Echo(executor.LastAction + ".");

            return true;
        }

        /// <summary>
        /// Prints one line to the local echo channel.
        ///
        /// ECHO SPECIFICALLY. It is the one channel that cannot leave this client - no party, no
        /// linkshell, no /say - so there is no configuration of this plugin, and no mistake in it,
        /// that announces to other people that something is raising on your behalf.
        /// </summary>
        private void Echo(string message)
        {
            chatGui.Print(new XivChatEntry
            {
                Type = XivChatType.Echo,
                Message = $"[AutoRezzer] {message}",
            });
        }

        private void OnCommand(string _, string args)
        {
            switch (args.Trim().ToLowerInvariant())
            {
                case "on":
                    SetEnabled(true);
                    Echo("On.");
                    break;

                case "off":
                    SetEnabled(false);
                    Echo("Off.");
                    break;

                case "toggle":
                    Toggle();
                    Echo(config.Enabled ? "On." : "Off.");
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
                // Abort rather than Cancel on both: switching the plugin off is not a reason to
                // leave somebody in a healer's gearset or on a phantom job they did not pick. The
                // tick keeps running while either is busy specifically so these can finish.
                targeting.Reset();
                switcher.Abort();
                phantomSwitcher.Abort();
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
                ? "AutoRezzer is raising, and accepting raises cast on you. Click to stop."
                : "AutoRezzer is off - nothing is raising and nothing is being accepted. Click to start.";
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
