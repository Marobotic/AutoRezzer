using System;
using System.Numerics;
using System.Text;
using AutoRezzer.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AutoRezzer.UI
{
    /// <summary>
    /// The whole interface: a switch, a sentence describing the room, and three tabs of settings.
    ///
    /// That sentence is the important part. A plugin that acts on its own and shows nothing is one
    /// you cannot tell is broken from one that has simply decided not to act - "holding: enemies are
    /// standing on the body" and "this job cannot raise" look identical from the outside otherwise.
    /// </summary>
    internal sealed class ConfigWindow : Window
    {
        private static readonly Vector4 Green = new(0.35f, 0.85f, 0.45f, 1f);
        private static readonly Vector4 Amber = new(0.95f, 0.75f, 0.30f, 1f);
        private static readonly Vector4 Blue = new(0.45f, 0.70f, 0.95f, 1f);
        private static readonly Vector4 Red = new(0.95f, 0.45f, 0.40f, 1f);

        /// <summary>Where Rotation Solver Reborn comes from. Shown, and copyable, because a plugin
        /// that needs another plugin and does not say where to get it is a support request.</summary>
        private const string RsrRepoUrl =
            "https://raw.githubusercontent.com/FFXIV-CombatReborn/CombatRebornRepo/main/pluginmaster.json";

        private readonly Configuration config;
        private readonly RezTargeting targeting;
        private readonly RezExecutor executor;
        private readonly JobSwitcher switcher;
        private readonly RsrManager rsr;
        private readonly PhantomJobs phantom;
        private readonly PhantomJobSwitcher phantomSwitcher;
        private readonly Action<bool> setEnabled;
        private readonly Action save;

        /// <summary>
        /// The phantom job to offer a way back to after a MANUAL switch.
        ///
        /// The automatic errand keeps its own copy and gives it back on its own; this is for the
        /// button presses, which have no state machine behind them. Without it, pressing "Phantom
        /// Chemist" to pick one person up is a one-way door and you have to remember what you were
        /// levelling yourself.
        /// </summary>
        private int manualReturnJob = -1;

        public ConfigWindow(Configuration config, RezTargeting targeting, RezExecutor executor,
            JobSwitcher switcher, RsrManager rsr, PhantomJobs phantom,
            PhantomJobSwitcher phantomSwitcher, Action<bool> setEnabled, Action save)
            : base("AutoRezzer###AutoRezzerConfig")
        {
            this.config = config;
            this.targeting = targeting;
            this.executor = executor;
            this.switcher = switcher;
            this.rsr = rsr;
            this.phantom = phantom;
            this.phantomSwitcher = phantomSwitcher;
            this.setEnabled = setEnabled;
            this.save = save;

            Size = new Vector2(460, 540);
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        public override void Draw()
        {
            // Handed to the plugin rather than written here. The checkbox is one of four ways to
            // flip this and the only one that used to forget the server bar existed.
            bool enabled = config.Enabled;
            if (ImGui.Checkbox("Enabled", ref enabled))
                setEnabled(enabled);

            ImGui.TextDisabled("Raises any dead player in range, and accepts raises cast on you.");

            DrawStatus();
            ImGui.Separator();

            if (!ImGui.BeginTabBar("##autorezzer"))
                return;

            if (ImGui.BeginTabItem("Raising"))
            {
                DrawRaisingTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Occult Crescent"))
            {
                DrawOccultTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Job switching"))
            {
                DrawJobTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Integration"))
            {
                DrawIntegrationTab();
                ImGui.EndTabItem();
            }

            if (ImGui.BeginTabItem("Statistics"))
            {
                DrawStatsTab();
                ImGui.EndTabItem();
            }

            ImGui.EndTabBar();
        }

        // ══════════════════════════════════════════════════════════
        //  STATUS
        // ══════════════════════════════════════════════════════════

        private void DrawStatus()
        {
            if (!config.Enabled)
            {
                ImGui.TextDisabled("Off. Nothing is raising, and nothing is being accepted.");
                return;
            }

            if (config.UseOccultRaise && phantom.CanOccultRaise)
            {
                string spell = phantom.CurrentRaiseAction == RezIds.Revive ? "Revive" : "Occult Raise";
                ImGui.TextColored(Blue, $"{spell}: instant, free, 5s.");
            }

            ImGui.TextColored(Green, DescribeRoom());

            string reason = targeting.LastReason;
            if (!string.IsNullOrEmpty(reason))
                ImGui.TextColored(Amber, reason);

            string phase = switcher.Describe();
            if (string.IsNullOrEmpty(phase))
                phase = phantomSwitcher.Describe();

            if (!string.IsNullOrEmpty(phase))
                ImGui.TextColored(Blue, phase);

            if (!string.IsNullOrEmpty(executor.LastAction))
                ImGui.TextDisabled($"Last: {executor.LastAction}");
        }

        /// <summary>
        /// The room, in one sentence.
        ///
        /// It used to say "Watching." - accurate, and reads like something looking through a window
        /// at your neighbours. This says the same thing by describing what is actually out there,
        /// which has the side effect of being far more useful: "12 players around, all alive" and
        /// "12 players around, 3 dead already, 2 rezzed, raising Someone" are the difference between
        /// a quiet zone and a wipe recovery, and both used to render as the same word.
        /// </summary>
        private string DescribeRoom()
        {
            var seen = targeting.LastSurroundings;

            if (seen.Players == 0)
                return "Solo gaming.";

            var line = new StringBuilder();
            line.Append(seen.Players == 1 ? "1 player around" : $"{seen.Players} players around");

            if (seen.Dead == 0)
            {
                line.Append(", all alive");
            }
            else
            {
                line.Append(seen.Dead == 1 ? ", 1 dead already" : $", {seen.Dead} dead already");
                if (seen.Raised > 0)
                    line.Append(seen.Raised == 1 ? ", 1 rezzed" : $", {seen.Raised} rezzed");
            }

            string doing = DescribeActivity();
            if (!string.IsNullOrEmpty(doing))
                line.Append(", ").Append(doing);

            return line.Append('.').ToString();
        }

        private string DescribeActivity()
        {
            if (phantomSwitcher.Busy)
                return phantomSwitcher.ShortPhase;

            if (switcher.Busy)
                return switcher.ShortPhase;

            string target = targeting.LastTargetName;
            return string.IsNullOrEmpty(target) ? string.Empty : $"rezzing {target}";
        }

        // ══════════════════════════════════════════════════════════
        //  RAISING
        // ══════════════════════════════════════════════════════════

        private void DrawRaisingTab()
        {
            float radius = config.EnemyNearBodyYalms;
            if (ImGui.SliderFloat("Enemy distance", ref radius, 0f, 30f, "%.0f yalms"))
            {
                config.EnemyNearBodyYalms = radius;
                save();
            }
            Help("A body with a live enemy closer than this is left alone, so raising somebody does "
                 + "not pull the pack they died in. Zero turns the check off entirely, which is the "
                 + "default: in Occult Crescent and on a FATE train there is nearly always something "
                 + "alive nearby, and a radius big enough to matter is a radius that raises nobody.");

            float delay = config.RaiseDelaySeconds;
            if (ImGui.SliderFloat("Raise delay", ref delay, 0f, 10f, "%.1f s"))
            {
                config.RaiseDelaySeconds = delay;
                save();
            }
            Help("How long somebody has to have been down first. Zero by default. Turn it up in a "
                 + "party with its own healer, where the polite thing is to let them get there first.");

            int minMp = config.MinimumMpForRaise;
            if (ImGui.SliderInt("Minimum MP", ref minMp,
                    Configuration.MinimumMpFloor, Configuration.MinimumMpCeiling, "%d MP"))
            {
                // Clamped as well as bounded: ctrl-clicking an ImGui slider lets you type a number
                // straight past its own range.
                config.MinimumMpForRaise = Math.Clamp(
                    minMp, Configuration.MinimumMpFloor, Configuration.MinimumMpCeiling);
                save();
            }
            Help("Below this, no raise is attempted - and Rotation Solver Reborn is left running "
                 + "rather than paused, because stopping your rotation for a raise that was never "
                 + "going to happen is worse than not raising.\n\n"
                 + "It will not go below 2400, the cost of one raise. Lower is not a gentler "
                 + "setting, it is a broken one: the game refuses the cast below the cost, so the "
                 + "plugin would keep interrupting your rotation to attempt a spell it cannot "
                 + "afford.\n\n"
                 + "A raise costs 2400 MP on every job that has one. Red Mage needing Dualcast pays "
                 + "Vercure's 500 on top, so its real floor is 2900 - added for you rather than "
                 + "being a second setting.\n\n"
                 + "Occult Raise is free and ignores this entirely.");

            ImGui.Separator();

            bool swift = config.UseSwiftcast;
            if (ImGui.Checkbox("Use Swiftcast", ref swift))
            {
                config.UseSwiftcast = swift;
                save();
            }
            Help("Spends Swiftcast to make the raise instant. An 8 second hardcast is the part that "
                 + "gets interrupted.");

            bool swiftOnly = config.SwiftcastOnly;
            if (ImGui.Checkbox("Only rez with Swiftcast", ref swiftOnly))
            {
                config.SwiftcastOnly = swiftOnly;
                save();
            }
            Help("Never hardcast. With Swiftcast on cooldown the plugin waits rather than standing "
                 + "still for eight seconds.\n\n"
                 + "Applies to every job except Red Mage, which is already incapable of hardcasting "
                 + "a raise here - it goes through Dualcast instead.");

            if (!config.SwiftcastOnly)
                ImGui.BeginDisabled();

            ImGui.Indent();
            bool inCombatOnly = config.SwiftcastOnlyInCombat;
            if (ImGui.Checkbox("...in combat only", ref inCombatOnly))
            {
                config.SwiftcastOnlyInCombat = inCombatOnly;
                save();
            }
            Help("Narrows the rule above to combat. The hardcast is dangerous because something is "
                 + "hitting you; out of combat - the corpse-run case - eight seconds is merely eight "
                 + "seconds, so the raise goes out anyway.");
            ImGui.Unindent();

            if (!config.SwiftcastOnly)
                ImGui.EndDisabled();

            ImGui.Separator();

            bool strangers = config.RaiseStrangers;
            if (ImGui.Checkbox("Raise people outside my party", ref strangers))
            {
                config.RaiseStrangers = strangers;
                save();
            }

            bool echo = config.AnnounceInEcho;
            if (ImGui.Checkbox("Announce in echo", ref echo))
            {
                config.AnnounceInEcho = echo;
                save();
            }
            Help("One line per raise, in your own echo channel. Echo cannot leave your client, so "
                 + "nothing here is ever visible to the party.");

            ImGui.Separator();

            // ── Being raised, rather than raising ──
            bool accept = config.AcceptRaise;
            if (ImGui.Checkbox("Accept raises cast on me", ref accept))
            {
                config.AcceptRaise = accept;
                save();
            }
            Help("Answers the raise prompt for you when you are the one who died. Works on every "
                 + "job, including ones that could never cast a raise themselves - being raised has "
                 + "nothing to do with being able to raise.\n\n"
                 + "Like everything else here, it only applies while the switch at the top is on.");
        }

        // ══════════════════════════════════════════════════════════
        //  OCCULT CRESCENT
        // ══════════════════════════════════════════════════════════

        private void DrawOccultTab()
        {
            bool use = config.UseOccultRaise;
            if (ImGui.Checkbox("Use phantom raises when available", ref use))
            {
                config.UseOccultRaise = use;
                save();
            }
            Help("Inside Occult Crescent, raise with the phantom job's own raise instead of "
                 + "anything else:\n\n"
                 + "  Phantom White Mage - Occult Raise, from phantom level 4\n"
                 + "  Phantom Chemist - Revive, from phantom level 3\n\n"
                 + "Both are instant, cost no MP and come back every five seconds, so Swiftcast, the "
                 + "MP floor, the hardcast rules and the gearset switching are all answers to "
                 + "problems they do not have, and all of them are skipped. Neither cares what job "
                 + "you are on - a Warrior raises.");

            if (!config.UseOccultRaise)
                ImGui.BeginDisabled();

            string[] raisers = { "Automatic", "Phantom White Mage", "Phantom Chemist" };
            int pref = (int)config.PreferredPhantomRaiser;
            if (pref < 0 || pref >= raisers.Length)
                pref = 0;

            if (ImGui.Combo("Prefer", ref pref, raisers, raisers.Length))
            {
                config.PreferredPhantomRaiser = (PhantomRaiser)pref;
                save();
            }
            Help("Which to use when both are levelled far enough. The two raises are identical in "
                 + "every way that matters, so this is really about which job's other four actions "
                 + "you would rather be holding.\n\n"
                 + "Automatic keeps whichever is already equipped and never swaps for the sake of "
                 + "it. If the one you pick is not levelled, the other is used rather than nothing.");

            if (!config.UseOccultRaise)
                ImGui.EndDisabled();

            ImGui.Separator();
            DrawOccultConditions();
        }

        private void DrawOccultConditions()
        {
            // Shown as a checklist rather than one verdict on purpose. "Phantom raise unavailable"
            // tells somebody standing in South Horn nothing; "you are on Phantom Cannoneer" tells
            // them exactly which of the three to go and fix.
            ImGui.TextUnformatted("Conditions");

            bool inOc = phantom.InOccultCrescent;
            Condition(inOc, inOc ? "In Occult Crescent." : "Not in Occult Crescent.");

            if (!inOc)
            {
                ImGui.Spacing();
                ImGui.TextDisabled("Everything below is only knowable inside the zone.");
                return;
            }

            int row = phantom.CurrentJobRow;
            bool canNow = phantom.CanOccultRaise;
            Condition(canNow, canNow
                ? $"On {phantom.CurrentJobName}, level {phantom.CurrentJobLevel}."
                : $"On {phantom.CurrentJobName}, which cannot raise.");

            // Both raisers listed whether or not they are the one equipped: the question this
            // answers is "could I", and the swap and the buttons below both depend on it.
            ImGui.Spacing();
            ImGui.TextUnformatted("Phantom jobs that can raise");

            foreach (byte raiser in RezIds.PhantomRaisers)
            {
                int level = phantom.LevelOf(raiser);
                int need = RezIds.PhantomRaiseLevelFor(raiser);
                bool ready = phantom.CanRaiseAs(raiser);
                string spell = RezIds.PhantomRaiseActionFor(raiser) == RezIds.Revive ? "Revive" : "Occult Raise";

                Condition(ready, ready
                    ? $"{phantom.NameOf(raiser)} - level {level}, has {spell}."
                    : $"{phantom.NameOf(raiser)} - level {level}, {spell} needs {need}.");
            }

            ImGui.Spacing();

            if (canNow)
            {
                ImGui.TextColored(Green, config.UseOccultRaise
                    ? "Raising with the phantom job. Other raise settings do not apply."
                    : "Available, but the switch above is off.");
            }
            else if (phantom.AnyRaiserAvailable)
            {
                ImGui.TextColored(Amber, "Falling back to the normal raise settings.");
            }
            else
            {
                ImGui.TextColored(Amber, "No phantom job is levelled far enough to raise.");
            }

            ImGui.Separator();
            DrawPhantomSwap();
        }

        private void DrawPhantomSwap()
        {
            ImGui.TextUnformatted("Borrowing a raising job");

            bool swap = config.SwapPhantomJobToRaise;
            if (ImGui.Checkbox("Change phantom job to raise, out of combat", ref swap))
            {
                config.SwapPhantomJobToRaise = swap;
                save();
            }
            Help("When somebody is down and the phantom job you are on cannot raise, change to one "
                 + "that can, raise, and change back.\n\n"
                 + "OFF by default and deliberately so: the phantom job is the thing you are "
                 + "levelling, and being taken off it mid-grind is a decision you should make rather "
                 + "than discover. The buttons below are there for the one-off case.\n\n"
                 + "Out of combat only - the game refuses the change in combat, and for about four "
                 + "seconds after it ends.");

            if (!config.SwapPhantomJobToRaise)
                ImGui.BeginDisabled();

            ImGui.Indent();
            bool back = config.SwapPhantomJobBack;
            if (ImGui.Checkbox("Change back afterwards", ref back))
            {
                config.SwapPhantomJobBack = back;
                save();
            }
            Help("Returns you to the phantom job you were levelling once the raise lands, or once "
                 + "the attempt gives up. Off leaves you on the raising job.");
            ImGui.Unindent();

            if (!config.SwapPhantomJobToRaise)
                ImGui.EndDisabled();

            ImGui.Spacing();

            // ── The manual buttons ──
            //
            // Separate from the toggle above, and the reason they exist: somebody who does not want
            // an automatic swap still wants a one-press way to put the right job on when they can
            // see the plugin is on the wrong one.
            ImGui.TextDisabled("Change phantom job now:");

            bool blocked = !phantom.CanChangeJobNow();

            foreach (byte raiser in RezIds.PhantomRaisers)
            {
                bool isCurrent = phantom.CurrentJobRow == raiser;
                bool can = phantom.CanRaiseAs(raiser);
                bool disabled = blocked || isCurrent || !can || phantomSwitcher.Busy;

                if (disabled)
                    ImGui.BeginDisabled();

                if (ImGui.Button($"{phantom.NameOf(raiser)}##switch{raiser}"))
                {
                    // The player has taken the wheel. Cancel rather than Abort, so an errand in
                    // flight does not immediately undo the choice they just made by hand.
                    int from = phantom.CurrentJobRow;
                    phantomSwitcher.Cancel();

                    if (phantom.TryChangeJob(raiser) && from >= 0 && from != raiser)
                        manualReturnJob = from;
                }

                if (disabled)
                    ImGui.EndDisabled();

                ImGui.SameLine();

                if (isCurrent)
                    ImGui.TextDisabled("(current)");
                else if (!can)
                    ImGui.TextDisabled($"(needs level {RezIds.PhantomRaiseLevelFor(raiser)})");
                else if (phantomSwitcher.Busy)
                    ImGui.TextDisabled("(busy)");
                else if (blocked)
                    ImGui.TextDisabled("(not right now)");
                else
                    ImGui.TextDisabled(string.Empty);
            }

            if (blocked && phantom.InOccultCrescent)
            {
                ImGui.TextDisabled("A phantom job change is refused in combat, and for a few "
                                   + "seconds after it ends.");
            }

            // Offer the way back too, so a manual swap is not a one-way door. The errand's own
            // copy wins while one is running, since that is the more recent intent.
            int home = phantomSwitcher.Busy ? phantomSwitcher.ReturnToJob : manualReturnJob;

            if (home >= 0 && phantom.CurrentJobRow == home)
            {
                // Home by whatever route. Stop offering it.
                manualReturnJob = -1;
            }
            else if (home >= 0)
            {
                ImGui.Spacing();

                bool cannot = blocked || phantomSwitcher.Busy;
                if (cannot)
                    ImGui.BeginDisabled();

                if (ImGui.Button($"Back to {phantom.NameOf(home)}##phantomhome"))
                {
                    phantomSwitcher.Cancel();
                    if (phantom.TryChangeJob(home))
                        manualReturnJob = -1;
                }

                if (cannot)
                    ImGui.EndDisabled();
            }
        }

        /// <summary>One line of the condition checklist, ticked or crossed.</summary>
        private static void Condition(bool met, string text)
        {
            ImGui.TextColored(met ? Green : Amber, met ? "  [x]" : "  [ ]");
            ImGui.SameLine();
            ImGui.TextUnformatted(text);
        }

        // ══════════════════════════════════════════════════════════
        //  JOB SWITCHING
        // ══════════════════════════════════════════════════════════

        private void DrawJobTab()
        {
            if (config.UseOccultRaise && phantom.CanOccultRaise)
            {
                ImGui.TextColored(Blue, "Occult Raise is active - none of this is being used.");
                ImGui.Separator();
            }

            ImGui.TextUnformatted("Raise on another job");
            ImGui.TextDisabled("Switches to this gearset when someone dies, raises, then switches back.");

            var sets = JobSwitcher.RaiseCapableGearsets();
            string current = "Never switch job";
            foreach (var (id, name, job) in sets)
            {
                if (id == config.RezGearsetId)
                    current = $"{RezIds.JobName(job)} - {name}";
            }

            if (ImGui.BeginCombo("Gearset", current))
            {
                if (ImGui.Selectable("Never switch job", config.RezGearsetId < 0))
                {
                    config.RezGearsetId = -1;
                    save();
                }

                foreach (var (id, name, job) in sets)
                {
                    if (ImGui.Selectable($"{RezIds.JobName(job)} - {name}##gs{id}", id == config.RezGearsetId))
                    {
                        config.RezGearsetId = id;
                        save();
                    }
                }

                ImGui.EndCombo();
            }
            Help("A gearset rather than a job, because equipping a gearset is the only way the game "
                 + "changes job - and picking the job alone would mean guessing which of your sets "
                 + "was meant. Only gearsets on jobs that can raise are listed.\n\n"
                 + "Job changes are impossible in combat, so this only happens once the fight is over "
                 + "- which is the corpse-run case it is for.");

            if (sets.Count == 0)
                ImGui.TextDisabled("No gearsets on a raising job were found.");

            bool back = config.SwitchBackAfter;
            if (ImGui.Checkbox("Switch back afterwards", ref back))
            {
                config.SwitchBackAfter = back;
                save();
            }

            ImGui.Separator();

            // ── The Red Mage detour ──
            ImGui.TextUnformatted("Red Mage, out of combat");

            bool rdm = config.OutOfCombatSwitchToRdm;
            if (ImGui.Checkbox("Switch to Red Mage when Swiftcast is down", ref rdm))
            {
                config.OutOfCombatSwitchToRdm = rdm;
                save();
            }
            Help("Out of combat, with Swiftcast on cooldown, changes to Red Mage and raises through "
                 + "Dualcast - a throwaway Vercure, then an instant Verraise.\n\n"
                 + "Red Mage is the only job in the game with an instant raise that does not cost "
                 + "Swiftcast, so this is the difference between raising now and waiting out a sixty "
                 + "second cooldown. Out of combat only, because the game refuses job changes in a "
                 + "fight.");

            if (!config.OutOfCombatSwitchToRdm)
                ImGui.BeginDisabled();

            var rdmSets = JobSwitcher.GearsetsForJob(RezIds.RedMage);
            int autoPick = JobSwitcher.FirstGearsetForJob(RezIds.RedMage);

            string rdmCurrent = "Automatic";
            foreach (var (id, name) in rdmSets)
            {
                if (id == config.RdmGearsetId)
                    rdmCurrent = name;
            }

            if (ImGui.BeginCombo("Red Mage gearset", rdmCurrent))
            {
                if (ImGui.Selectable("Automatic", config.RdmGearsetId < 0))
                {
                    config.RdmGearsetId = -1;
                    save();
                }

                foreach (var (id, name) in rdmSets)
                {
                    if (ImGui.Selectable($"{name}##rdm{id}", id == config.RdmGearsetId))
                    {
                        config.RdmGearsetId = id;
                        save();
                    }
                }

                ImGui.EndCombo();
            }
            Help("Automatic uses the first Red Mage gearset it finds, which is the right answer for "
                 + "anyone who owns exactly one. Pick explicitly if you have several and one of them "
                 + "is a glamour set.");

            if (rdmSets.Count == 0)
                ImGui.TextColored(Amber, "No Red Mage gearset found - this cannot do anything.");
            else if (config.RdmGearsetId < 0 && autoPick >= 0)
                ImGui.TextDisabled($"Will use gearset {autoPick}.");

            if (!config.OutOfCombatSwitchToRdm)
                ImGui.EndDisabled();
        }

        // ══════════════════════════════════════════════════════════
        //  INTEGRATION
        // ══════════════════════════════════════════════════════════

        private void DrawIntegrationTab()
        {
            ImGui.TextUnformatted("Rotation Solver Reborn");

            if (!rsr.IsRsrInstalled)
            {
                ImGui.TextColored(Red, "Not installed.");
                ImGui.TextWrapped("AutoRezzer expects Rotation Solver Reborn. Without it the plugin "
                                  + "still raises, but nothing stops your rotation spending the GCD "
                                  + "the raise needed. Add the Combat Reborn repository in "
                                  + "/xlsettings -> Experimental -> Custom Plugin Repositories:");

                ImGui.SetNextItemWidth(-1f);
                string url = RsrRepoUrl;
                ImGui.InputText("##rsrrepo", ref url, 256, ImGuiInputTextFlags.ReadOnly);

                if (ImGui.Button("Copy repository URL"))
                    ImGui.SetClipboardText(RsrRepoUrl);
            }
            else
            {
                ImGui.TextColored(Green, "Installed.");
            }

            ImGui.Separator();

            bool pause = config.PauseRotationSolver;
            if (ImGui.Checkbox("Pause Rotation Solver Reborn while raising", ref pause))
            {
                config.PauseRotationSolver = pause;
                save();
            }
            Help("Stops RSR while AutoRezzer is preparing or executing a raise, and puts it back "
                 + "once the raise resolves or the bodies are gone. If RSR was already off when the "
                 + "raise started, it is left off.");

            if (!config.PauseRotationSolver)
                ImGui.BeginDisabled();

            string[] modes = { "Automatic", "Auto", "Manual", "Target Only", "Henched" };
            int mode = (int)config.RsrResume;
            if (mode < 0 || mode >= modes.Length)
                mode = 0;

            if (ImGui.Combo("Resume as", ref mode, modes, modes.Length))
            {
                config.RsrResume = (RsrResumeMode)mode;
                save();
            }
            Help("Which mode RSR is put back into afterwards.\n\n"
                 + "RSR's IPC can be told a mode but cannot be asked which one it is in, so this is "
                 + "a decision made in advance rather than a value read back. Automatic picks "
                 + "Henched when BOCCHI is loaded and Auto otherwise.");

            if (config.RsrResume == RsrResumeMode.AutoDetect)
                ImGui.TextDisabled($"Automatic resolves to: {rsr.ResumeModeName}.");

            if (!config.PauseRotationSolver)
                ImGui.EndDisabled();

            ImGui.Separator();

            // ── BOCCHI ──
            ImGui.TextUnformatted("BOCCHI");

            if (!rsr.IsBocchiInstalled)
            {
                ImGui.TextDisabled("Not installed. Nothing on this tab changes because of it.");
                return;
            }

            ImGui.TextColored(Green, "Installed.");
            ImGui.TextWrapped(
                "Illegal Mode drives Rotation Solver Reborn in Henched - rotation only, no targeting "
                + "- and BOCCHI does not re-send that mode once it has landed. So coming back from a "
                + "raise in Auto would quietly hand RSR its own targeting back, in the middle of "
                + "BOCCHI's run, without BOCCHI ever noticing. Resuming as Henched is what keeps the "
                + "two of them agreeing.");
            ImGui.TextWrapped(
                "AutoRezzer never touches BOCCHI itself. Illegal Mode stays exactly as you left it; "
                + "only RSR is moved, and only while there is a body to pick up.");
        }

        // ══════════════════════════════════════════════════════════
        //  STATISTICS
        // ══════════════════════════════════════════════════════════

        private void DrawStatsTab()
        {
            ImGui.TextDisabled("Recorded locally on this client, and kept.");
            ImGui.Spacing();

            int totalCast = config.TotalRaisesCast;
            int totalAccepted = config.AcceptedRaisesCount;

            ImGui.Text($"Total rezzes handled: {totalCast + totalAccepted}");
            ImGui.BulletText($"Rezzes cast: {totalCast}");
            ImGui.BulletText($"Rezzes accepted: {totalAccepted}");

            ImGui.Spacing();
            ImGui.TextWrapped("Written to disk the moment each one lands, and there is no reset - a "
                              + "running total is only interesting because it has been running the "
                              + "whole time.");
        }

        private static void Help(string text)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(?)");
            if (!ImGui.IsItemHovered())
                return;

            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 22f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }
}
