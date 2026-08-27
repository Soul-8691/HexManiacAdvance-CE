using HavenSoft.HexManiac.Core.Models.Images;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace HavenSoft.HexManiac.Core.Models.Runs {
   /// <summary>
   /// Imports a card's main 80x80 6bpp artwork (graphics.cards.sprites/.palettes) plus its three
   /// icon variants (large, small straight, small side) from a single downloaded image. Icons are
   /// built by pasting a shrunk copy of the card art onto a type-specific template (see
   /// resources/CardIcons/, sourced from the Cyber-Twin-Dragon project) and quantizing against the
   /// existing, shared graphics.cards.icons.palette - which is never modified, only read from.
   /// </summary>
   public static class CardGraphicsImport {
      public const string SpritesName = "graphics.cards.sprites";
      public const string PalettesName = "graphics.cards.palettes";
      public const string IconsLargeName = "graphics.cards.icons.large";
      public const string IconsSmallName = "graphics.cards.icons.small";
      public const string IconsPaletteName = "graphics.cards.icons.palette";
      public const string ArtTableName = "data.cards.art";

      private const int CardArtSize = 80;
      private const int CardPaletteColors = 64;

      private const int LargeIconWidth = 32, LargeIconHeight = 48, LargeSpriteSize = 30, LargeSpriteOffsetX = 1, LargeSpriteOffsetY = 9;
      private const int SmallIconSize = 24, SmallSpriteSize = 14, SmallSpriteOffsetX = 5, SmallSpriteOffsetY = 5;

      // index -> template basename, matching data.cards.stats.internal's "type" enum (0-14) and
      // the file names bundled in resources/CardIcons/. Slots 10-12 are the ROM's unused "None"
      // type entries and have no template.
      private static readonly string[] TypeTemplateBasenames = {
         "normal_monster", "effect_monster", "fusion_monster", "fusion_effect_monster",
         "ritual_monster", "ritual_effect_monster", "toon_monster", "spirit_monster",
         "union_monster", "token", null, null, null, "spell_card", "trap_card",
      };

      public static bool CanImport(IDataModel model) =>
         model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, SpritesName) != Pointer.NULL &&
         model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, ArtTableName) != Pointer.NULL;

      public static CardImport.Result Import(IDataModel model, ModelDelta token, IFileSystem fs, int internalIndex, byte[] imageBytes) {
         var artTable = model.GetTable(ArtTableName);
         var internalTable = model.GetTable(CardImport.StatsInternalName);
         if (artTable == null) return new(false, $"{ArtTableName} not found.");
         if (internalTable == null) return new(false, $"{CardImport.StatsInternalName} not found.");
         if (internalIndex < 0 || internalIndex >= artTable.ElementCount) return new(false, "Row index out of range.");

         if (fs == null || !fs.TryDecodeImage(imageBytes, out var raw, out var width, out var height) || raw == null || width <= 0 || height <= 0) {
            return new(false, "Could not decode the downloaded card image.");
         }

         var gfxIndex = artTable.ReadValue(model, internalIndex, "art");
         var cardArt = ImageQuantizer.Resize(ImageQuantizer.ToRgbGrid(raw, width, height), CardArtSize, CardArtSize);

         if (!WriteCardArt(model, token, gfxIndex, cardArt, out var artError)) return new(false, artError);
         if (!WriteIcons(model, token, internalTable, internalIndex, gfxIndex, cardArt, out var iconError)) return new(false, iconError);

         return new(true, "Imported card art and icons.");
      }

      private static bool WriteCardArt(IDataModel model, ModelDelta token, int gfxIndex, Rgb[,] cardArt, out string error) {
         error = null;
         var spritesRun = model.GetNextRun(model.GetAddressFromAnchor(token, -1, SpritesName)) as ISpriteRun;
         var palettesRun = model.GetNextRun(model.GetAddressFromAnchor(token, -1, PalettesName)) as IPaletteRun;
         if (spritesRun == null || palettesRun == null) { error = "Card art sprite/palette tables not found."; return false; }

         var (indices, palette) = ImageQuantizer.MedianCutQuantize(cardArt, CardPaletteColors);
         spritesRun.SetPixels(model, token, gfxIndex, indices);
         palettesRun.SetPalette(model, token, gfxIndex, palette);
         return true;
      }

      private static bool WriteIcons(IDataModel model, ModelDelta token, ITableRun internalTable, int internalIndex, int gfxIndex, Rgb[,] cardArt, out string error) {
         error = null;
         var typeValue = internalTable.ReadValue(model, internalIndex, "type");
         var basename = typeValue >= 0 && typeValue < TypeTemplateBasenames.Length ? TypeTemplateBasenames[typeValue] : null;
         if (basename == null) { error = "This card's type has no icon template."; return false; }

         var largeRun = model.GetNextRun(model.GetAddressFromAnchor(token, -1, IconsLargeName)) as ISpriteRun;
         var smallRun = model.GetNextRun(model.GetAddressFromAnchor(token, -1, IconsSmallName)) as ISpriteRun;
         var iconPaletteRun = model.GetNextRun(model.GetAddressFromAnchor(token, -1, IconsPaletteName)) as IPaletteRun;
         if (largeRun == null || smallRun == null || iconPaletteRun == null) { error = "Icon graphics/palette tables not found."; return false; }

         var candidates = BuildIconPaletteCandidates(iconPaletteRun, model);

         if (!TryBuildIcon(basename, "large", LargeIconWidth, LargeIconHeight, cardArt, LargeSpriteSize, LargeSpriteOffsetX, LargeSpriteOffsetY, out var largeCanvas, out error)) return false;
         largeRun.SetPixels(model, token, gfxIndex, ImageQuantizer.QuantizeToPalette(largeCanvas, candidates));

         var smallSprite = ImageQuantizer.Resize(cardArt, SmallSpriteSize, SmallSpriteSize);

         if (!TryLoadTemplate(basename, "small", SmallIconSize, SmallIconSize, out var smallCanvas, out error)) return false;
         ImageQuantizer.Paste(smallCanvas, smallSprite, SmallSpriteOffsetX, SmallSpriteOffsetY);
         smallRun.SetPixels(model, token, gfxIndex * 2, ImageQuantizer.QuantizeToPalette(smallCanvas, candidates));

         if (!TryLoadTemplate(basename, "small_side", SmallIconSize, SmallIconSize, out var smallSideCanvas, out error)) return false;
         ImageQuantizer.Paste(smallSideCanvas, ImageQuantizer.Rotate90Clockwise(smallSprite), SmallSpriteOffsetX, SmallSpriteOffsetY);
         smallRun.SetPixels(model, token, gfxIndex * 2 + 1, ImageQuantizer.QuantizeToPalette(smallSideCanvas, candidates));

         return true;
      }

      private static bool TryBuildIcon(string basename, string label, int width, int height, Rgb[,] cardArt, int spriteSize, int offsetX, int offsetY, out Rgb[,] canvas, out string error) {
         if (!TryLoadTemplate(basename, label, width, height, out canvas, out error)) return false;
         var sprite = ImageQuantizer.Resize(cardArt, spriteSize, spriteSize);
         ImageQuantizer.Paste(canvas, sprite, offsetX, offsetY);
         return true;
      }

      private static bool TryLoadTemplate(string basename, string label, int expectedWidth, int expectedHeight, out Rgb[,] canvas, out string error) {
         canvas = null;
         error = null;
         var path = Path.Combine("resources", "CardIcons", $"{basename}_{label}.png");
         if (!File.Exists(path)) { error = $"Missing icon template: {path}"; return false; }

         var (pixels, palette) = IndexedPng.Load(path);
         canvas = ImageQuantizer.FromIndexed(pixels, palette);
         if (canvas.GetLength(0) != expectedWidth || canvas.GetLength(1) != expectedHeight) {
            canvas = ImageQuantizer.Resize(canvas, expectedWidth, expectedHeight);
         }
         return true;
      }

      /// <summary>
      /// The icon palette shares its low 16 entries (indices 1-15) with other UI chrome, so icon
      /// art may only use index 0 (background) or indices 16-143.
      /// </summary>
      private static List<(int paletteIndex, short color)> BuildIconPaletteCandidates(IPaletteRun iconPaletteRun, IDataModel model) {
         var all = iconPaletteRun.AllColors(model);
         var candidates = new List<(int, short)> { (0, all[0]) };
         for (int i = 16; i < System.Math.Min(144, all.Count); i++) candidates.Add((i, all[i]));
         return candidates;
      }
   }
}
