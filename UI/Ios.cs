using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Utility;

namespace AutoRezzer.UI
{
    /// <summary>
    /// The widget kit: grouped inset lists, switches, steppers, drill-in rows.
    ///
    /// ── WHY NONE OF THIS IS ImGui.Checkbox ────────────────────────────────
    /// The window is a settings app, and a settings app is read by scanning rather than by reading.
    /// ImGui's stock widgets put the control on the LEFT and the label after it, at one size, with
    /// no room for the second line that says what the setting means. Scanning that costs the same
    /// effort per row as reading it, which is why the old window needed a paragraph of help text per
    /// switch to be usable at all.
    ///
    /// So: label left, control right, a smaller line underneath for the meaning, and rows grouped
    /// into rounded cards with hairlines between them. That is a phone's settings screen, and the
    /// reason to copy it is not fashion. It is that a billion people can already read one.
    ///
    /// ── THREE RULES THIS FILE EXISTS TO ENFORCE ───────────────────────────
    /// 1. NOTHING OVERLAPS. Every row computes where its control starts and clips the label to stop
    ///    short of it, so a long name is cut off cleanly at that edge instead of running underneath
    ///    a switch. Done once, here, rather than remembered at forty call sites.
    /// 2. EVERY GAP IS A NAMED CONSTANT. Padding lives at the top of this file and nowhere else.
    ///    Numbers typed inline at a call site are how a window ends up with four different left
    ///    margins that all look like mistakes because they are.
    /// 3. EVERYTHING SCALES. Every constant is multiplied by Dalamud's global scale, so the window
    ///    holds together for somebody running the game at 150%.
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    internal sealed class Ios
    {
        // ══════════════════════════════════════════════════════════
        //  PALETTE  (iOS dark, verbatim)
        // ══════════════════════════════════════════════════════════

        public static readonly uint Bg = Col(0x00, 0x00, 0x00);
        public static readonly uint Card = Col(0x1C, 0x1C, 0x1E);
        public static readonly uint CardRaised = Col(0x2C, 0x2C, 0x2E);
        public static readonly uint RowHover = Col(0x2A, 0x2A, 0x2D);
        public static readonly uint Sep = Col(0x38, 0x38, 0x3A);
        public static readonly uint Label = Col(0xFF, 0xFF, 0xFF);
        public static readonly uint Secondary = Col(0x8E, 0x8E, 0x93);
        public static readonly uint Tertiary = Col(0x5B, 0x5B, 0x60);
        public static readonly uint Green = Col(0x30, 0xD1, 0x58);
        public static readonly uint Blue = Col(0x0A, 0x84, 0xFF);
        public static readonly uint Orange = Col(0xFF, 0x9F, 0x0A);
        public static readonly uint Red = Col(0xFF, 0x45, 0x3A);
        public static readonly uint Teal = Col(0x64, 0xD2, 0xFF);
        /// <summary>Ko-fi's own red. Not an iOS colour, and deliberately the only thing on screen
        /// wearing somebody else's brand: a support button that looks like the rest of the window is
        /// a support button nobody notices.</summary>
        public static readonly uint Kofi = Col(0xFF, 0x5E, 0x5B);

        public static readonly uint SwitchOff = Col(0x39, 0x39, 0x3D);
        public static readonly uint Knob = Col(0xFF, 0xFF, 0xFF);

        // ══════════════════════════════════════════════════════════
        //  METRICS  (unscaled; multiply through Px)
        // ══════════════════════════════════════════════════════════

        // ── THESE NUMBERS WERE ALL TOO SMALL ONCE ─────────────────────────
        // The first cut of this file used ImGui's own idea of a line height, which is the font's em
        // size and nothing more: no leading, no descender room. Two lines of a row ended up two
        // pixels apart, the rows were 48px tall with 29px of text crammed into them, and every
        // descender was clipped by the text's own bounding box. It read as broken rather than tight.
        //
        // Leading is now explicit and generous, rows have real vertical padding, and every clip
        // rectangle is the full row rather than the measured glyph box.
        private const float CardRadius = 12f;
        private const float RowPadX = 16f;
        private const float RowPadY = 12f;
        private const float RowMinHeight = 50f;
        private const float TileSize = 28f;
        private const float TileRadius = 8f;
        private const float TileGap = 13f;
        private const float ControlGap = 14f;

        /// <summary>
        /// How far below a text block a clip rectangle has to reach.
        ///
        /// ImGui reports a line's height as the font's em size, and Manrope's descenders hang below
        /// it. Clipping to the reported height slices the tail off every p, g and y, which is what
        /// cut the bottom out of "3 nearby, all up".
        /// </summary>
        public const float DescenderRoom = 6f;

        /// <summary>Space between a row's label and the line under it. Four, not two.</summary>
        private const float LineGap = 4f;

        private const float CaptionTop = 28f;
        private const float CaptionBottom = 14f;
        private const float CaptionIndent = 6f;
        private const float FootnoteTop = 10f;

        /// <summary>Extra leading between wrapped footnote lines. ImGui advances wrapped text by
        /// exactly the font size, which for a 13px face is lines that touch.</summary>
        private const float FootnoteLead = 5f;

        private const float SwitchW = 44f;
        private const float SwitchH = 26f;
        private const float ChevronGap = 10f;

        private readonly Fonts fonts;

        /// <summary>Where the open card started, so its background can be drawn once it is closed
        /// and its height is finally known.</summary>
        private Vector2 cardTop;
        private bool inCard;
        private bool cardFirstRow;
        private int nextId;

        /// <summary>
        /// The hovered row's rectangle, held until the card closes.
        ///
        /// ── WHY THE HIGHLIGHT IS DRAWN LAST AND NOT WHERE IT HAPPENS ──────
        /// A hover fill has to match the card's corners or it spills square edges over the rounded
        /// ones, and whether a row is the LAST row is not knowable while that row is being drawn.
        /// EndCard knows both, so the rectangle is remembered and painted there, on the background
        /// channel so it sits behind the label rather than over it.
        /// </summary>
        private Vector2 hoverMin, hoverMax;
        private bool hoverFirst, hoverAny;
        private Vector2 lastRowMin;

        public Ios(Fonts fonts)
        {
            this.fonts = fonts;
        }

        /// <summary>Dalamud's UI scale. Read fresh every frame; the user can change it mid-session.</summary>
        public static float Scale => ImGuiHelpers.GlobalScale;

        /// <summary>One unscaled measurement, in real pixels.</summary>
        public static float Px(float v) => v * Scale;

        public static uint Col(byte r, byte g, byte b, byte a = 255)
            => ((uint)a << 24) | ((uint)b << 16) | ((uint)g << 8) | r;

        /// <summary>
        /// Shortens text to fit a width, ending in an ellipsis.
        ///
        /// ── CLIPPING IS NOT TRUNCATION ────────────────────────────────────
        /// A clip rectangle stops a label running under a switch, which is necessary, but on its own
        /// it slices the last glyph down the middle and leaves "Wait for Swiftcast instead of standi"
        /// looking like the plugin ran out of memory. Measuring and cutting at a word boundary with
        /// an ellipsis says the same thing on purpose.
        ///
        /// The clip stays as well. This is the polite version and that is the guarantee.
        /// </summary>
        public static string Fit(string text, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || maxWidth <= 0f)
                return text;

            if (ImGui.CalcTextSize(text).X <= maxWidth)
                return text;

            float dots = ImGui.CalcTextSize("...").X;
            float room = maxWidth - dots;
            if (room <= 0f)
                return "...";

            // Binary search rather than a character-at-a-time walk: CalcTextSize is not free and a
            // long label in a narrow window would otherwise measure sixty times per frame.
            int low = 0, high = text.Length;
            while (low < high)
            {
                int mid = (low + high + 1) / 2;
                if (ImGui.CalcTextSize(text.AsSpan(0, mid)).X <= room)
                    low = mid;
                else
                    high = mid - 1;
            }

            if (low <= 0)
                return "...";

            // Back off to a word boundary when one is close, so the cut lands between words rather
            // than mid-syllable.
            int space = text.LastIndexOf(' ', Math.Min(low, text.Length - 1));
            if (space > low - 12 && space > 0)
                low = space;

            return string.Concat(text.AsSpan(0, low).TrimEnd(), "...");
        }

        /// <summary>The same colour at a different opacity, for disabled rows.</summary>
        public static uint Fade(uint colour, float alpha)
        {
            uint a = (uint)((colour >> 24) * Math.Clamp(alpha, 0f, 1f));
            return (colour & 0x00FFFFFF) | (a << 24);
        }

        // ══════════════════════════════════════════════════════════
        //  SECTION FURNITURE
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The small grey heading above a card.
        ///
        /// The gap ABOVE it is much larger than the gap below, because a caption belongs to the card
        /// it introduces. Even spacing on both sides makes it read as floating between two cards and
        /// attached to neither, which is what made the section breaks feel arbitrary.
        /// </summary>
        public void Caption(string text)
        {
            ImGui.Dummy(new Vector2(0f, Px(CaptionTop)));

            using (fonts.PushSmall())
            {
                ImGui.Indent(Px(CaptionIndent));
                ImGui.PushStyleColor(ImGuiCol.Text, ToVec(Secondary));
                ImGui.TextUnformatted(text.ToUpperInvariant());
                ImGui.PopStyleColor();
                ImGui.Unindent(Px(CaptionIndent));
            }

            ImGui.Dummy(new Vector2(0f, Px(CaptionBottom)));
        }

        /// <summary>
        /// The small grey paragraph under a card.
        ///
        /// ── WRAPPED BY HAND, BECAUSE IMGUI HAS NO LINE HEIGHT ─────────────
        /// ImGui advances wrapped text by exactly the font size and offers no way to change that. At
        /// 13px that is thirteen pixels of advance for glyphs about nine tall, so a three line
        /// footnote renders as a grey brick. Wrapping here means each line can be placed with real
        /// leading between them.
        /// </summary>
        public void Footnote(string text)
        {
            ImGui.Dummy(new Vector2(0f, Px(FootnoteTop)));

            using (fonts.PushSmall())
            {
                var dl = ImGui.GetWindowDrawList();
                float indent = Px(CaptionIndent);
                var start = ImGui.GetCursorScreenPos();
                float width = ImGui.GetContentRegionAvail().X - indent;

                float lineH = ImGui.GetTextLineHeight();
                float step = lineH + Px(FootnoteLead);
                float y = start.Y;

                foreach (string line in Wrap(text, width))
                {
                    dl.AddText(new Vector2(start.X + indent, y), Secondary, line);
                    y += step;
                }

                // The last line keeps its leading as trailing space, which is what stops the next
                // caption sitting on top of it.
                ImGui.Dummy(new Vector2(0f, y - start.Y));
            }
        }

        /// <summary>Greedy word wrap against the current font. Long single words are left alone;
        /// the clip and the ellipsis elsewhere deal with those.</summary>
        private static System.Collections.Generic.List<string> Wrap(string text, float width)
        {
            var lines = new System.Collections.Generic.List<string>();
            if (width <= 0f)
            {
                lines.Add(text);
                return lines;
            }

            var line = new System.Text.StringBuilder();

            foreach (string word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                string candidate = line.Length == 0 ? word : $"{line} {word}";

                if (ImGui.CalcTextSize(candidate).X <= width || line.Length == 0)
                {
                    line.Clear().Append(candidate);
                    continue;
                }

                lines.Add(line.ToString());
                line.Clear().Append(word);
            }

            if (line.Length > 0)
                lines.Add(line.ToString());

            return lines;
        }

        // ══════════════════════════════════════════════════════════
        //  THE CARD
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// Opens a grouped card. Every row until <see cref="EndCard"/> sits inside it.
        ///
        /// The background is drawn on a SEPARATE DRAW CHANNEL and merged at the end, because its
        /// height is not known until the rows have been laid out. Reserving a guessed height instead
        /// is how a card ends up with a strip of colour hanging off the bottom.
        /// </summary>
        public void BeginCard()
        {
            var dl = ImGui.GetWindowDrawList();
            dl.ChannelsSplit(2);
            dl.ChannelsSetCurrent(1);

            cardTop = ImGui.GetCursorScreenPos();
            inCard = true;
            cardFirstRow = true;
            hoverAny = false;
            lastRowMin = cardTop;
        }

        public void EndCard()
        {
            if (!inCard)
                return;

            inCard = false;

            var dl = ImGui.GetWindowDrawList();
            float bottom = ImGui.GetCursorScreenPos().Y;
            float width = ImGui.GetContentRegionAvail().X;

            dl.ChannelsSetCurrent(0);
            dl.AddRectFilled(cardTop, new Vector2(cardTop.X + width, bottom), Card, Px(CardRadius));

            if (hoverAny)
            {
                bool last = Math.Abs(hoverMin.Y - lastRowMin.Y) < 0.5f;

                var corners = (hoverFirst, last) switch
                {
                    (true, true) => ImDrawFlags.RoundCornersAll,
                    (true, false) => ImDrawFlags.RoundCornersTop,
                    (false, true) => ImDrawFlags.RoundCornersBottom,
                    _ => ImDrawFlags.RoundCornersNone,
                };

                dl.AddRectFilled(hoverMin, hoverMax, RowHover, Px(CardRadius), corners);
            }

            dl.ChannelsMerge();
        }

        // ══════════════════════════════════════════════════════════
        //  ROWS
        // ══════════════════════════════════════════════════════════

        /// <summary>What a row needs to know about itself before anything is drawn.</summary>
        private readonly record struct Frame(
            Vector2 Min, Vector2 Max, float Width, float Height,
            bool Hovered, bool Clicked, float TextLeft, float TextRight);

        /// <summary>
        /// Lays out one row: reserves the space, takes the click, draws the hover fill and the
        /// hairline, and works out the horizontal band the label is allowed to occupy.
        ///
        /// <paramref name="controlWidth"/> is what sits on the right. The label's clip edge is
        /// derived from it rather than guessed, which is the whole of rule 1 at the top of this file.
        /// </summary>
        private Frame BeginRow(float height, float controlWidth, bool hasTile, bool interactive)
        {
            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            var max = new Vector2(min.X + width, min.Y + height);

            bool pressed = ImGui.InvisibleButton($"##row{nextId++}", new Vector2(width, height));

            // ── THE ROW MUST NOT SWALLOW ITS OWN CONTROLS ─────────────────
            // ImGui gives the hover to the FIRST item that claims it, and refuses every later item
            // that overlaps unless the earlier one says otherwise. The row is submitted before the
            // stepper buttons that sit inside it, so without this the plus and minus are drawn,
            // hit-tested against a rectangle that has already been claimed, and silently do nothing.
            // That is exactly what they did.
            ImGui.SetItemAllowOverlap();

            bool hovered = interactive && ImGui.IsItemHovered();
            bool clicked = interactive && pressed;

            if (hovered)
            {
                hoverMin = min;
                hoverMax = max;
                hoverFirst = cardFirstRow;
                hoverAny = true;
            }

            lastRowMin = min;

            // The hairline is inset to where the TEXT starts, not to the card edge. That is what
            // makes a list of icon rows read as one column rather than as stripes.
            if (!cardFirstRow)
            {
                float inset = Px(RowPadX) + (hasTile ? Px(TileSize + TileGap) : 0f);
                dl.AddLine(new Vector2(min.X + inset, min.Y), new Vector2(max.X, min.Y), Sep, 1f);
            }

            cardFirstRow = false;

            float textLeft = min.X + Px(RowPadX) + (hasTile ? Px(TileSize + TileGap) : 0f);
            float textRight = max.X - Px(RowPadX) - controlWidth - (controlWidth > 0f ? Px(ControlGap) : 0f);

            return new Frame(min, max, width, height, hovered, clicked, textLeft, textRight);
        }

        /// <summary>
        /// Draws a row's label, and its secondary line when there is one, vertically centred as a
        /// block and clipped to the band the frame worked out.
        /// </summary>
        private void RowText(in Frame f, string label, string? detail, uint labelColour, float alpha)
        {
            var dl = ImGui.GetWindowDrawList();
            bool two = !string.IsNullOrEmpty(detail);

            float labelH, detailH = 0f;
            using (fonts.PushMedium())
                labelH = ImGui.GetTextLineHeight();

            if (two)
            {
                using (fonts.PushSmall())
                    detailH = ImGui.GetTextLineHeight();
            }

            float block = labelH + (two ? Px(LineGap) + detailH : 0f);
            float y = f.Min.Y + (f.Height - block) * 0.5f;
            float room = Math.Max(0f, f.TextRight - f.TextLeft);

            // Clipped to the FULL ROW, not to the measured text block. Clipping to the block height
            // cuts the descenders off every p, g and y, which is the sort of detail that makes a
            // window look cheap without anybody being able to say why.
            dl.PushClipRect(new Vector2(f.TextLeft, f.Min.Y),
                new Vector2(Math.Max(f.TextLeft, f.TextRight), f.Max.Y + Px(DescenderRoom)), true);

            using (fonts.PushMedium())
                dl.AddText(new Vector2(f.TextLeft, y), Fade(labelColour, alpha), Fit(label, room));

            if (two)
            {
                using (fonts.PushSmall())
                {
                    dl.AddText(new Vector2(f.TextLeft, y + labelH + Px(LineGap)),
                        Fade(Secondary, alpha), Fit(detail!, room));
                }
            }

            dl.PopClipRect();
        }

        /// <summary>The height a row needs: two lines fit, one line sits at the minimum.</summary>
        private float HeightFor(bool twoLines)
        {
            if (!twoLines)
                return Px(RowMinHeight);

            float labelH, detailH;
            using (fonts.PushMedium())
                labelH = ImGui.GetTextLineHeight();
            using (fonts.PushSmall())
                detailH = ImGui.GetTextLineHeight();

            return Math.Max(Px(RowMinHeight), labelH + Px(LineGap) + detailH + Px(RowPadY) * 2f);
        }

        // ══════════════════════════════════════════════════════════
        //  ROW KINDS
        // ══════════════════════════════════════════════════════════

        /// <summary>A row with a switch on the right. The whole row is the hit target, which is a
        /// far bigger one than the switch alone and costs nothing.</summary>
        public bool SwitchRow(string label, string? detail, ref bool value,
            Art icon = default, uint tint = 0, bool enabled = true)
        {
            float alpha = enabled ? 1f : 0.36f;
            bool hasTile = !icon.IsNull;
            var f = BeginRow(HeightFor(!string.IsNullOrEmpty(detail)), Px(SwitchW), hasTile, enabled);

            if (hasTile)
                DrawTile(f, icon, tint, alpha);

            RowText(f, label, detail, Label, alpha);
            DrawSwitch(f, value, alpha);

            if (f.Clicked)
            {
                value = !value;
                return true;
            }

            return false;
        }

        /// <summary>A row that pushes another screen. Value on the right, then a chevron.</summary>
        public bool DrillRow(string label, string? detail, string value, ImTextureID icon = default,
            uint tint = 0, bool enabled = true)
        {
            float alpha = enabled ? 1f : 0.36f;

            float chevW = GlyphWidth(FontAwesomeIcon.ChevronRight), valueW = 0f;
            if (!string.IsNullOrEmpty(value))
            {
                using (fonts.PushBody())
                    valueW = ImGui.CalcTextSize(value).X;
            }

            float control = valueW + (valueW > 0f ? Px(ChevronGap) : 0f) + chevW;
            var f = BeginRow(HeightFor(!string.IsNullOrEmpty(detail)), control, !icon.IsNull, enabled);

            if (!icon.IsNull)
                DrawTile(f, icon, tint, alpha);

            RowText(f, label, detail, Label, alpha);

            var dl = ImGui.GetWindowDrawList();
            float right = f.Max.X - Px(RowPadX);

            Glyph(dl, new Vector2(right - chevW * 0.5f, f.Min.Y + f.Height * 0.5f),
                FontAwesomeIcon.ChevronRight, Fade(Tertiary, alpha));

            if (valueW > 0f)
            {
                using (fonts.PushBody())
                {
                    float h = ImGui.GetTextLineHeight();
                    dl.AddText(new Vector2(right - chevW - Px(ChevronGap) - valueW,
                        f.Min.Y + (f.Height - h) * 0.5f), Fade(Secondary, alpha), value);
                }
            }

            return f.Clicked;
        }

        /// <summary>
        /// A row you pick from a list. Ticked on the right when it is the chosen one.
        ///
        /// Separate from DrillRow because the affordance is different: a chevron promises another
        /// screen, and a tick promises the choice was taken here.
        /// </summary>
        public bool SelectRow(string label, string? detail, bool selected, Art icon = default)
        {
            float tickW = GlyphWidth(FontAwesomeIcon.Check);
            var f = BeginRow(HeightFor(!string.IsNullOrEmpty(detail)), tickW, !icon.IsNull, true);

            if (!icon.IsNull)
                DrawTile(f, icon, 0, 1f);

            RowText(f, label, detail, Label, 1f);

            if (selected)
            {
                Glyph(ImGui.GetWindowDrawList(),
                    new Vector2(f.Max.X - Px(RowPadX) - tickW * 0.5f, f.Min.Y + f.Height * 0.5f),
                    FontAwesomeIcon.Check, Blue);
            }

            return f.Clicked;
        }

        /// <summary>A row that only reports something. No hit target, no chevron.</summary>
        public void ValueRow(string label, string value, uint valueColour, Art icon = default,
            string? detail = null)
        {
            float valueW;
            using (fonts.PushBody())
                valueW = ImGui.CalcTextSize(value).X;

            var f = BeginRow(HeightFor(!string.IsNullOrEmpty(detail)), valueW, !icon.IsNull, false);

            if (!icon.IsNull)
                DrawTile(f, icon, 0, 1f);

            RowText(f, label, detail, Label, 1f);

            using (fonts.PushBody())
            {
                float h = ImGui.GetTextLineHeight();
                ImGui.GetWindowDrawList().AddText(
                    new Vector2(f.Max.X - Px(RowPadX) - valueW, f.Min.Y + (f.Height - h) * 0.5f),
                    valueColour, value);
            }
        }

        /// <summary>
        /// A row with a minus / value / plus control on the right.
        ///
        /// Steppers rather than sliders, and not for fashion: a slider in a 420px window is about
        /// 140px of travel for a range of 7,600 MP, which is 54 MP per pixel and unusable. A stepper
        /// lands on the value you meant on the first try.
        /// </summary>
        public bool StepperRow(string label, string? detail, ref float value, float min, float max,
            float step, Func<float, string> format, bool enabled = true)
        {
            float alpha = enabled ? 1f : 0.36f;

            float btn = Px(28f);
            float numW = Px(66f);
            float control = btn * 2f + numW + Px(4f);

            var f = BeginRow(HeightFor(!string.IsNullOrEmpty(detail)), control, false, false);
            RowText(f, label, detail, Label, alpha);

            var dl = ImGui.GetWindowDrawList();
            float trackH = Px(32f);
            float left = f.Max.X - Px(RowPadX) - control;
            var trackMin = new Vector2(left, f.Min.Y + (f.Height - trackH) * 0.5f);
            var trackMax = new Vector2(left + control, trackMin.Y + trackH);

            dl.AddRectFilled(trackMin, trackMax, Fade(CardRaised, alpha), Px(8f));

            bool changed = false;

            if (enabled)
            {
                changed |= StepButton(FontAwesomeIcon.Minus, new Vector2(trackMin.X + Px(2f), trackMin.Y + Px(2f)),
                    new Vector2(btn, trackH - Px(4f)), ref value, -step, min, max);

                changed |= StepButton(FontAwesomeIcon.Plus, new Vector2(trackMax.X - btn - Px(2f), trackMin.Y + Px(2f)),
                    new Vector2(btn, trackH - Px(4f)), ref value, step, min, max);
            }
            else
            {
                var box = new Vector2(btn, trackH - Px(4f));
                var a = new Vector2(trackMin.X + Px(2f), trackMin.Y + Px(2f));
                var b = new Vector2(trackMax.X - btn - Px(2f), trackMin.Y + Px(2f));
                Glyph(dl, a + box * 0.5f, FontAwesomeIcon.Minus, Fade(Label, alpha));
                Glyph(dl, b + box * 0.5f, FontAwesomeIcon.Plus, Fade(Label, alpha));
            }

            string text = format(value);
            using (fonts.PushMedium())
            {
                var size = ImGui.CalcTextSize(text);
                dl.AddText(new Vector2(trackMin.X + (control - size.X) * 0.5f,
                    trackMin.Y + (trackH - size.Y) * 0.5f), Fade(Label, alpha), text);
            }

            return changed;
        }

        private bool StepButton(FontAwesomeIcon icon, Vector2 pos, Vector2 size, ref float value,
            float delta, float min, float max)
        {
            var saved = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(pos);

            bool clicked = ImGui.InvisibleButton($"##step{nextId++}", size);
            bool hovered = ImGui.IsItemHovered();

            ImGui.SetCursorScreenPos(saved);

            if (hovered)
                ImGui.GetWindowDrawList().AddRectFilled(pos, pos + size, RowHover, Px(6f));

            Glyph(ImGui.GetWindowDrawList(), pos + size * 0.5f, icon, Label);

            if (!clicked)
                return false;

            float next = Math.Clamp(value + delta, min, max);
            if (Math.Abs(next - value) < 0.0001f)
                return false;

            value = next;
            return true;
        }

        /// <summary>A row of mutually exclusive choices, iOS segmented-control style.</summary>
        public bool Segmented(string[] options, ref int selected, bool enabled = true)
        {
            float alpha = enabled ? 1f : 0.36f;
            float height = Px(32f);

            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;

            dl.AddRectFilled(min, new Vector2(min.X + width, min.Y + height), Fade(CardRaised, alpha), Px(9f));

            float pad = Px(2f);
            float cell = (width - pad * 2f) / Math.Max(1, options.Length);
            bool changed = false;

            // One real button per segment. The previous version put a single button across the whole
            // bar and then tried to work out which cell the mouse was in, which meant every segment
            // reported the same click and none of them reported it reliably.
            for (int i = 0; i < options.Length; i++)
            {
                var cellMin = new Vector2(min.X + pad + cell * i, min.Y + pad);
                var cellMax = new Vector2(cellMin.X + cell, min.Y + height - pad);

                ImGui.SetCursorScreenPos(cellMin);
                bool hit = ImGui.InvisibleButton($"##seg{nextId++}", cellMax - cellMin);

                if (enabled && hit)
                {
                    selected = i;
                    changed = true;
                }

                if (i == selected)
                    dl.AddRectFilled(cellMin, cellMax, Fade(Col(0x48, 0x48, 0x4C), alpha), Px(7f));
                else if (enabled && ImGui.IsItemHovered())
                    dl.AddRectFilled(cellMin, cellMax, Fade(Col(0x3A, 0x3A, 0x3E), alpha), Px(7f));

                dl.PushClipRect(cellMin, cellMax, true);
                using (fonts.PushMedium())
                {
                    var size = ImGui.CalcTextSize(options[i]);
                    dl.AddText(new Vector2(cellMin.X + (cell - size.X) * 0.5f,
                            cellMin.Y + ((cellMax.Y - cellMin.Y) - size.Y) * 0.5f),
                        Fade(i == selected ? Label : Secondary, alpha), options[i]);
                }
                dl.PopClipRect();
            }

            ImGui.SetCursorScreenPos(new Vector2(min.X, min.Y + height));
            return changed;
        }

        /// <summary>
        /// A big pick-one card: artwork on top, name underneath, a ring when it is the chosen one.
        ///
        /// ── WHY THIS IS NOT A ROW ─────────────────────────────────────────
        /// Rows are for settings, where the label is the thing and any icon beside it is decoration.
        /// This is the other way round: two phantom jobs are told apart by their sprites long before
        /// anybody reads "Phantom Chemist", and the pixel character is the recognisable half. So the
        /// art gets the space and the name goes underneath it, which is how the game's own phantom
        /// job window presents them.
        /// </summary>
        public bool SpriteCard(in Art art, string label, bool selected, Vector2 pos, Vector2 size,
            bool enabled = true)
        {
            var dl = ImGui.GetWindowDrawList();
            var saved = ImGui.GetCursorScreenPos();

            ImGui.SetCursorScreenPos(pos);
            bool pressed = ImGui.InvisibleButton($"##pick{nextId++}", size);
            bool hovered = enabled && ImGui.IsItemHovered();
            ImGui.SetCursorScreenPos(saved);

            float alpha = enabled ? 1f : 0.36f;
            var max = pos + size;

            dl.AddRectFilled(pos, max, Fade(hovered ? CardRaised : Card, alpha), Px(12f));

            if (selected)
                dl.AddRect(pos, max, Fade(Blue, alpha), Px(12f), ImDrawFlags.None, Px(2f));

            // The sprite, as large as the card allows once the caption has taken its line, and never
            // stretched: these are 40 by 58 pixel characters and squashing one is instantly visible.
            float captionH;
            using (fonts.PushSmall())
                captionH = ImGui.GetTextLineHeight();

            float padY = Px(14f);
            float artBox = size.Y - captionH - padY * 2f - Px(8f);

            if (!art.IsNull && artBox > 0f)
            {
                float w = art.Aspect >= 1f ? artBox : artBox * art.Aspect;
                float h = art.Aspect >= 1f ? artBox / art.Aspect : artBox;
                var artMin = new Vector2(pos.X + (size.X - w) * 0.5f, pos.Y + padY);

                dl.AddImage(art.Tex, artMin, artMin + new Vector2(w, h), art.Uv0, art.Uv1,
                    Fade(Label, alpha));
            }

            dl.PushClipRect(pos, max, true);
            using (fonts.PushSmall())
            {
                string text = Fit(label, size.X - Px(12f));
                var textSize = ImGui.CalcTextSize(text);
                dl.AddText(new Vector2(pos.X + (size.X - textSize.X) * 0.5f, max.Y - padY - captionH),
                    Fade(selected ? Label : Secondary, alpha), text);
            }
            dl.PopClipRect();

            return enabled && pressed;
        }

        /// <summary>A full-width action button, iOS style.</summary>
        public bool Button(string label, uint colour, bool enabled = true)
        {
            float alpha = enabled ? 0.4f : 0.25f;
            float height = Px(38f);

            var dl = ImGui.GetWindowDrawList();
            var min = ImGui.GetCursorScreenPos();
            float width = ImGui.GetContentRegionAvail().X;
            var max = new Vector2(min.X + width, min.Y + height);

            bool pressed = ImGui.InvisibleButton($"##btn{nextId++}", new Vector2(width, height));
            bool hovered = enabled && ImGui.IsItemHovered();
            bool clicked = enabled && pressed;

            dl.AddRectFilled(min, max, Fade(colour, hovered ? 0.62f : alpha), Px(10f));

            dl.PushClipRect(min, max, true);
            using (fonts.PushMedium())
            {
                var size = ImGui.CalcTextSize(label);
                dl.AddText(min + (new Vector2(width, height) - size) * 0.5f,
                    Fade(enabled ? Label : Secondary, enabled ? 1f : 0.6f), label);
            }
            dl.PopClipRect();

            return clicked;
        }

        // ══════════════════════════════════════════════════════════
        //  ICONS  (Font Awesome, the set Dalamud already ships)
        // ══════════════════════════════════════════════════════════
        //
        // The first cut drew the chevrons and ticks by hand out of two AddLine calls each. They were
        // passable and they were also wrong: hand-drawn glyphs do not sit on the same optical
        // baseline as the text beside them, they need re-tuning at every UI scale, and every other
        // Dalamud plugin uses this set, so a hand-rolled one reads as off before anybody can say why.

        /// <summary>Draws one Font Awesome glyph centred on a point.</summary>
        private void Glyph(ImDrawListPtr dl, Vector2 centre, FontAwesomeIcon icon, uint colour)
        {
            if (!fonts.IconsReady)
                return;

            using (fonts.PushIcon())
            {
                string glyph = icon.ToIconString();
                var size = ImGui.CalcTextSize(glyph);
                dl.AddText(centre - size * 0.5f, colour, glyph);
            }
        }

        /// <summary>
        /// The Ko-fi button for the title bar: cup, then words, on a red pill.
        ///
        /// Returns its own width so the title bar can lay the name out around it rather than
        /// guessing, which is the same rule every other measured thing in this file follows.
        /// </summary>
        /// <summary>The cup and the words, as one string in one font. See Fonts.PushBodyWithIcons.</summary>
        private static string KofiText(string label)
            => $"{FontAwesomeIcon.MugHot.ToIconString()}  {label}";

        public float KofiButtonWidth(string label)
        {
            using (fonts.PushBodyWithIcons())
                return ImGui.CalcTextSize(KofiText(label)).X + Px(28f);
        }

        /// <summary>
        /// The support button.
        ///
        /// ── ONE STRING, ONE MEASUREMENT, ONE DRAW ─────────────────────────
        /// The cup is a glyph inside the label rather than an icon drawn beside it, because Font
        /// Awesome pushed as its own handle is a second font with a second line box, and centring
        /// two boxes whose ink sits at different heights inside them produces a pair that looks
        /// crooked however exact the arithmetic is.
        ///
        /// Merged into the text face, "cup space space Support me" is one run on one baseline.
        /// Centring it is a subtraction that cannot be wrong.
        /// </summary>
        public bool KofiButton(string label, Vector2 pos, float height)
        {
            float width = KofiButtonWidth(label);

            var saved = ImGui.GetCursorScreenPos();
            ImGui.SetCursorScreenPos(pos);
            bool pressed = ImGui.InvisibleButton($"##kofi{nextId++}", new Vector2(width, height));
            bool hovered = ImGui.IsItemHovered();
            ImGui.SetCursorScreenPos(saved);

            if (hovered)
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);

            var dl = ImGui.GetWindowDrawList();
            var size = new Vector2(width, height);

            dl.AddRectFilled(pos, pos + size, Fade(Kofi, hovered ? 1f : 0.88f), Px(8f));

            using (fonts.PushBodyWithIcons())
            {
                string text = KofiText(label);
                var textSize = ImGui.CalcTextSize(text);
                dl.AddText(pos + (size - textSize) * 0.5f, Label, text);
            }

            return pressed;
        }

        /// <summary>The close cross, for the window's own title bar.</summary>
        public void CloseGlyph(ImDrawListPtr dl, Vector2 centre, uint colour)
            => Glyph(dl, centre, FontAwesomeIcon.Times, colour);

        /// <summary>How wide to reserve for one icon. Measured, not assumed.</summary>
        private float GlyphWidth(FontAwesomeIcon icon)
        {
            if (!fonts.IconsReady)
                return 0f;

            using (fonts.PushIcon())
                return ImGui.CalcTextSize(icon.ToIconString()).X;
        }

        // ══════════════════════════════════════════════════════════
        //  PIECES
        // ══════════════════════════════════════════════════════════

        /// <summary>
        /// The icon at the head of a row, drawn inside a fixed square without being squashed.
        ///
        /// A phantom job sprite is 40 wide by 58 tall. Stretching one to fill a square tile makes a
        /// pixel character look like it has been sat on, and pixel art is the one kind of image
        /// where that is unmistakable.
        /// </summary>
        private void DrawTile(in Frame f, in Art icon, uint tint, float alpha)
        {
            var dl = ImGui.GetWindowDrawList();
            float box = Px(TileSize);
            var slot = new Vector2(f.Min.X + Px(RowPadX), f.Min.Y + (f.Height - box) * 0.5f);

            if (tint != 0)
                dl.AddRectFilled(slot, slot + new Vector2(box, box), Fade(tint, alpha * 0.9f), Px(TileRadius));

            float w = icon.Aspect >= 1f ? box : box * icon.Aspect;
            float h = icon.Aspect >= 1f ? box / icon.Aspect : box;
            var min = slot + new Vector2((box - w) * 0.5f, (box - h) * 0.5f);

            dl.AddImage(icon.Tex, min, min + new Vector2(w, h), icon.Uv0, icon.Uv1, Fade(Label, alpha));
        }

        /// <summary>The switch. Drawn rather than animated: a settings screen that slides is a
        /// settings screen you wait for.</summary>
        private void DrawSwitch(in Frame f, bool on, float alpha)
        {
            var dl = ImGui.GetWindowDrawList();
            float w = Px(SwitchW), h = Px(SwitchH);
            var min = new Vector2(f.Max.X - Px(RowPadX) - w, f.Min.Y + (f.Height - h) * 0.5f);
            var max = min + new Vector2(w, h);

            dl.AddRectFilled(min, max, Fade(on ? Green : SwitchOff, alpha), h * 0.5f);

            float r = h * 0.5f - Px(2f);
            float cx = on ? max.X - Px(2f) - r : min.X + Px(2f) + r;
            dl.AddCircleFilled(new Vector2(cx, min.Y + h * 0.5f), r, Fade(Knob, alpha), 24);
        }

        /// <summary>
        /// The large navigation title, with a back affordance when there is somewhere to go.
        ///
        /// Returns true when the back control was pressed.
        /// </summary>
        public bool NavBar(string title, bool showBack)
        {
            bool back = false;
            var start = ImGui.GetCursorScreenPos();

            float titleH;
            using (fonts.PushTitle())
                titleH = ImGui.GetTextLineHeight();

            float bar = titleH + Px(6f);
            float button = Px(30f);
            float indent = 0f;

            if (showBack)
            {
                // A chevron in a rounded square, sitting on the title's own line. The previous
                // version stacked a blue "< AutoRezzer" link ABOVE the title, which is a web
                // breadcrumb rather than a navigation bar and looked like one.
                var min = new Vector2(start.X, start.Y + (bar - button) * 0.5f);

                ImGui.SetCursorScreenPos(min);
                back = ImGui.InvisibleButton("##back", new Vector2(button, button));
                bool hovered = ImGui.IsItemHovered();

                var dl = ImGui.GetWindowDrawList();
                dl.AddRectFilled(min, min + new Vector2(button, button),
                    hovered ? CardRaised : Card, Px(9f));

                Glyph(dl, min + new Vector2(button * 0.5f, button * 0.5f),
                    FontAwesomeIcon.ChevronLeft, Blue);

                indent = button + Px(12f);
            }

            ImGui.SetCursorScreenPos(new Vector2(start.X + indent, start.Y));

            using (fonts.PushTitle())
            {
                ImGui.PushStyleColor(ImGuiCol.Text, ToVec(Label));
                ImGui.TextUnformatted(Fit(title, ImGui.GetContentRegionAvail().X));
                ImGui.PopStyleColor();
            }

            ImGui.SetCursorScreenPos(new Vector2(start.X, start.Y + bar));
            return back;
        }

        /// <summary>Resets the per-frame widget ids. Called once at the top of Draw.</summary>
        public void NewFrame() => nextId = 0;

        /// <summary>
        /// Closes a card that was left open, without drawing its background.
        ///
        /// ── INSURANCE AGAINST A HALF-DRAWN FRAME ──────────────────────────
        /// BeginCard splits the window's draw list into channels and EndCard merges them back. If
        /// anything throws in between, the split is never closed, and an unmerged draw list is not a
        /// missing card: it is corrupted geometry for the whole window and an assert inside ImGui.
        ///
        /// Draw calls this from a finally, so the worst an exception can cost is one card's
        /// background rather than the interface.
        /// </summary>
        public void AbortCard()
        {
            if (!inCard)
                return;

            inCard = false;
            ImGui.GetWindowDrawList().ChannelsMerge();
        }

        public static Vector4 ToVec(uint colour) => new(
            (colour & 0xFF) / 255f,
            ((colour >> 8) & 0xFF) / 255f,
            ((colour >> 16) & 0xFF) / 255f,
            ((colour >> 24) & 0xFF) / 255f);
    }
}
