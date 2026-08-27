using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using System.Collections.Generic;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   /// <summary>
   /// Covers importing an indexed PNG (with its own embedded palette, like a GraphicsGale export)
   /// through the Sprite Tool onto a "row-paired" inline sprite/palette table - one full,
   /// independent palette per row, e.g. graphics.cards.sprites/.palettes (6bpp, 64 colors/row).
   /// Regression coverage for two bugs: the import gate hardcoded 16-colors-per-page (rejecting
   /// any embedded palette bigger than that, silently discarding it and re-quantizing instead),
   /// and the direct-write path assumed page 0 always (Pages != 1 forced a fallback that also
   /// discarded the exact imported palette) instead of writing to the row currently being edited.
   /// </summary>
   public class SpriteToolCardArtImportTests : BaseViewModelTestClass {
      public SpriteToolCardArtImportTests() : base(0x4000) { }

      private void SetupTables() {
         Model.SetList(Token, "cardgraphicsindexes", "A", "B");
         ViewPort.Edit("@0000 ^graphics.cards.sprites[`ucs6x10x10|graphics.cards.palettes`]cardgraphicsindexes ");
         ViewPort.Edit("@2600 ^graphics.cards.palettes[`ucp6`]cardgraphicsindexes ");
      }

      private static (int[,] pixels, IReadOnlyList<short> palette) BuildTestImage() {
         var palette = new short[64];
         for (int i = 0; i < 64; i++) palette[i] = (short)i; // 64 distinct, recognizable colors
         var pixels = new int[80, 80];
         for (int y = 0; y < 80; y++) {
            for (int x = 0; x < 80; x++) pixels[x, y] = (x + y) % 64;
         }
         return (pixels, palette);
      }

      [Fact]
      public void ImportSpriteAndPalette_IndexedPngWith64ColorPalette_WritesExactPaletteToCurrentRow() {
         SetupTables();
         var spriteTool = ViewPort.Tools.SpriteTool;
         spriteTool.SpriteAddress = Model.GetAddressFromAnchor(Token, -1, "graphics.cards.sprites");
         spriteTool.SpritePage = 1; // row "B" - not the first row, so a page-0-hardcoded bug would miss it

         var (pixels, palette) = BuildTestImage();
         var filename = "dummy.png";
         FileSystem.TryLoadIndexImage = (ref string fileName, out int[,] image, out IReadOnlyList<short> pal) => {
            fileName = filename;
            image = pixels;
            pal = palette;
            return true;
         };

         spriteTool.ImportSpriteAndPalette(FileSystem);

         var spritesRun = (ISpriteRun)Model.GetNextRun(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.sprites"));
         var palettesRun = (IPaletteRun)Model.GetNextRun(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.palettes"));

         // the imported palette must land, unmodified, on the row that was being edited (row 1)...
         var writtenPalette = palettesRun.GetPalette(Model, 1);
         Assert.Equal(palette, writtenPalette);

         // ...and the pixel data for that same row must match what was imported.
         var writtenPixels = spritesRun.GetPixels(Model, 1, -1);
         for (int y = 0; y < 80; y++) {
            for (int x = 0; x < 80; x++) Assert.Equal(pixels[x, y], writtenPixels[x, y]);
         }

         // row 0 must be untouched.
         var untouchedPalette = palettesRun.GetPalette(Model, 0);
         foreach (var color in untouchedPalette) Assert.Equal(0, color);
      }
   }
}
