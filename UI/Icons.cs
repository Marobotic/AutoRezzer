using System;
using System.Reflection;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Plugin.Services;

namespace AutoRezzer.UI
{
    /// <summary>
    /// The game's own icons, borrowed from the installed client.
    ///
    /// ── LOADED, NOT SHIPPED ───────────────────────────────────────────────
    /// Every icon here comes out of the player's own game files at runtime through Dalamud's texture
    /// provider. Nothing is bundled. That is the only version of "use the real job icons" that is
    /// honest: the alternative is redistributing Square Enix's artwork inside a plugin zip, which is
    /// not mine to hand out, and a set of hand-drawn imitations would look worse than either.
    ///
    /// ── A TEXTURE WRAP IS VALID FOR ONE FRAME AND NOT ONE FRAME LONGER ────
    /// This class hands back an ImTextureID rather than an IDalamudTextureWrap, and that is the
    /// whole design, not a convenience.
    ///
    /// The wrap Dalamud gives you is disposed at the end of the frame it was asked for. Keeping one,
    /// anywhere, for any reason, means the next frame reads Handle on a disposed object and throws
    /// ObjectDisposedException from inside Draw, which takes the window down. It is an easy mistake
    /// to make because the object looks perfectly ordinary and works fine for exactly one frame.
    ///
    /// So the wrap never leaves this file. It is fetched, its handle is read, and it is dropped, all
    /// inside one call. Callers get a value type they can pass straight to AddImage and cannot
    /// meaningfully hoard. Nothing is cached: the provider's own lookup is documented as cheap
    /// enough that caching it buys "minimal" benefit, and minimal benefit is not worth a class of
    /// crash that only shows up on the second frame.
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    /// <summary>
    /// One drawable thing: a texture plus the corner of it to use.
    ///
    /// The UV pair is what lets a sprite sheet be a first-class icon. Everything that draws an icon
    /// takes this rather than a bare handle, so a phantom job's 40x58 cell out of a shared atlas and
    /// a whole 40x40 job icon are the same kind of value at every call site.
    /// </summary>
    internal readonly record struct Art(ImTextureID Tex, Vector2 Uv0, Vector2 Uv1, float Aspect)
    {
        public bool IsNull => Tex.IsNull;

        /// <summary>A whole texture, square.</summary>
        public static implicit operator Art(ImTextureID tex) => new(tex, Vector2.Zero, Vector2.One, 1f);
    }

    internal sealed class Icons
    {
        /// <summary>
        /// The phantom job sprite sheet, and the only place it is named.
        ///
        /// ── WHY A ULD ATLAS AND NOT AN ICON ID ────────────────────────────
        /// Because there is no icon id. MKDSupportJob has seventeen columns and not one of them is
        /// an icon: five strings, a level cap, a job index, and five action-and-level pairs. The
        /// phantom job art is not in the icon table at all, it is one texture behind the phantom job
        /// window, and the client picks a cell out of it.
        ///
        /// 400x348, ten columns of 40x58. The cell index is the MKDSupportJob ROW, which is why
        /// Phantom White Mage, row 17, is the white robe with the red trim in the eighth cell of the
        /// second band, and Phantom Chemist, row 10, is the alchemist at the start of it. Rows three
        /// to five of the sheet repeat the same twenty-four in a second style and are not used.
        /// </summary>
        private const string PhantomSheet = "ui/uld/MKDSupportJob.tex";
        private const float SheetW = 400f;
        private const float SheetH = 348f;
        private const float CellW = 40f;
        private const float CellH = 58f;
        private const int CellsPerRow = 10;

        private readonly ITextureProvider textures;

        public Icons(ITextureProvider textures)
        {
            this.textures = textures;
        }

        /// <summary>
        /// AutoRezzer's own icon, for the window's title bar.
        ///
        /// ── COMPILED IN, NOT COPIED BESIDE THE DLL ────────────────────────
        /// The icon has to sit at images/icon.png for a DEV build, because that is where Dalamud
        /// looks for one. It does not follow the plugin into the release zip, which the packager
        /// builds from the assembly and its manifest, so reading that path works perfectly for
        /// whoever built it and shows an empty square to everybody who installed it.
        ///
        /// An embedded resource travels with the assembly wherever it goes.
        /// </summary>
        public ImTextureID Own()
        {
            try
            {
                var shared = textures.GetFromManifestResource(
                    Assembly.GetExecutingAssembly(), "AutoRezzer.images.icon.png");

                var wrap = shared.GetWrapOrDefault();
                return wrap == null ? default : wrap.Handle;
            }
            catch (Exception)
            {
                return default;
            }
        }

        /// <summary>
        /// A drawable handle for this frame, or the null handle when the game has no such icon or
        /// has not decoded it yet.
        ///
        /// The null handle is an ordinary answer rather than an error. Textures decode
        /// asynchronously, so the first frame that asks for a never-before-seen icon legitimately
        /// gets nothing back, and every caller draws a plain tile instead. It fills in a frame later.
        /// </summary>
        public ImTextureID Handle(uint iconId)
        {
            if (iconId == 0)
                return default;

            try
            {
                if (!textures.TryGetFromGameIcon(new GameIconLookup(iconId), out var shared))
                    return default;

                // GetWrapOrDefault rather than TryGetWrap: it is documented not to throw and to
                // return null rather than anything questionable when the texture is not ready.
                var wrap = shared.GetWrapOrDefault();
                return wrap == null ? default : wrap.Handle;
            }
            catch (Exception)
            {
                // A missing icon is a slightly plainer window. It is never worth an exception
                // escaping into Draw, which would take the whole UI down with it.
                return default;
            }
        }

        /// <summary>
        /// A phantom job's pixel sprite, cut out of the sheet the phantom job window uses.
        ///
        /// The aspect is carried through so callers can draw it tall rather than squashing a 40x58
        /// character into a square tile, which is the whole reason Art has the field.
        /// </summary>
        public Art PhantomSprite(int jobRow)
        {
            if (jobRow < 0 || jobRow >= CellsPerRow * 3)
                return default;

            try
            {
                var shared = textures.GetFromGame(PhantomSheet);
                var wrap = shared.GetWrapOrDefault();
                if (wrap == null)
                    return default;

                int col = jobRow % CellsPerRow;
                int row = jobRow / CellsPerRow;

                var uv0 = new Vector2(col * CellW / SheetW, row * CellH / SheetH);
                var uv1 = new Vector2((col + 1) * CellW / SheetW, (row + 1) * CellH / SheetH);

                return new Art(wrap.Handle, uv0, uv1, CellW / CellH);
            }
            catch (Exception)
            {
                return default;
            }
        }
    }
}
