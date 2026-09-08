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
        private readonly IDataManager dataManager;
        private readonly IPluginLog log;

        private readonly Configuration config;
        private readonly RezTargeting targeting;
        private readonly RezExecutor executor;
        private readonly RaiseAccepter accepter;
        private readonly JobSwitcher switcher;
        private readonly RsrManager rsrManager;
        private readonly PhantomJobs phantom;
        private readonly PhantomJobSwitcher phantomSwitcher;
        private readonly Zone zone;
        private readonly Fonts fonts;
        private readonly Icons icons;
        private readonly StatusReporter status;
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

        /// <summary>
        /// Whether the plugin was allowed to act on the previous tick.
        ///
        /// Kept so the moment it STOPS being allowed can be noticed. Walking into a sanctuary is not
        /// the same event as the tick after it: the first one has a gearset trip to abandon, a
        /// Rotation Solver to hand back and a server bar entry to repaint, and every one after it
        /// has nothing to do at all.
        /// </summary>
        private bool wasActive;

        /// <summary>
        /// What the server bar is currently saying, so it is only repainted when that changes.
        ///
        /// ── THE BAR AND THE WINDOW MUST ANSWER THE SAME QUESTION ──────────
        /// They did not. The bar asked StandDownReason, which knows about sanctuaries and the
        /// content switches; the window asked the status reporter, which also knows whether this
        /// character has any route to a rez at all. A Gunbreaker with no rez gearset therefore read
        /// "Rez: on" on the bar and "Paused" in the window at the same moment, which is the plugin
        /// contradicting itself in two places a foot apart.
        ///
        /// Both now come from BarStateNow, and the word is the same in both: paused is paused.
        /// </summary>
        private enum BarState
        {
            Off,
            Paused,
            On,
        }

        private BarState barState = BarState.Off;

        public Plugin(
            IDalamudPluginInterface pluginInterface,
            ICommandManager commandManager,
            IFramework framework,
            IClientState clientState,
            IObjectTable objectTable,
            ICondition condition,
            IDataManager dataManager,
            IGameGui gameGui,
            ITextureProvider textureProvider,
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
            this.dataManager = dataManager;
            this.log = log;

            config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
            config.Migrate(log);

            void Save() => pluginInterface.SavePluginConfig(config);

            phantom = new PhantomJobs(config, dataManager, condition, log);
            phantomSwitcher = new PhantomJobSwitcher(phantom, config, log);
            targeting = new RezTargeting(clientState, objectTable, config, phantom);
            executor = new RezExecutor(objectTable, condition, log, config, Save);
            accepter = new RaiseAccepter(objectTable, gameGui, log, config, Save);
            switcher = new JobSwitcher(objectTable, condition, log, config);
            rsrManager = new RsrManager(pluginInterface, log);
            zone = new Zone(log, clientState, phantom);

            fonts = new Fonts(pluginInterface, log);
            icons = new Icons(textureProvider);
            status = new StatusReporter(objectTable, dataManager, config, targeting,
                executor, switcher, phantomSwitcher, phantom, zone);

            configWindow = new ConfigWindow(
                config, dataManager, phantom, phantomSwitcher, status, fonts, icons,
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
            barState = config.Enabled ? BarState.On : BarState.Off;
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
                    (RezIds.ThinAir, "Thin Air"),
                    (RezIds.LucidDreaming, "Lucid Dreaming"),
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
            long now = Environment.TickCount64;
            if (now - lastTick < TickMs)
                return;
            lastTick = now;

            // ── THE MASTER SWITCH IS THE FIRST THING ASKED, AND IT IS ABSOLUTE ──
            //
            // Off means the plugin is inert. No sweep of the object table, no rez, no accepting one
            // cast on you, nothing said to Rotation Solver, no reading of the world at all. Every
            // line below this point belongs to a plugin that is running.
            //
            // The single exception is a job change already in flight, which is walked home rather
            // than abandoned. That is not the plugin still working; it is the plugin declining to
            // leave somebody stood in a healer's gearset because they reached for the switch a
            // second after it started changing their clothes.
            if (!config.Enabled)
            {
                if (!switcher.Busy && !phantomSwitcher.Busy)
                    return;

                try
                {
                    DriveSwitchersHome();
                }
                catch (Exception ex)
                {
                    log.Error($"[AutoRezzer] Could not finish the job change: {ex.Message}");
                }

                return;
            }

            // ── WATCHING THE WORLD IS NOT ACTING ON IT ────────────────────
            //
            // Ahead of everything else the plugin does, but behind the switch, because the switch
            // governs everything. This records nothing but what the game is already showing on
            // screen, changes nothing and casts nothing; it exists because the client publishes
            // phantom job levels through the Crescent's own instance director and nowhere else, so
            // the moment you zone out they are gone.
            try
            {
                RememberPhantomJobs();
            }
            catch (Exception ex)
            {
                log.Debug($"[AutoRezzer] Could not record phantom job levels: {ex.Message}");
            }

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

        /// <summary>
        /// Writes down the phantom job levels while the game is willing to say what they are.
        ///
        /// ── THE CLIENT ONLY KNOWS THIS INSIDE THE CRESCENT ────────────────
        /// Not a guess: SupportJobLevels exists in exactly one structure in the whole client,
        /// OccultCrescentState, reached only through the Crescent's instance director. That director
        /// is created on entering the zone and destroyed on leaving, so from anywhere else in the
        /// game there is genuinely nothing to read. UIModule's MKDSupportJobModule sounds like the
        /// answer and is not: it stores which action each phantom job has on its bar, not levels.
        ///
        /// So the levels are copied out on the way past, and read back from the config whenever the
        /// live ones are absent. A remembered level cannot drift far: it is refreshed every tick in
        /// the zone, and the only way to raise one is to be in there raising it.
        /// </summary>
        private void RememberPhantomJobs()
        {
            if (!phantom.InOccultCrescent)
                return;

            bool learned = phantom.RememberLevels();

            if (!config.SeenOccultCrescent)
            {
                config.SeenOccultCrescent = true;
                learned = true;
            }

            // Only written when something actually changed. Otherwise this is a config file save
            // five times a second for as long as somebody is in the zone.
            if (learned)
                pluginInterface.SavePluginConfig(config);
        }

        private void Tick()
        {
            if (!clientState.IsLoggedIn || objectTable.LocalPlayer == null)
                return;

            // Noticed once, remembered forever: the window has no other way to know this content
            // exists for this character when they are standing anywhere else in the game.
            // Repainted from the same answer the window draws, and only when it changes. Computed
            // before the gate below, or the bar would freeze on whatever it said last as soon as
            // the plugin stopped ticking past this point.
            var wantBar = BarStateNow();
            if (wantBar != barState)
            {
                barState = wantBar;
                RefreshDtr();
            }

            // ── ALLOWED TO ACT AT ALL? ──
            //
            // The switch and the two zone rules, answered as one question, because everything below
            // this line behaves identically whichever of them said no.
            bool active = config.Enabled && StandDownReason().Length == 0;

            if (active != wasActive)
            {
                wasActive = active;

                // The EDGE, not the state. Aborting a gearset trip every tick would re-enter
                // Returning forever and the trip would never finish.
                if (!active)
                    StandDown();
            }

            if (!active)
            {
                // Driven home first. Being switched off, or having walked into a sanctuary, is not
                // a reason to leave somebody wearing a healer's gearset or levelling a phantom job
                // they did not pick; both machines are told nobody needs raising, which walks them
                // through Returning.
                DriveSwitchersHome();
                rsrManager.Resume();

                // ── ACCEPTING SURVIVES THE CONTENT SWITCHES ───────────────
                //
                // The four switches exist to stop the plugin INTERFERING: pausing a rotation,
                // taking somebody's gear off, choosing targets. Answering the prompt that appears
                // on your own corpse interferes with nothing and costs nothing.
                //
                // Gating it too produced a trap with no way out: a Warrior with dungeons switched
                // off would silently stop accepting rezzes in a dungeon, and the setting that
                // caused it is one this window now hides from them, because no job they own can
                // cast a rez.
                //
                // The MASTER switch is a different matter and has already had its say: nothing
                // reaches this line while it is off.
                //
                // PvP is the other exception, and the only gate that reaches down this far. The
                // argument above is that accepting costs nothing - which holds everywhere a raise
                // can be cast on you, and PvP is precisely the place one cannot be. There is no
                // prompt in there to answer, so this is the plugin going properly quiet rather
                // than politely idling.
                if (!zone.InPvP)
                    _ = accepter.Tick();

                return;
            }

            // Ours first. A corpse cannot raise anybody, so if we are the one down there is nothing
            // else this tick could usefully do.
            if (accepter.Tick())
                return;

            // ── NO ROUTE TO A REZ MEANS NO SWEEP ──
            //
            // Asked BEFORE the object table is walked, not after. A Gunbreaker with no rez gearset
            // and no phantom job cannot pick anybody up, will not be able to in a minute, and
            // sweeping thirty yalms of players to establish that is work done to reach a conclusion
            // already known. Out of MP with no free phantom rez is the same dead end.
            //
            // It is also what stops the window claiming to watch something it is not watching for.
            if (!status.AnyRouteToRez())
            {
                targeting.Reset();
                executor.ClearHold();
                rsrManager.Resume();

                // The one thing worth doing about an empty bar: refill it. Off-GCD, so this happens
                // with the rotation still running and nothing interrupted. See TryRestoreMp.
                _ = executor.TryRestoreMp();
                return;
            }

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
                // Nobody down. The rotation goes back, and so does the window's explanation of why
                // it was holding - it was about a body that is no longer there.
                executor.ClearHold();
                rsrManager.Resume();
                return;
            }

            // ── The ordinary path: already on a job that can do this ──
            if (canRaiseNow)
            {
                // ── "CAN I ACTUALLY RAISE THIS INSTANT?" GOES BEFORE Pause(), NOT AFTER ──
                //
                // The whole plugin turns on asking this in the right order. Pausing Rotation Solver
                // and THEN discovering the cast cannot go out costs the player their rotation and
                // leaves the body on the floor - strictly worse than never having got involved,
                // because at least a plugin that does nothing leaves you fighting.
                //
                // It used to ask about MP alone, so the MP case was the only one that behaved: "only
                // rez with Swiftcast" with Swiftcast on a fifty second cooldown paused the rotation
                // and then stood there for the whole fifty seconds. Every reason the cast could not
                // go out is asked here now, in one question, before anybody is interrupted.
                if (!executor.CanRaiseThisInstant(raiseAction))
                {
                    // ── TWO WAYS OUT OF A HOLD, BOTH TRIED BEFORE SETTLING IN TO WAIT ──
                    //
                    // A phantom raise is instant and free, so it is the answer to nearly every
                    // reason the ordinary one is unavailable - no MP to be short of, no Swiftcast
                    // to wait for.
                    if (BeginPhantomSwap())
                        return;

                    // And Red Mage is the answer the "only rez with Swiftcast" hold was always
                    // meant to have: Dualcast is instant WITHOUT spending Swiftcast, so a sixty
                    // second cooldown stops being the thing standing between a body and a raise.
                    // That is the entire point of the setting, and the readiness check above would
                    // otherwise have quietly locked it out of the one case it exists for.
                    //
                    // Only worth the trip if the MP for it is there. Changing job in order to be
                    // short of MP as a Red Mage instead is not an escape from anything - and Red
                    // Mage's bill is higher, because it buys Dualcast with a Vercure first.
                    if (executor.HasMpFor(RezIds.Verraise) && BeginRedMageDetour())
                        return;

                    // HOLDING. Not paused, not switching, not casting - watching, with the player's
                    // rotation left exactly where it was, until the thing being waited for arrives.
                    rsrManager.Resume();
                    _ = executor.TryRestoreMp();
                    return;
                }

                rsrManager.Pause();

                // The Red Mage detour, asked BEFORE the raise rather than after it fails. TryRaise
                // failing is not a signal - it fails for a dozen transient reasons every tick - so
                // the condition it would have to be inferred from is checked directly instead.
                if (BeginRedMageDetour())
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
            //
            // NEITHER OF THESE PAUSES ANYTHING UNLESS THE ERRAND STARTS, which is the bug that made
            // the plugin unusable in a raid: it used to pause Rotation Solver and THEN ask for a
            // gearset change, and the game refuses a gearset change in combat outright - so a
            // Gunbreaker in a savage fight, standing near anybody's corpse, had their rotation
            // switched off for a job swap that was never going to be allowed, until the body was
            // gone. See BeginPhantomSwap for where that decision lives now.
            if (BeginPhantomSwap())
                return;

            if (BeginGearsetSwap())
                return;

            // Nothing this plugin can do about this body from here: wrong job, no way to change it
            // right now, and nothing worth taking anybody's rotation away for.
            rsrManager.Resume();
        }

        // ══════════════════════════════════════════════════════════
        //  STARTING AN ERRAND
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Borrows a raising phantom job if that is both wanted and possible, pausing only if it is.
        /// Returns whether the tick is finished.
        ///
        /// ── PAUSE AFTER THE ERRAND STARTS, WITH ONE EXCEPTION ─────────────
        /// Begin answers "did this actually start" and nothing is interrupted until it says yes.
        /// The exception is the reason the exception exists: a job change is refused while a cast is
        /// in progress, and the cast in progress is Rotation Solver's. Waiting for Begin to succeed
        /// there is a deadlock - RSR is casting, so the change is refused, so nothing pauses RSR, so
        /// it is still casting - and pausing is precisely what breaks it.
        ///
        /// Every OTHER refusal - in combat, mid-settle, wrong zone - is left alone, because pausing
        /// a rotation does not change any of them.
        /// ─────────────────────────────────────────────────────────────────
        /// </summary>
        private bool BeginPhantomSwap()
        {
            if (!WantsPhantomSwap(out int jobRow))
                return false;

            if (phantomSwitcher.Begin(jobRow) || CastingIsTheOnlyBlocker())
            {
                rsrManager.Pause();
                return true;
            }

            return false;
        }

        /// <summary>The same, for the gearset trip. See the note on <see cref="BeginPhantomSwap"/>.</summary>
        private bool BeginGearsetSwap()
        {
            if (config.RezGearsetId < 0 || !switcher.CanSwitchOnceQuiet())
                return false;

            if (switcher.Begin(config.RezGearsetId) || CastingIsTheOnlyBlocker())
            {
                rsrManager.Pause();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Goes and becomes a Red Mage about this, if that is both wanted and possible.
        ///
        /// Reached from two places - once as the escape from a hold, once as the shortcut past a
        /// hardcast - and they arrive in different states, so the pause is here rather than at
        /// either call site. It is idempotent, so the caller that has already paused pays nothing
        /// for it, and the caller that has not is covered.
        /// </summary>
        private bool BeginRedMageDetour()
        {
            if (!WantsRedMageDetour(out int gearset) || !switcher.Begin(gearset))
                return false;

            rsrManager.Pause();
            return true;
        }

        /// <summary>Whether a cast in progress is the last thing standing in the way - the one
        /// blocker on either switcher's list that pausing the rotation actually removes.</summary>
        private bool CastingIsTheOnlyBlocker()
            => condition[ConditionFlag.Casting] || condition[ConditionFlag.Casting87];

        // ══════════════════════════════════════════════════════════
        //  WHERE THE PLUGIN IS ALLOWED TO RUN
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Why the plugin is standing down right now, or an empty string when it is not.
        ///
        /// ── A GATE, NOT A SWITCH ──
        /// Nothing here writes to Enabled, and that is deliberate. "Turn it off in a town and
        /// restore it on the way out" as an actual write is a promise the plugin cannot keep: the
        /// moment it overwrites the switch it has destroyed the thing it was going to restore, and
        /// any manual toggle in between, or a client that closes in town, leaves the wrong answer
        /// saved forever.
        ///
        /// So the switch is left exactly where the user put it and this decides whether it is being
        /// honoured. Somebody who was off stays off; somebody who was on comes back on by itself,
        /// because nothing was ever changed to have to be changed back.
        ///
        /// Returned as a sentence rather than a bool because the window and the server bar both have
        /// to explain a plugin that is switched on and doing nothing, which otherwise looks broken.
        /// </summary>
        private string StandDownReason()
        {
            // PvP first, and above even the sanctuary rule, because it is the only one of these
            // with no setting behind it. Nothing can be raised in there and the attempt costs a
            // match; see Zone.InPvP for why that is not negotiable.
            if (zone.InPvP)
                return "you're in PvP";

            // Sanctuary next, because it cuts across all four content switches: a city is open
            // world, the Crescent's base camp is the Crescent, and the answer is the same in both.
            if (!config.RezInSanctuaries && zone.InSanctuary)
                return "you're in a sanctuary";

            var kind = zone.Kind;
            if (!status.RunsHere(kind))
                return $"{Zone.NameOf(kind)} is switched off";

            return string.Empty;
        }

        /// <summary>
        /// Everything that has to be undone the moment the plugin stops being allowed to act.
        ///
        /// The same list as switching it off by hand, and for the same reason: whatever it had
        /// started, it started, and walking into a sanctuary is not an excuse to leave somebody in a
        /// healer's gearset with their rotation switched off.
        /// </summary>
        private void StandDown()
        {
            executor.ClearHold();
            targeting.Reset();
            switcher.Abort();
            phantomSwitcher.Abort();
            rsrManager.Resume();
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

            // The LOOSE gate: everything the strict one asks except being mid-cast, which is the
            // blocker pausing the rotation removes. Begin still enforces the strict one; this only
            // decides whether the errand is worth interrupting anybody for. See BeginPhantomSwap.
            if (!phantom.CanChangeJobOnceQuiet())
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

            // ── OUT OF COMBAT ONLY, ALWAYS ──
            // The game refuses gearset changes in a fight, so this is not a preference, it is the
            // only window the trip can happen in.
            if (condition[ConditionFlag.InCombat])
                return false;

            // ── NO TOGGLE, BECAUSE EVERY CONDITION BELOW IS ALREADY THE TOGGLE ──
            //
            // Dualcast is the only instant rez in the game that does not spend Swiftcast. This only
            // runs out of combat, only when Swiftcast is genuinely down, only when there is a body,
            // and only when Red Mage is past Verraise with a gearset to get there. By the time all
            // of those are true the answer is yes, and a switch on top of them would exist only to
            // let somebody say no to the better outcome by accident.
            //
            // It also covers the case the hardcast rule quietly does not: with "only in combat" on,
            // that rule stops applying the moment the fight ends, so without this the plugin would
            // happily stand still for eight seconds on a corpse run.

            // A phantom raise is already instant and already free. Swapping to Red Mage to buy an
            // instant cast we are holding for nothing would be pure ceremony.
            if (config.UseOccultRaise && phantom.CanOccultRaise)
                return false;

            var me = objectTable.LocalPlayer;
            if (me == null || me.ClassJob.RowId == RezIds.RedMage)
                return false;

            if (executor.SwiftcastReady())
                return false;

            // Verraise unlocks at 64. Below that the trip buys a gearset change and a Red Mage who
            // still cannot rez, which is worse than the hardcast it was avoiding.
            if (JobSwitcher.LevelOfJob(RezIds.RedMage, dataManager) < RezIds.VerraiseLevel)
                return false;

            // The first Red Mage gearset, always. There is no picker for this any more: owning one
            // set is no choice, and owning three is a choice between three ways to cast the same
            // Verraise.
            gearsetId = JobSwitcher.FirstGearsetForJob(RezIds.RedMage);
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
                {
                    SetEnabled(true);

                    // Said at the moment of switching on, because that is the moment somebody is
                    // watching for something to happen. "On." followed by nothing happening, in a
                    // city, with no explanation, is how a scope rule gets reported as a bug.
                    Echo(BarStateNow() == BarState.Paused ? $"On, but paused: {PauseReason()}" : "On.");
                    break;
                }

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
                // Abort rather than Cancel inside StandDown: switching the plugin off is not a
                // reason to leave somebody in a healer's gearset or on a phantom job they did not
                // pick. The tick keeps running while either is busy specifically so these finish.
                StandDown();
            }

            // Cleared either way, so the next tick sees an edge rather than trusting a flag that
            // was last written under the old switch.
            wasActive = false;

            pluginInterface.SavePluginConfig(config);

            barState = BarStateNow();
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
        /// <summary>
        /// The one reading of "what is this plugin doing", shared by the bar and the window.
        ///
        /// Paused covers both kinds of standing down, because from outside they are the same thing:
        /// the switch is on and nothing is going to happen. A sanctuary, a content switch, a job
        /// with no rez and no way to get one, an empty MP bar. Splitting those into different words
        /// on the bar would be describing the plumbing rather than the state.
        /// </summary>
        private BarState BarStateNow()
        {
            if (!config.Enabled)
                return BarState.Off;

            if (StandDownReason().Length > 0)
                return BarState.Paused;

            return status.AnyRouteToRez() ? BarState.On : BarState.Paused;
        }

        private void RefreshDtr()
        {
            switch (barState)
            {
                case BarState.Off:
                    dtr.Text = "Rez: off";
                    dtr.Tooltip = "AutoRezzer is off. Nothing is rezzing and nothing is being "
                                  + "accepted. Click to start.";
                    return;

                case BarState.Paused:
                    dtr.Text = "Rez: paused";
                    dtr.Tooltip = $"AutoRezzer is on but paused: {PauseReason()} Click to switch it off.";
                    return;

                default:
                    dtr.Text = "Rez: on";
                    dtr.Tooltip = "AutoRezzer is rezzing, and accepting rezzes cast on you. "
                                  + "Click to stop.";
                    return;
            }
        }

        /// <summary>Why the bar says paused, as a sentence for its tooltip.</summary>
        private string PauseReason()
        {
            string gate = StandDownReason();
            if (gate.Length > 0)
                return $"{gate}.";

            return config.AcceptRaise
                ? "nothing here can rez. Still accepting rezzes cast on you."
                : "nothing here can rez.";
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
            fonts.Dispose();
        }
    }
}
