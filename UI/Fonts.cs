using System;
using System.IO;
using System.Reflection;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.GameFonts;
using Dalamud.Interface.ManagedFontAtlas;
using Dalamud.Interface.Utility;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace AutoRezzer.UI
{
    /// <summary>
    /// The window's typeface, built once at load and ready before anybody opens anything.
    ///
    /// ── WHY NOT JUST USE DALAMUD'S FONT ───────────────────────────────────
    /// Because the window is not a Dalamud window in the way the default font implies. The layout is
    /// grouped cards with a large title, a 15px row label and a 12.5px caption under it, and that
    /// hierarchy only reads if the sizes are actually different. Dalamud gives you one size, so
    /// "secondary text" would have to be conveyed with colour alone, and everything would sit at the
    /// same optical weight.
    ///
    /// Inter, the closest free face to the one a phone uses. SIL Open Font License, shipped in
    /// fonts/ with its licence beside it.
    ///
    /// ── AND WHY EVERY CUT MERGES THE GAME'S OWN GLYPHS ────────────────────
    /// The status line prints player names, and player names are Japanese, Chinese and Korean as
    /// often as not. A Latin-only font renders those as a row of boxes, which is a worse bug than
    /// an ugly font: it makes the plugin look like it cannot see the person it is talking about.
    /// So each handle adds Inter first, then merges the game's glyphs behind it. ImGui takes each
    /// codepoint from the first font that has it, so Latin stays Inter and everything else falls
    /// through to Axis.
    ///
    /// ── BUILT IN THE CONSTRUCTOR, NOT ON FIRST DRAW ───────────────────────
    /// The atlas build is asynchronous and takes a moment. Starting it at load means it has long
    /// since finished by the time somebody types /autorez, rather than the first frame of the window
    /// being drawn in the wrong font and snapping a moment later. Push() is safe before then anyway,
    /// it simply leaves the current font alone, so a half-built atlas costs nothing worse than one
    /// frame that looks ordinary.
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    internal sealed class Fonts : IDisposable
    {
        /// <summary>Row labels, values, buttons. The size everything else is judged against.</summary>
        public const float BodyPx = 14f;

        /// <summary>Captions, secondary lines under a label, footnotes.</summary>
        public const float SmallPx = 11.5f;

        /// <summary>
        /// The version under the window's name.
        ///
        /// Eleven, and the number was found rather than chosen: 11.5, the caption size, reads as too
        /// loud for a line nobody needs to see, and 10.5 disappears. Half a pixel is a real
        /// difference at this size, which is why it took a few passes to land on.
        /// </summary>
        public const float TinyPx = 11f;

        /// <summary>The large navigation title, iOS style.</summary>
        public const float TitlePx = 21f;

        /// <summary>The one big number on the stats screen.</summary>
        public const float HugePx = 64f;

        /// <summary>Font Awesome, for chevrons, ticks and the stepper's plus and minus.</summary>
        public const float IconPx = 12f;

        private readonly IPluginLog log;

        private readonly IFontHandle? body;
        private readonly IFontHandle? medium;
        private readonly IFontHandle? small;
        private readonly IFontHandle? tiny;
        private readonly IFontHandle? bodyWithIcons;
        private readonly IFontHandle? title;
        private readonly IFontHandle? huge;
        private readonly IFontHandle? iconFont;

        public Fonts(IDalamudPluginInterface pluginInterface, IPluginLog log)
        {
            this.log = log;

            byte[]? regular = Load("AutoRezzer.fonts.Inter-Regular.ttf");
            byte[]? semibold = Load("AutoRezzer.fonts.Inter-SemiBold.ttf");
            byte[]? bold = Load("AutoRezzer.fonts.Inter-Bold.ttf");

            if (regular == null || semibold == null || bold == null)
            {
                // Everything below degrades to Dalamud's font, which is a worse-looking window and
                // nothing else. Not worth failing to load over.
                log.Warning("[AutoRezzer] Could not read the bundled fonts; falling back to the default.");
                return;
            }

            var atlas = pluginInterface.UiBuilder.FontAtlas;

            body = Build(atlas, regular, BodyPx, "Inter Regular");
            medium = Build(atlas, semibold, BodyPx, "Inter SemiBold");
            small = Build(atlas, regular, SmallPx, "Inter Small");
            tiny = Build(atlas, regular, TinyPx, "Inter Tiny", withGameGlyphs: false);

            // ── ONE FONT, SO AN ICON AND A WORD SHARE A BASELINE ───────────
            // Font Awesome merged INTO the text face rather than pushed beside it. Two separate
            // handles mean two line boxes whose ink sits at different heights, so "cup then label"
            // reads as misaligned however carefully each half is centred, and the only fixes are
            // hand-tuned nudges that break at the next size or UI scale.
            //
            // Merged, the pair is one string. One measurement, one draw, centred by construction.
            //
            // Built at BODY size, because a button somebody is meant to notice is not caption text.
            bodyWithIcons = Build(atlas, semibold, BodyPx, "Inter Body + icons",
                withGameGlyphs: false, withIcons: true);
            title = Build(atlas, bold, TitlePx, "Inter Title");
            huge = Build(atlas, bold, HugePx, "Inter Huge", withGameGlyphs: false);

            // ── ICONS COME FROM FONT AWESOME, NOT FROM ME ──────────────────
            // The first cut drew the chevrons and ticks by hand out of two AddLine calls each. They
            // were passable and they were also wrong: hand-drawn glyphs do not sit on the same
            // optical baseline as the text beside them, they need re-tuning at every UI scale, and
            // Dalamud already ships the icon set every other plugin uses.
            iconFont = BuildIcons(atlas);
        }

        /// <summary>Whether the bundled font is up. False means every Push is a no-op and the window
        /// draws in Dalamud's font, which is ugly but entirely functional.</summary>
        public bool Ready => body is { Available: true };

        public IDisposable PushBody() => Push(body);
        public IDisposable PushMedium() => Push(medium);
        public IDisposable PushSmall() => Push(small);

        public IDisposable PushTiny() => Push(tiny);

        /// <summary>The body cut with Font Awesome merged in, for a label with an icon in it.</summary>
        public IDisposable PushBodyWithIcons() => Push(bodyWithIcons);
        public IDisposable PushTitle() => Push(title);
        public IDisposable PushHuge() => Push(huge);
        public IDisposable PushIcon() => Push(iconFont);

        /// <summary>Whether Font Awesome built. False means the icon call sites draw nothing rather
        /// than drawing a box, which is the tidier failure.</summary>
        public bool IconsReady => iconFont is { Available: true };

        /// <summary>
        /// One cut: Manrope at a size, with the game's glyphs merged in behind it.
        /// </summary>
        /// <param name="withGameGlyphs">
        /// Whether to merge the game's own glyphs in behind this cut.
        ///
        /// ── OFF FOR THE BIG NUMBER, AND THAT IS THE POINT ─────────────────
        /// The game's fonts are bitmaps baked at fixed sizes. Merging Axis into a 64px face is asking
        /// for a glyph that does not exist at that size, and the whole handle fails to build, which
        /// Push() then silently treats as "not ready" and leaves the previous font in place. The
        /// symptom is a headline number that renders at body size and never gets bigger no matter
        /// what the constant says.
        ///
        /// The stats figure draws digits and a comma. It has no use for CJK, so it does not pay for
        /// it.
        /// </param>
        private IFontHandle? Build(IFontAtlas atlas, byte[] bytes, float px, string tag,
            bool withGameGlyphs = true, bool withIcons = false)
        {
            try
            {
                return atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
                {
                    var config = new SafeFontConfig { SizePx = px * ImGuiHelpers.GlobalScale };
                    config.MergeFont = tk.AddFontFromMemory(bytes, config, tag);

                    // Merged into the face above, so its glyphs sit on the same baseline as the
                    // letters rather than in a font of their own.
                    if (withIcons)
                        tk.AddFontAwesomeIconFont(config);

                    if (withGameGlyphs)
                    {
                        // The fallback half: Latin is already claimed by Inter, so what actually
                        // comes through here is CJK, the game's private-use symbols, and anything
                        // else Inter has no glyph for.
                        tk.AddGameGlyphs(new GameFontStyle(GameFontFamilyAndSize.Axis14), null, config.MergeFont);
                        tk.AttachExtraGlyphsForDalamudLanguage(config);
                    }

                    tk.Font = config.MergeFont;
                }));
            }
            catch (Exception ex)
            {
                log.Warning($"[AutoRezzer] Could not build the {tag} font: {ex.Message}");
                return null;
            }
        }

        private IFontHandle? BuildIcons(IFontAtlas atlas)
        {
            try
            {
                return atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
                {
                    var config = new SafeFontConfig { SizePx = IconPx * ImGuiHelpers.GlobalScale };
                    tk.Font = tk.AddFontAwesomeIconFont(config);
                }));
            }
            catch (Exception ex)
            {
                log.Warning($"[AutoRezzer] Could not build the icon font: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Pushes a handle, or nothing at all when it is not ready.
        ///
        /// Always returns something disposable so every call site can be a `using` without asking
        /// whether the font exists. A null handle is a window in Dalamud's font, not a crash.
        /// </summary>
        private static IDisposable Push(IFontHandle? handle)
            => handle is { Available: true } ? handle.Push() : Nothing.Instance;

        private static byte[]? Load(string resource)
        {
            try
            {
                using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
                if (stream == null)
                    return null;

                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                return memory.ToArray();
            }
            catch (Exception)
            {
                return null;
            }
        }

        public void Dispose()
        {
            body?.Dispose();
            medium?.Dispose();
            small?.Dispose();
            tiny?.Dispose();
            bodyWithIcons?.Dispose();
            title?.Dispose();
            huge?.Dispose();
            iconFont?.Dispose();
        }

        /// <summary>A `using` block that does nothing, for when the font is not there yet.</summary>
        private sealed class Nothing : IDisposable
        {
            public static readonly Nothing Instance = new();
            public void Dispose() { }
        }
    }
}
