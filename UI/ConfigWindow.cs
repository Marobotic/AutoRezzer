using System;
using System.Numerics;
using AutoRezzer.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace AutoRezzer.UI
{
    /// <summary>
    /// The whole interface: a switch, four numbers, and a line saying what it is currently doing.
    ///
    /// That last line is the important one. A plugin that acts on its own and shows nothing is one
    /// you cannot tell is broken from one that has simply decided not to act - "holding: enemies are
    /// standing on the body" and "this job cannot raise" look identical from the outside otherwise.
    /// </summary>
    internal sealed class ConfigWindow : Window
    {
        private readonly Configuration config;
        private readonly RezTargeting targeting;
        private readonly RezExecutor executor;
        private readonly JobSwitcher switcher;
        private readonly Action<bool> setEnabled;
        private readonly Action save;

        public ConfigWindow(Configuration config, RezTargeting targeting, RezExecutor executor,
            JobSwitcher switcher, Action<bool> setEnabled, Action save)
            : base("AutoRezzer###AutoRezzerConfig")
        {
            this.config = config;
            this.targeting = targeting;
            this.executor = executor;
            this.switcher = switcher;
            this.setEnabled = setEnabled;
            this.save = save;

            Size = new Vector2(440, 520);
            SizeCondition = ImGuiCond.FirstUseEver;
        }

        public override void Draw()
        {
            // Handed to the plugin rather than written here. The checkbox is one of four ways to
            // flip this and the only one that used to forget the server bar existed.
            bool enabled = config.Enabled;
            if (ImGui.Checkbox("Enabled", ref enabled))
                setEnabled(enabled);

            ImGui.TextDisabled("Raises any dead player in range while this is on.");
            ImGui.Separator();

            // ── State ──
            if (config.Enabled)
            {
                string reason = targeting.LastReason;
                if (!string.IsNullOrEmpty(reason))
                    ImGui.TextColored(new Vector4(0.95f, 0.75f, 0.30f, 1f), reason);
                else
                    ImGui.TextColored(new Vector4(0.35f, 0.85f, 0.45f, 1f), "Watching.");

                string phase = switcher.Describe();
                if (!string.IsNullOrEmpty(phase))
                    ImGui.TextColored(new Vector4(0.45f, 0.70f, 0.95f, 1f), phase);

                if (!string.IsNullOrEmpty(executor.LastAction))
                    ImGui.TextDisabled($"Last: {executor.LastAction}");
            }
            else
            {
                ImGui.TextDisabled("Off.");
            }

            ImGui.Separator();

            // ── Safety ──
            float radius = config.EnemyNearBodyYalms;
            if (ImGui.SliderFloat("Enemy distance", ref radius, 0f, 30f, "%.0f yalms"))
            {
                config.EnemyNearBodyYalms = radius;
                save();
            }
            Help("A body with a live enemy closer than this is left alone, so raising somebody does "
                 + "not pull the pack they died in. Zero turns the check off entirely.");

            float delay = config.RaiseDelaySeconds;
            if (ImGui.SliderFloat("Raise delay", ref delay, 0f, 10f, "%.1f s"))
            {
                config.RaiseDelaySeconds = delay;
                save();
            }
            Help("How long somebody has to have been down first. Gives a human healer the chance to "
                 + "get there before the plugin does, and avoids raising into whatever just killed them.");

            ImGui.Separator();

            bool swift = config.UseSwiftcast;
            if (ImGui.Checkbox("Use Swiftcast", ref swift))
            {
                config.UseSwiftcast = swift;
                save();
            }
            Help("Spends Swiftcast to make the raise instant. An 8 second hardcast is the part that "
                 + "gets interrupted.");

            bool vercure = config.UseVercureForDualcast;
            if (ImGui.Checkbox("Red Mage: Vercure for Dualcast", ref vercure))
            {
                config.UseVercureForDualcast = vercure;
                save();
            }
            Help("When Swiftcast is on cooldown, cast a throwaway Vercure on yourself to proc "
                 + "Dualcast, making the following Verraise instant. Red Mage will never hardcast Verraise.");

            bool rsr = config.PauseRotationSolver;
            if (ImGui.Checkbox("Pause Rotation Solver Reborn while raising", ref rsr))
            {
                config.PauseRotationSolver = rsr;
                save();
            }
            Help("Temporarily pauses Rotation Solver Reborn (/rotation Off) while AutoRezzer is "
                 + "preparing or executing a raise, and resumes it (/rotation Auto) once finished. "
                 + "This gives AutoRezzer priority over combat rotations.");

            bool strangers = config.RaiseStrangers;
            if (ImGui.Checkbox("Raise people outside my party", ref strangers))
            {
                config.RaiseStrangers = strangers;
                save();
            }

            bool brink = config.SkipBrinkOfDeath;
            if (ImGui.Checkbox("Skip people with Brink of Death", ref brink))
            {
                config.SkipBrinkOfDeath = brink;
                save();
            }

            bool chatty = config.Chatty;
            if (ImGui.Checkbox("Announce in chat", ref chatty))
            {
                config.Chatty = chatty;
                save();
            }

            ImGui.Separator();

            // ── Being raised, rather than raising ──
            bool accept = config.AcceptRaise;
            if (ImGui.Checkbox("Accept raises cast on me", ref accept))
            {
                config.AcceptRaise = accept;
                save();
            }
            Help("Answers the raise prompt for you when you are the one who died. Works on every "
                 + "job, and works even with the switch at the top turned off - being raised has "
                 + "nothing to do with being able to raise.");

            ImGui.Separator();

            // ── Changing job to do it ──
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

            // ── Local Statistics ──
            ImGui.TextUnformatted("Statistics");
            ImGui.TextDisabled("Recorded locally on this client.");

            int totalCast = config.TotalRaisesCast;
            int totalAccepted = config.AcceptedRaisesCount;
            int totalAll = totalCast + totalAccepted;

            ImGui.Text($"Total Rezzes Handled: {totalAll}");
            ImGui.BulletText($"Rezzes Cast: {totalCast}");
            ImGui.BulletText($"Rezzes Accepted: {totalAccepted}");

            if (ImGui.TreeNode("Rezzes per Class"))
            {
                uint[] knownRaisingJobs = new uint[]
                {
                    RezIds.WhiteMage,
                    RezIds.RedMage,
                    RezIds.Sage,
                    RezIds.Scholar,
                    RezIds.Astrologian,
                    RezIds.Summoner,
                    RezIds.Conjurer,
                    RezIds.Arcanist,
                };

                foreach (var jobId in knownRaisingJobs)
                {
                    int count = config.RaisesCastByJob.TryGetValue(jobId, out var c) ? c : 0;
                    ImGui.BulletText($"{RezIds.JobName(jobId)}: {count}");
                }

                foreach (var (jobId, count) in config.RaisesCastByJob)
                {
                    if (Array.IndexOf(knownRaisingJobs, jobId) < 0)
                        ImGui.BulletText($"{RezIds.JobName(jobId)}: {count}");
                }

                ImGui.TreePop();
            }

            if (ImGui.Button("Reset Statistics"))
            {
                config.ResetStats();
                save();
            }
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
