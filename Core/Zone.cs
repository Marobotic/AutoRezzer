using System;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Enums;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace AutoRezzer.Core
{
    /// <summary>
    /// The four kinds of place this plugin can be in, as far as anybody using it cares.
    ///
    /// Deliberately coarse. There are sixty-odd values of TerritoryIntendedUse and precisely four
    /// answers to "should AutoRezzer be working here", so the sheet's vocabulary is collapsed into
    /// the player's.
    /// </summary>
    internal enum ContentKind
    {
        /// <summary>Overworld, FATEs, hunt trains, the Diadem, housing, the Gold Saucer.</summary>
        OpenWorld,

        /// <summary>Dungeons, trials, raids, deep dungeons, variant and criterion.</summary>
        Instance,

        /// <summary>Eureka, Bozja, Delubrum Reginae.</summary>
        FieldOperation,

        /// <summary>The Occult Crescent, which is a field operation with phantom jobs in it.</summary>
        OccultCrescent,
    }

    /// <summary>
    /// Where the character is, in the only two senses this plugin cares about: what KIND of content
    /// this is, and whether it is somewhere nobody can die.
    ///
    /// ── WHY INTENDED USE AND NOT A LIST OF TERRITORY IDS ──────────────────
    /// Same argument as PhantomJobs makes about the Occult Crescent director. Eureka is four zones,
    /// Bozja is two plus two raids, and there are hundreds of dungeons. A hardcoded list is a list
    /// that silently stops being true on a patch day, and the failure mode is the plugin quietly
    /// refusing to work in the newest zone, which reads like a bug rather than a stale table.
    ///
    /// TerritoryIntendedUse is the game's own classification of what a zone IS, it is already loaded
    /// in GameMain, and a new dungeon arrives carrying the Dungeon value.
    ///
    /// ── AND WHY SANCTUARY IS A FLAG RATHER THAN A ZONE TYPE ───────────────
    /// "Sanctuary" is not a kind of zone, it is a region inside one: the Crescent's base camp, the
    /// Bozjan staging area and every city are all sanctuaries, and walking twenty yalms out of one
    /// stops it being true without the territory changing at all. TerritoryInfo carries the live
    /// flag, which is the only thing that tracks that.
    /// ─────────────────────────────────────────────────────────────────────
    /// </summary>
    internal sealed class Zone
    {
        private readonly IPluginLog log;
        private readonly PhantomJobs phantom;

        public Zone(IPluginLog log, PhantomJobs phantom)
        {
            this.log = log;
            this.phantom = phantom;
        }

        /// <summary>The game's classification of the current zone, or null when it cannot be read
        /// (between areas, at the title screen, or on a client that has moved the field).</summary>
        public unsafe TerritoryIntendedUse? IntendedUse
        {
            get
            {
                try
                {
                    var game = GameMain.Instance();
                    return game == null ? null : game->CurrentTerritoryIntendedUseId;
                }
                catch (Exception ex)
                {
                    log.Debug($"[AutoRezzer] Could not read the territory's intended use: {ex.Message}");
                    return null;
                }
            }
        }

        /// <summary>
        /// Which of the four this is.
        ///
        /// The Occult Crescent's own director is asked first, because it is the check that is right
        /// for zones that do not exist yet: PhantomJobs asks the director, and the director is up
        /// wherever phantom jobs are, whatever the sheet ends up calling the territory.
        ///
        /// Delubrum Reginae counts as a field operation. It is not a separate kind of content, it is
        /// launched from inside Bozja and runs on the same lost actions, and somebody who has
        /// switched field operations on has not asked to be dropped at the door of the raid they
        /// queued for from the field.
        ///
        /// Anything unrecognised is treated as open world, which is the forgiving direction: a zone
        /// this plugin has never heard of behaves like the overworld rather than going silent.
        /// </summary>
        public ContentKind Kind
        {
            get
            {
                if (phantom.InOccultCrescent)
                    return ContentKind.OccultCrescent;

                return IntendedUse switch
                {
                    TerritoryIntendedUse.OccultCrescent => ContentKind.OccultCrescent,

                    TerritoryIntendedUse.Eureka => ContentKind.FieldOperation,
                    TerritoryIntendedUse.Bozja => ContentKind.FieldOperation,
                    TerritoryIntendedUse.DelubrumReginae => ContentKind.FieldOperation,
                    TerritoryIntendedUse.DelubrumReginaeSavage => ContentKind.FieldOperation,

                    TerritoryIntendedUse.Dungeon => ContentKind.Instance,
                    TerritoryIntendedUse.VariantDungeon => ContentKind.Instance,
                    TerritoryIntendedUse.BeforeTrialDung => ContentKind.Instance,
                    TerritoryIntendedUse.Trial => ContentKind.Instance,
                    TerritoryIntendedUse.AllianceRaid => ContentKind.Instance,
                    TerritoryIntendedUse.Raid1 => ContentKind.Instance,
                    TerritoryIntendedUse.Raid2 => ContentKind.Instance,
                    TerritoryIntendedUse.ChaoticRaid => ContentKind.Instance,
                    TerritoryIntendedUse.DeepDungeon => ContentKind.Instance,
                    TerritoryIntendedUse.CriterionDungeon => ContentKind.Instance,
                    TerritoryIntendedUse.CriterionDungeonSavage => ContentKind.Instance,
                    TerritoryIntendedUse.SoloDuty => ContentKind.Instance,
                    TerritoryIntendedUse.SoloOverworldInstances => ContentKind.Instance,
                    TerritoryIntendedUse.MaskedCarnival => ContentKind.Instance,
                    TerritoryIntendedUse.Elysion => ContentKind.Instance,

                    _ => ContentKind.OpenWorld,
                };
            }
        }

        /// <summary>What to call the current kind on screen, in the same words the switch uses.</summary>
        public static string NameOf(ContentKind kind) => kind switch
        {
            ContentKind.Instance => "Duties",
            ContentKind.FieldOperation or ContentKind.OccultCrescent => "Field operations",
            _ => "Open world and FATEs",
        };

        /// <summary>
        /// Whether the character is standing in a sanctuary: a rested area, where the game will not
        /// let anybody take damage and so cannot let anybody die.
        ///
        /// NOT A SETTING, and it never was one worth offering. Read live rather than per-zone: the
        /// Crescent's base camp and the Bozjan staging area are sanctuaries inside zones that are
        /// mostly not, and this goes false the moment you step out of one without any territory
        /// change to notice.
        /// </summary>
        public unsafe bool InSanctuary
        {
            get
            {
                try
                {
                    var info = TerritoryInfo.Instance();
                    return info != null && info->InSanctuary;
                }
                catch (Exception ex)
                {
                    // The safe answer is "no". Answering "yes" on a failed read would switch the
                    // plugin off everywhere for anybody whose client moved the flag.
                    log.Debug($"[AutoRezzer] Could not read the sanctuary flag: {ex.Message}");
                    return false;
                }
            }
        }
    }
}
