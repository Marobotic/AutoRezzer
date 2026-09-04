using System;
using System.Numerics;
using AutoRezzer.Core;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;

namespace AutoRezzer.UI
{
    /// <summary>
    /// The whole interface: a card that says what is happening, and four screens of settings.
    ///
    /// ── WHAT THIS REPLACED, AND WHY ───────────────────────────────────────
    /// Five tabs, twenty-two controls and about nine hundred words of help text. Every one of those
    /// words existed for a good reason and the sum of them was unreadable: the Integration tab
    /// opened with three paragraphs on RSR's IPC surface, and the thing a player wanted to know was
    /// whether it worked.
    ///
    /// The cuts were not stylistic. Six settings were DELETED because each had one right answer, one
    /// whole screen became automatic behaviour, and what was left reorganised itself into the shape
    /// a phone's settings app has had for fifteen years: a summary at the top, grouped switches
    /// under it, and the rare things behind a chevron.
    ///
    /// ── THE TOP CARD IS THE POINT ─────────────────────────────────────────
    /// Job, synced level, and the rez this character actually has right now. That card answers "why
    /// isn't it working" before anybody goes looking through settings for the reason, and it is
    /// where three level rules live that used to live in a README nobody read: Verraise needs 64 and
    /// sync takes it away, Swiftcast does not exist until 18, and a Gunbreaker has nothing at all.
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    internal sealed class ConfigWindow : Window
    {
        /// <summary>Which screen is showing. There is no stack: every screen is one level deep, so
        /// "back" always means the root and a stack would be ceremony.</summary>
        private enum Screen
        {
            Main,
            Occult,
            JobSwap,
            Advanced,
            Stats,
        }

        private readonly Configuration config;
        private readonly IDataManager dataManager;
        private readonly PhantomJobs phantom;
        private readonly PhantomJobSwitcher phantomSwitcher;
        private readonly StatusReporter status;
        private readonly Fonts fonts;
        private readonly Icons icons;
        private readonly Ios ui;
        private readonly Action<bool> setEnabled;
        private readonly Action save;

        private Screen screen = Screen.Main;

        /// <summary>Where the support button goes. The same one as every other plugin of mine, so
        /// somebody who has been there once is not being asked twice.</summary>
        private const string KofiUrl = "https://ko-fi.com/marobotic";

        private const string KofiLabel = "Support me";

        /// <summary>
        /// The running version, read from the assembly rather than written here.
        ///
        /// A version number typed into a UI string is a version number that is wrong the first time
        /// somebody forgets to change it, and the one place it matters is a bug report.
        /// </summary>
        private static readonly string Version =
            $"v{System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?"}";

        public ConfigWindow(Configuration config, IDataManager dataManager, PhantomJobs phantom,
            PhantomJobSwitcher phantomSwitcher, StatusReporter status,
            Fonts fonts, Icons icons, Action<bool> setEnabled, Action save)
            : base("AutoRezzer###AutoRezzerConfig")
        {
            this.config = config;
            this.dataManager = dataManager;
            this.phantom = phantom;
            this.phantomSwitcher = phantomSwitcher;
            this.status = status;
            this.fonts = fonts;
            this.icons = icons;
            this.setEnabled = setEnabled;
            this.save = save;

            ui = new Ios(fonts);

            // ── OUR OWN CHROME ────────────────────────────────────────────
            //
            // Dalamud's title bar is a grey strip with a name and an X, drawn in a font and a colour
            // this window does not use anywhere else. On a black card layout it reads as a different
            // program's window with this one pasted inside it.
            //
            // NoTitleBar removes it. ImGui still lets the window be dragged from any empty space in
            // the body, and the header below is mostly empty space, so nothing is lost by it.
            Flags = ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoScrollbar;
            ShowCloseButton = false;

            Size = new Vector2(464f, 680f);
            SizeCondition = ImGuiCond.FirstUseEver;

            // A floor rather than a fixed size. Narrower than this and the two-line rows start
            // truncating labels that have no business being truncated.
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(400f, 400f),
                MaximumSize = new Vector2(720f, 2000f),
            };
        }

        public override void PreDraw()
        {
            // The whole app is black-on-black cards. Dalamud's default window background is a
            // translucent grey that turns every card into mud.
            ImGui.PushStyleColor(ImGuiCol.WindowBg, Ios.ToVec(Ios.Bg));
            ImGui.PushStyleColor(ImGuiCol.ChildBg, Ios.ToVec(Ios.Bg));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(Ios.Px(16f), Ios.Px(14f)));
            ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(0f, 0f));
            ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, Ios.Px(8f));
        }

        public override void PostDraw()
        {
            ImGui.PopStyleVar(3);
            ImGui.PopStyleColor(2);
        }

        public override void Draw()
        {
            ui.NewFrame();

            using (fonts.PushBody())
            {
                try
                {
                    switch (screen)
                    {
                        case Screen.Occult: DrawOccultScreen(); break;
                        case Screen.JobSwap: DrawJobScreen(); break;
                        case Screen.Advanced: DrawAdvancedScreen(); break;
                        case Screen.Stats: DrawStatsScreen(); break;
                        default: DrawMainScreen(); break;
                    }
                }
                finally
                {
                    // A card left open means a draw list left split, which is not one missing
                    // rectangle but corrupted geometry for the entire window. See Ios.AbortCard.
                    ui.AbortCard();
                }
            }

            // Breathing room under the last card, so the final row never sits flush against the
            // window edge on a screen that happens to fit exactly.
            ImGui.Dummy(new Vector2(0f, Ios.Px(14f)));
        }

        // ══════════════════════════════════════════════════════════
        //  MAIN
        // ══════════════════════════════════════════════════════════

        private void DrawMainScreen()
        {
            DrawTitleBar();
            DrawMasterSwitch();

            // Everything past the switch describes a plugin that is running. With it off there is
            // nothing to describe, and nothing to say either: the switch is right there, and it has
            // already said it.
            if (!config.Enabled)
                return;

            DrawHeroCard();

            // ── NOTHING THAT CANNOT APPLY IS SHOWN ────────────────────────
            //
            // A character with no rez on any job it owns has no use for content switches, job
            // swapping, an MP floor or a hardcast rule. Showing them anyway is not neutral: it
            // presents a screen full of controls that cannot change anything, and leaves somebody
            // hunting through them for the reason nothing happens.
            //
            // What is left is the half that still works on every job in the game. Anybody can be
            // picked up off the floor.
            bool canRez = JobSwitcher.CanRezOnAnyJob(dataManager);
            bool showCrescent = config.SeenOccultCrescent || phantom.InOccultCrescent;

            if (canRez)
            {
                // Four places, read as a list under the caption rather than four sentences that
                // each restate the verb. The Crescent is not among them: it is a field operation,
                // and a switch of its own was one switch too many.
                ui.Caption("Rezzing in");
                ui.BeginCard();

                bool world = config.RunInOpenWorld;
                if (ui.SwitchRow("Open world / FATEs", null, ref world))
                {
                    config.RunInOpenWorld = world;
                    save();
                }

                bool instances = config.RunInInstances;
                if (ui.SwitchRow("In duties", null, ref instances))
                {
                    config.RunInInstances = instances;
                    save();
                }

                bool fieldop = config.RunInFieldOperations;
                if (ui.SwitchRow("Field operations", "Eureka, Bozja, Occult Crescent", ref fieldop))
                {
                    config.RunInFieldOperations = fieldop;
                    save();
                }

                bool sanctuary = config.RezInSanctuaries;
                if (ui.SwitchRow("Sanctuaries", null, ref sanctuary))
                {
                    config.RezInSanctuaries = sanctuary;
                    save();
                }

                ui.EndCard();

                // The Crescent screen survives, for phantom jobs. It is no longer a place switch.
                if (showCrescent)
                {
                    ui.Caption("Occult Crescent");
                    ui.BeginCard();

                    if (ui.DrillRow("Phantom jobs", null,
                            config.UseOccultRaise ? "On" : "Off"))
                    {
                        screen = Screen.Occult;
                    }

                    ui.EndCard();
                }
            }

            ui.Caption("Getting rezzed");
            ui.BeginCard();

            bool accept = config.AcceptRaise;
            if (ui.SwitchRow("Accept rezzes on me", null, ref accept))
            {
                config.AcceptRaise = accept;
                save();
            }

            float wait = config.AcceptRaiseDelaySeconds;
            if (ui.StepperRow("Accept rez after", null,
                    ref wait, 0f, Configuration.AcceptDelayCeiling, 1f, Seconds, config.AcceptRaise))
            {
                config.AcceptRaiseDelaySeconds = wait;
                save();
            }

            ui.EndCard();

            if (canRez)
            {
                // The row IS the answer, not a label pointing at one: the job's own icon and name on
                // the left, the chevron on the right. "Swap to a rez job: White Mage" said the same
                // thing twice and left the icon nowhere to go.
                ui.Caption("Rezzing job");
                ui.BeginCard();

                uint rezJob = config.RezGearsetId >= 0
                    ? JobSwitcher.JobOfGearset(config.RezGearsetId)
                    : 0u;

                if (ui.DrillRow(rezJob == 0 ? "None" : RezIds.JobName(rezJob, dataManager),
                        null, string.Empty,
                        rezJob == 0 ? default : icons.Handle(StatusReporter.IconOfJob(rezJob))))
                {
                    screen = Screen.JobSwap;
                }

                ui.EndCard();
            }

            ui.Caption("More");
            ui.BeginCard();

            if (canRez && ui.DrillRow("Advanced", null, string.Empty))
                screen = Screen.Advanced;

            if (ui.DrillRow("Stats", null, $"{config.TotalRaisesCast + config.AcceptedRaisesCount:N0}"))
                screen = Screen.Stats;

            ui.EndCard();

            if (!canRez)
            {
                ui.Footnote("No job on this character can rez, so only accepting is shown. Level a "
                            + "healer or Red Mage and the rest appears.");
            }
        }

        /// <summary>
        /// The window's own title bar: the plugin's icon, its name, and a close button.
        ///
        /// The master switch is NOT here. It moved into a card of its own underneath, because it is
        /// the most consequential control in the plugin and a switch tucked into a title bar reads
        /// like window furniture rather than like the thing that governs everything below it.
        /// </summary>
        private void DrawTitleBar()
        {
            var dl = ImGui.GetWindowDrawList();
            var start = ImGui.GetCursorScreenPos();
            float avail = ImGui.GetContentRegionAvail().X;

            float icon = Ios.Px(26f);
            float close = Ios.Px(26f);

            float titleH, subH;
            using (fonts.PushTitle())
                titleH = ImGui.GetTextLineHeight();
            using (fonts.PushTiny())
                subH = ImGui.GetTextLineHeight();

            float bar = Math.Max(Ios.Px(32f), Math.Max(icon, titleH + Ios.Px(2f) + subH));

            var iconMin = new Vector2(start.X, start.Y + (bar - icon) * 0.5f);
            var own = icons.Own();
            if (!own.IsNull)
                dl.AddImage(own, iconMin, iconMin + new Vector2(icon, icon));

            // ── The close button ──
            var closeMin = new Vector2(start.X + avail - close, start.Y + (bar - close) * 0.5f);

            ImGui.SetCursorScreenPos(closeMin);
            bool closed = ImGui.InvisibleButton("##close", new Vector2(close, close));
            bool closeHot = ImGui.IsItemHovered();

            if (closeHot)
                dl.AddRectFilled(closeMin, closeMin + new Vector2(close, close), Ios.CardRaised, Ios.Px(8f));

            ui.CloseGlyph(dl, closeMin + new Vector2(close * 0.5f, close * 0.5f),
                closeHot ? Ios.Label : Ios.Secondary);

            // ── Ko-fi, to the left of the close button ──
            float kofiH = Ios.Px(32f);
            float kofiW = ui.KofiButtonWidth(KofiLabel);
            var kofiMin = new Vector2(closeMin.X - Ios.Px(8f) - kofiW, start.Y + (bar - kofiH) * 0.5f);

            if (ui.KofiButton(KofiLabel, kofiMin, kofiH))
                Dalamud.Utility.Util.OpenLink(KofiUrl);

            // ── The name, with the version under it, in whatever is left between them ──
            float textLeft = start.X + icon + Ios.Px(11f);
            float textRight = kofiMin.X - Ios.Px(10f);
            float room = Math.Max(0f, textRight - textLeft);

            float versionH;
            using (fonts.PushTiny())
                versionH = ImGui.GetTextLineHeight();

            float nameBlock = titleH + Ios.Px(2f) + versionH;
            float blockY = start.Y + (bar - nameBlock) * 0.5f;

            dl.PushClipRect(new Vector2(textLeft, start.Y),
                new Vector2(Math.Max(textLeft, textRight), start.Y + bar + Ios.Px(Ios.DescenderRoom)), true);

            using (fonts.PushTitle())
                dl.AddText(new Vector2(textLeft, blockY), Ios.Label, Ios.Fit("AutoRezzer", room));

            using (fonts.PushTiny())
            {
                dl.AddText(new Vector2(textLeft, blockY + titleH + Ios.Px(2f)), Ios.Tertiary,
                    Ios.Fit(Version, room));
            }

            dl.PopClipRect();

            // ── The rule under the bar ──
            //
            // The window's own chrome needs the same edge Dalamud's used to give it, or the title
            // and the first card read as one stack of things rather than a frame with contents.
            ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + bar));
            ImGui.Dummy(new Vector2(0f, Ios.Px(14f)));

            float ruleY = ImGui.GetCursorScreenPos().Y;
            dl.AddLine(new Vector2(start.X, ruleY), new Vector2(start.X + avail, ruleY), Ios.Sep, 1f);

            if (closed)
                IsOpen = false;
        }

        /// <summary>
        /// The master switch, in a card of its own at the top of the screen.
        ///
        /// ── OFF MEANS OFF, AND IT MEANS IT EVERYWHERE ─────────────────────
        /// Nothing below this runs while it is off. Not the sweep, not a rez, not accepting one cast
        /// on you, not a single thing done to Rotation Solver. The plugin is inert.
        ///
        /// The one exception is a job change already in flight, which is finished rather than
        /// abandoned. That is not the plugin still working; it is the plugin not leaving somebody
        /// stood in a healer's gearset because they reached for the switch at the wrong moment.
        /// </summary>
        private void DrawMasterSwitch()
        {
            ImGui.Dummy(new Vector2(0f, Ios.Px(18f)));
            ui.BeginCard();

            bool on = config.Enabled;
            if (ui.SwitchRow("Master switch", null, ref on))
                setEnabled(on);

            ui.EndCard();
        }

        /// <summary>
        /// The card at the top: who is rezzing, with what, and what is happening.
        ///
        /// ── ONE ACTIVE, AND IT MOVES ──────────────────────────────────────
        /// Your job and your phantom job are two candidates for the same task, so exactly one of
        /// them is ever marked. The phantom job wins when it has a rez and the setting allows it,
        /// because that is genuinely the order the plugin decides in; otherwise the badge sits on
        /// your class. Reading which line is green is reading the priority.
        ///
        /// Both icons are the same size on purpose. A 40 by 58 pixel character and a 40 by 40 job
        /// emblem drawn at different scales would read as a hierarchy that is not there: they are
        /// alternatives, not a heading and a footnote.
        /// </summary>
        private void DrawHeroCard()
        {
            var r = status.Build();

            ImGui.Dummy(new Vector2(0f, Ios.Px(12f)));

            var dl = ImGui.GetWindowDrawList();
            var top = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            float pad = Ios.Px(16f);

            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);

            ImGui.Dummy(new Vector2(0f, pad));

            bool phantomActive = status.PhantomRezReady();
            bool inCrescent = phantom.InOccultCrescent && phantom.CurrentJobRow >= 0;

            // ── Your job ──
            //
            // The line under the name is suppressed while the phantom job is the one rezzing. It
            // would otherwise be a second claim about readiness sitting next to the badge that
            // already settled it, and worse, a level warning about a class nothing is asking to
            // cast. Active is the answer; this line only explains the job Active is sitting on.
            //
            // Warn still counts as active. "Swiftcast in 34s" is a job that rezzes, in a moment.
            bool classRezzes = r.AbilityTone == Tone.Good || r.AbilityTone == Tone.Warn;

            DrawJobLine(dl, top, width, pad,
                icons.Handle(r.IconId),
                r.JobName,
                phantomActive ? string.Empty : r.Ability,
                ToneColour(r.AbilityTone),
                active: !phantomActive && classRezzes);

            // ── The phantom job you are wearing, inside the zone ──
            if (inCrescent)
            {
                ImGui.Dummy(new Vector2(0f, Ios.Px(12f)));

                int row = phantom.CurrentJobRow;
                DrawJobLine(dl, top, width, pad,
                    icons.PhantomSprite(row),
                    phantom.NameOf(row),
                    string.Empty,
                    Ios.Secondary,
                    active: phantomActive);
            }

            // ── The hairline ──
            ImGui.Dummy(new Vector2(0f, Ios.Px(15f)));
            float sepY = ImGui.GetCursorScreenPos().Y;
            dl.AddLine(new Vector2(top.X, sepY), new Vector2(top.X + width, sepY), Ios.Sep, 1f);
            ImGui.Dummy(new Vector2(0f, Ios.Px(14f)));

            // ── What it is doing ──
            var stateTop = ImGui.GetCursorScreenPos();
            float dot = Ios.Px(9f);

            float stateH, detailH = 0f;
            using (fonts.PushMedium())
                stateH = ImGui.GetTextLineHeight();

            bool hasDetail = !string.IsNullOrEmpty(r.Detail);
            if (hasDetail)
            {
                using (fonts.PushSmall())
                    detailH = ImGui.GetTextLineHeight();
            }

            float stateBlock = stateH + (hasDetail ? Ios.Px(5f) + detailH : 0f);
            float stateLeft = top.X + pad + dot + Ios.Px(10f);
            float stateRight = top.X + width - pad;
            float stateRoom = Math.Max(0f, stateRight - stateLeft);

            dl.AddCircleFilled(new Vector2(top.X + pad + dot * 0.5f, stateTop.Y + stateH * 0.5f),
                dot * 0.5f, ToneColour(r.StateTone), 16);

            dl.PushClipRect(new Vector2(stateLeft, stateTop.Y),
                new Vector2(stateRight, stateTop.Y + stateBlock + Ios.Px(Ios.DescenderRoom)), true);

            using (fonts.PushMedium())
                dl.AddText(new Vector2(stateLeft, stateTop.Y), Ios.Label, Ios.Fit(r.State, stateRoom));

            if (hasDetail)
            {
                using (fonts.PushSmall())
                {
                    dl.AddText(new Vector2(stateLeft, stateTop.Y + stateH + Ios.Px(5f)),
                        Ios.Secondary, Ios.Fit(r.Detail, stateRoom));
                }
            }

            dl.PopClipRect();

            ImGui.Dummy(new Vector2(0f, stateBlock + pad));

            float bottom = ImGui.GetCursorScreenPos().Y;
            dl.ChannelsSetCurrent(0);
            dl.AddRectFilled(top, new Vector2(top.X + width, bottom), Ios.Card, Ios.Px(14f));
            dl.ChannelsMerge();
        }

        /// <summary>
        /// One candidate for the rez: emblem, name, a line under it, and the Active badge.
        ///
        /// Advances the cursor by the emblem's height, so the caller stacks these by adding gaps
        /// between calls rather than by tracking coordinates itself.
        /// </summary>
        private void DrawJobLine(ImDrawListPtr dl, Vector2 top, float width, float pad,
            in Art art, string name, string sub, uint subColour, bool active)
        {
            float emblem = Ios.Px(42f);
            var iconMin = new Vector2(top.X + pad, ImGui.GetCursorScreenPos().Y);

            // The plate is drawn whether or not the art has decoded, so a texture that is not ready
            // yet leaves a tidy empty tile for a frame rather than a hole in the card.
            dl.AddRectFilled(iconMin, iconMin + new Vector2(emblem, emblem), Ios.CardRaised, Ios.Px(11f));

            if (!art.IsNull)
            {
                // Aspect preserved: these are 40 by 58 pixel characters, and a squashed one is
                // instantly obvious in a way a squashed photograph is not.
                float w = art.Aspect >= 1f ? emblem : emblem * art.Aspect;
                float h = art.Aspect >= 1f ? emblem / art.Aspect : emblem;
                var artMin = iconMin + new Vector2((emblem - w) * 0.5f, (emblem - h) * 0.5f);

                dl.AddImage(art.Tex, artMin, artMin + new Vector2(w, h), art.Uv0, art.Uv1);
            }

            float badgeW = 0f;
            Vector2 badgeText = default;
            if (active)
            {
                using (fonts.PushSmall())
                {
                    badgeText = ImGui.CalcTextSize("Active");
                    badgeW = badgeText.X;
                }
            }

            float textLeft = iconMin.X + emblem + Ios.Px(13f);
            float textRight = top.X + width - pad - (badgeW > 0f ? badgeW + Ios.Px(10f) : 0f);
            float room = Math.Max(0f, textRight - textLeft);

            bool hasSub = !string.IsNullOrEmpty(sub);

            float nameH, subH = 0f;
            using (fonts.PushMedium())
                nameH = ImGui.GetTextLineHeight();

            if (hasSub)
            {
                using (fonts.PushSmall())
                    subH = ImGui.GetTextLineHeight();
            }

            float block = nameH + (hasSub ? Ios.Px(5f) + subH : 0f);
            float blockY = iconMin.Y + (emblem - block) * 0.5f;

            dl.PushClipRect(new Vector2(textLeft, iconMin.Y),
                new Vector2(Math.Max(textLeft, textRight), iconMin.Y + emblem + Ios.Px(Ios.DescenderRoom)), true);

            using (fonts.PushMedium())
                dl.AddText(new Vector2(textLeft, blockY), Ios.Label, Ios.Fit(name, room));

            if (hasSub)
            {
                using (fonts.PushSmall())
                {
                    dl.AddText(new Vector2(textLeft, blockY + nameH + Ios.Px(5f)),
                        subColour, Ios.Fit(sub, room));
                }
            }

            dl.PopClipRect();

            // Green text, no pill. The word is doing the work; a filled shape behind it would make
            // it the loudest thing on the card, which it is not.
            if (badgeW > 0f)
            {
                using (fonts.PushSmall())
                {
                    dl.AddText(new Vector2(top.X + width - pad - badgeW,
                        iconMin.Y + (emblem - badgeText.Y) * 0.5f), Ios.Green, "Active");
                }
            }

            ImGui.Dummy(new Vector2(0f, emblem));
        }

        // ══════════════════════════════════════════════════════════
        //  OCCULT CRESCENT
        // ══════════════════════════════════════════════════════════

        private void DrawOccultScreen()
        {
            if (ui.NavBar("Occult Crescent", true))
                screen = Screen.Main;

            ImGui.Dummy(new Vector2(0f, Ios.Px(10f)));
            ui.BeginCard();

            // The first switch owns the screen. Swapping phantom job in order to rez is meaningless
            // when the phantom rez is not the one being used, so everything under it greys out with
            // it rather than sitting there offering to do nothing.
            bool use = config.UseOccultRaise;
            if (ui.SwitchRow("Prefer phantom rez", null, ref use))
            {
                config.UseOccultRaise = use;
                save();
            }

            bool swap = config.SwapPhantomJobToRaise;
            if (ui.SwitchRow("Swap phantom job to rez", null, ref swap, enabled: config.UseOccultRaise))
            {
                config.SwapPhantomJobToRaise = swap;
                save();
            }

            ui.EndCard();

            // ── Which one to borrow ──
            //
            // Only the phantom jobs this character has actually levelled far enough are offered.
            // "Whichever is levelled" was a third option that existed to paper over the other two
            // sometimes being useless; filtering them properly removes the need for it, and one
            // levelled raiser now means one button, already chosen.
            var raisers = new System.Collections.Generic.List<(PhantomRaiser Pref, int Row)>();

            foreach (byte row in RezIds.PhantomRaisers)
            {
                if (!phantom.CanRaiseAs(row))
                    continue;

                raisers.Add((row == RezIds.PhantomWhiteMage ? PhantomRaiser.WhiteMage
                    : PhantomRaiser.Chemist, row));
            }

            if (raisers.Count == 0)
            {
                // ── TWO DIFFERENT NOTHINGS ────────────────────────────────
                //
                // "You have not levelled one" and "I have never been told what you have levelled"
                // look identical from here and mean opposite things. Saying the first when the
                // second is true is the window inventing a fact about somebody's character.
                //
                // The client publishes phantom job levels through the Crescent's own instance
                // director and nowhere else, so until this plugin has stood in the zone once it
                // genuinely does not know, and it should say so rather than guess.
                ui.Footnote(config.SeenOccultCrescent
                    ? "No phantom job is levelled far enough yet. Chemist gets Revive at 3, White "
                      + "Mage gets Occult Raise at 4."
                    : "Your phantom jobs appear here after one visit to the Crescent. The game only "
                      + "reports their levels while you're inside the zone, so they're read once and "
                      + "remembered.");
                return;
            }

            ui.Caption(raisers.Count == 1 ? "Swaps to" : "Swap to");

            // One levelled raiser is not a choice, so it is shown as the answer rather than as a
            // question, and selected on the player's behalf.
            if (raisers.Count == 1 && config.PreferredPhantomRaiser != raisers[0].Pref)
            {
                config.PreferredPhantomRaiser = raisers[0].Pref;
                save();
            }

            float gap = Ios.Px(12f);
            float avail = ImGui.GetContentRegionAvail().X;
            float cardW = raisers.Count == 1 ? avail : (avail - gap) * 0.5f;
            float cardH = Ios.Px(132f);
            var origin = ImGui.GetCursorScreenPos();

            for (int i = 0; i < raisers.Count; i++)
            {
                var (pref, row) = raisers[i];
                var pos = new Vector2(origin.X + (cardW + gap) * i, origin.Y);

                if (ui.SpriteCard(icons.PhantomSprite(row), phantom.NameOf(row),
                        config.PreferredPhantomRaiser == pref, pos, new Vector2(cardW, cardH),
                        config.UseOccultRaise && config.SwapPhantomJobToRaise))
                {
                    config.PreferredPhantomRaiser = pref;
                    save();
                }
            }

            ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + cardH));
        }

        // ══════════════════════════════════════════════════════════
        //  JOB SWAPPING
        // ══════════════════════════════════════════════════════════

        private void DrawJobScreen()
        {
            if (ui.NavBar("Rezzing job", true))
                screen = Screen.Main;

            ui.Caption("Job");
            ui.BeginCard();

            // ── ONE ROW PER JOB, NOT PER GEARSET ─────────────────────────
            //
            // Three White Mage gearsets are one choice presented three times. The question is which
            // job to become; the gearset is how, and the first one found answers it. See
            // JobSwitcher.RezJobChoices for the level filter that goes with it.
            var sets = JobSwitcher.RezJobChoices(dataManager);

            // Picking one is the whole purpose of this screen, so it closes when the job is picked.
            // Leaving somebody on a list with a fresh tick on it, hunting for the back chevron, is
            // the screen refusing to admit it is finished.
            if (ui.SelectRow("Never swap job", null, config.RezGearsetId < 0))
            {
                config.RezGearsetId = -1;
                save();
                screen = Screen.Main;
            }

            // ── RED MAGE FIRST, AND SAID SO ──────────────────────────────
            //
            // It is the only job in the game that rezzes instantly without spending Swiftcast, so on
            // a corpse run it is the difference between going now and waiting out a sixty second
            // cooldown. Every other choice here is a healer that will hardcast or hold. Sorting it
            // to the top and labelling it is the shortest way to say that.
            sets.Sort((a, b) => a.Job == RezIds.RedMage ? -1 : b.Job == RezIds.RedMage ? 1 : 0);

            // The gearset's NAME is not shown, only the job. Most people name a gearset after the
            // job it holds, so the row read "White Mage" twice: once as the answer and once as the
            // same answer in smaller grey text.
            foreach (var (id, _, job) in sets)
            {
                if (ui.SelectRow(RezIds.JobName(job, dataManager),
                        job == RezIds.RedMage ? "Recommended" : null,
                        id == config.RezGearsetId,
                        icons.Handle(StatusReporter.IconOfJob(job))))
                {
                    config.RezGearsetId = id;
                    save();
                    screen = Screen.Main;
                }
            }

            ui.EndCard();

            if (sets.Count == 0)
                ui.Footnote("No gearset on a job that can rez, at a level that can rez.");

            bool swapBack = config.SwitchBackAfter;
            ui.Caption("After the rez");
            ui.BeginCard();
            if (ui.SwitchRow("Swap back after", null, ref swapBack))
            {
                config.SwitchBackAfter = swapBack;
                save();
            }
            ui.EndCard();

            // ── Red Mage, always ──
            //
            // No switch. Dualcast is the only instant rez in the game that does not spend Swiftcast,
            // it only ever fires out of combat, and it only fires when Swiftcast is down and there
            // is a body on the floor. Every one of those conditions is already the answer to "should
            // this happen"; a toggle on top of them would only offer a way to say no to the good
            // outcome. The plugin takes the first Red Mage gearset it finds.
            //
            // The note only appears when it would change something: Red Mage levelled past Verraise
            // and no gearset saved to get there. Telling a Warrior main about Dualcast is noise.
            if (JobSwitcher.LevelOfJob(RezIds.RedMage, dataManager) >= RezIds.VerraiseLevel
                && JobSwitcher.FirstGearsetForJob(RezIds.RedMage) < 0)
            {
                ui.Footnote("Save a Red Mage gearset and it will use Dualcast out of combat when "
                            + "Swiftcast is down.");
            }
        }

        // ══════════════════════════════════════════════════════════
        //  ADVANCED
        // ══════════════════════════════════════════════════════════

        private void DrawAdvancedScreen()
        {
            if (ui.NavBar("Advanced", true))
                screen = Screen.Main;

            ImGui.Dummy(new Vector2(0f, Ios.Px(10f)));
            ui.BeginCard();

            bool nohard = config.SwiftcastOnly;
            if (ui.SwitchRow("Never hardcast rez", null, ref nohard))
            {
                config.SwiftcastOnly = nohard;
                save();
            }

            bool inCombat = config.SwiftcastOnlyInCombat;
            if (ui.SwitchRow("Only in combat", null, ref inCombat,
                    enabled: config.SwiftcastOnly))
            {
                config.SwiftcastOnlyInCombat = inCombat;
                save();
            }

            ui.EndCard();
            ui.Footnote("Your rotation keeps running while it waits.");

            ui.Caption("Timing");
            ui.BeginCard();

            float delay = config.RaiseDelaySeconds;
            if (ui.StepperRow("Wait before rezzing", null,
                    ref delay, 0f, 10f, 0.5f, Seconds))
            {
                config.RaiseDelaySeconds = delay;
                save();
            }

            float mp = config.MinimumMpForRaise;
            if (ui.StepperRow("Minimum MP", null,
                    ref mp, Configuration.MinimumMpFloor, Configuration.MinimumMpCeiling, 200f, Mp))
            {
                config.MinimumMpForRaise = (int)MathF.Round(mp);
                save();
            }

            ui.EndCard();

            ui.Caption("Chat");
            ui.BeginCard();

            bool echo = config.AnnounceInEcho;
            if (ui.SwitchRow("Announce in /echo", null, ref echo))
            {
                config.AnnounceInEcho = echo;
                save();
            }

            ui.EndCard();
        }

        // ══════════════════════════════════════════════════════════
        //  STATS
        // ══════════════════════════════════════════════════════════

        private void DrawStatsScreen()
        {
            if (ui.NavBar("Stats", true))
                screen = Screen.Main;

            int cast = config.TotalRaisesCast;
            int accepted = config.AcceptedRaisesCount;

            ImGui.Dummy(new Vector2(0f, Ios.Px(18f)));

            var dl = ImGui.GetWindowDrawList();
            var top = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;

            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);

            // ── ONE NUMBER, GIVEN THE ROOM A HEADLINE NEEDS ───────────────
            // The first version put a 46px figure in a card padded like a settings row, so the
            // headline of the screen was the same visual weight as "Announce in /echo". A number
            // this size needs air around it or it reads as an accident.
            ImGui.Dummy(new Vector2(0f, Ios.Px(34f)));

            string total = $"{cast + accepted:N0}";
            using (fonts.PushHuge())
            {
                var size = ImGui.CalcTextSize(total);
                dl.AddText(new Vector2(top.X + (width - size.X) * 0.5f, ImGui.GetCursorScreenPos().Y),
                    Ios.Label, total);
                ImGui.Dummy(new Vector2(0f, size.Y + Ios.Px(Ios.DescenderRoom)));
            }

            ImGui.Dummy(new Vector2(0f, Ios.Px(10f)));

            using (fonts.PushSmall())
            {
                const string caption = "REZZES HANDLED";
                var size = ImGui.CalcTextSize(caption);
                dl.AddText(new Vector2(top.X + (width - size.X) * 0.5f, ImGui.GetCursorScreenPos().Y),
                    Ios.Secondary, caption);
                ImGui.Dummy(new Vector2(0f, size.Y));
            }

            ImGui.Dummy(new Vector2(0f, Ios.Px(34f)));

            float bottom = ImGui.GetCursorScreenPos().Y;
            dl.ChannelsSetCurrent(0);
            dl.AddRectFilled(top, new Vector2(top.X + width, bottom), Ios.Card, Ios.Px(14f));
            dl.ChannelsMerge();

            ui.Caption("Breakdown");
            ui.BeginCard();
            ui.ValueRow("Cast on other people", $"{cast:N0}", Ios.Label);
            ui.ValueRow("Accepted on you", $"{accepted:N0}", Ios.Label);
            ui.EndCard();
        }

        // ══════════════════════════════════════════════════════════

        private string DescribeRezGearset()
        {
            if (config.RezGearsetId < 0)
                return "Off";

            uint job = JobSwitcher.JobOfGearset(config.RezGearsetId);
            return job == 0 ? "Off" : RezIds.JobName(job, dataManager);
        }

        private static string Seconds(float v)
            => v <= 0f ? "Off" : (Math.Abs(v - MathF.Round(v)) < 0.05f ? $"{(int)MathF.Round(v)}s" : $"{v:0.0}s");

        private static string Mp(float v) => $"{(int)MathF.Round(v):N0}";

        private static uint ToneColour(Tone tone) => tone switch
        {
            Tone.Good => Ios.Green,
            Tone.Warn => Ios.Orange,
            Tone.Bad => Ios.Red,
            Tone.Info => Ios.Teal,
            _ => Ios.Secondary,
        };
    }
}
