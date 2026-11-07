using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using System.Collections.Generic;
using System.Linq;
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

      // enough rows (5 x 64 colours >= 256) that a 256-entry PNG palette takes the indexed path, as with EDS's
      // 1436-row graphics.cards.images/.palettes; 16x16 sprites keep it small
      private void Import(int[,] pixels, IReadOnlyList<short> palette) {
         Model.SetList(Token, "cardgraphicsindexes", "A", "B", "C", "D", "E");
         ViewPort.Edit("@0000 ^graphics.cards.sprites[`ucs6x2x2|graphics.cards.palettes`]cardgraphicsindexes ");
         ViewPort.Edit("@1000 ^graphics.cards.palettes[`ucp6`]cardgraphicsindexes ");
         var spriteTool = ViewPort.Tools.SpriteTool;
         spriteTool.SpriteAddress = Model.GetAddressFromAnchor(Token, -1, "graphics.cards.sprites");
         spriteTool.SpritePage = 1;
         FileSystem.TryLoadIndexImage = (ref string fileName, out int[,] image, out IReadOnlyList<short> pal) => {
            fileName = "dummy.png";
            image = pixels;
            pal = palette;
            return true;
         };
         spriteTool.ImportSpriteAndPalette(FileSystem);
      }

      // An indexed PNG saved with a 256-entry palette (e.g. EDS card art, `ucp6`: 64 colours a row) used to
      // overrun the palette row (IndexOutOfRangeException in PaletteRun.SetPalette).
      [Fact]
      public void ImportSpriteAndPalette_256EntryPaletteUsing64Colors_KeepsFirst64() {
         var palette64 = new short[64];
         for (int i = 0; i < 64; i++) palette64[i] = (short)i;
         var pixels = new int[16, 16];
         for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) pixels[x, y] = (x + y * 3) % 64;
         var palette = new short[256];
         for (int i = 0; i < 256; i++) palette[i] = i < 64 ? palette64[i] : (short)0x7FFF;

         Import(pixels, palette);

         var palettesRun = (IPaletteRun)Model.GetNextRun(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.palettes"));
         Assert.Equal(palette64, palettesRun.GetPalette(Model, 1));
         var spritesRun = (ISpriteRun)Model.GetNextRun(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.sprites"));
         Assert.Equal(pixels[5, 7], spritesRun.GetPixels(Model, 1, -1)[5, 7]);
         Assert.Equal(0, Model.ReadMultiByteValue(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.palettes"), 2)); // row 0 untouched
      }

      [Fact]
      public void ImportSpriteAndPalette_256ColorImage_RequantizedTo64() {
         var palette = new short[256];
         for (int i = 0; i < 256; i++) palette[i] = (short)((i & 0x1F) << 10 | (i >> 3 & 0x1F) << 5 | (31 - (i & 0x1F)));
         var pixels = new int[16, 16];
         for (int y = 0; y < 16; y++) for (int x = 0; x < 16; x++) pixels[x, y] = x * 16 + y; // all 256 colours

         Import(pixels, palette);

         var spritesRun = (ISpriteRun)Model.GetNextRun(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.sprites"));
         var written = spritesRun.GetPixels(Model, 1, -1);
         foreach (var p in written) Assert.InRange(p, 0, 63);
         Assert.Contains(written.Cast<int>(), p => p != 0);
      }

      // a true-colour PNG (TryLoadIndexedImage fails, LoadImage gives the colours) onto a 2-row table: before, the
      // multi-page path spread its colours over every row's palette
      private void ImportTrueColor(short[] image, int rows) {
         Model.SetList(Token, "cardgraphicsindexes", Enumerable.Range(0, rows).Select(i => $"row{i}").ToArray());
         ViewPort.Edit("@0000 ^graphics.cards.sprites[`ucs6x2x2|graphics.cards.palettes`]cardgraphicsindexes ");
         ViewPort.Edit("@1000 ^graphics.cards.palettes[`ucp6`]cardgraphicsindexes ");
         var spriteTool = ViewPort.Tools.SpriteTool;
         spriteTool.SpriteAddress = Model.GetAddressFromAnchor(Token, -1, "graphics.cards.sprites");
         spriteTool.SpritePage = 1;
         FileSystem.TryLoadIndexImage = (ref string fileName, out int[,] img, out IReadOnlyList<short> pal) => {
            fileName = "dummy.png";
            (img, pal) = (null, null);
            return false;
         };
         FileSystem.LoadImage = _ => (image, 16);
         spriteTool.ImportSpriteAndPalette(FileSystem);
      }

      [Theory]
      [InlineData(2)]
      [InlineData(5)]
      public void ImportSpriteAndPalette_TrueColorOver64_QuantizedTo64OnItsRowOnly(int rows) {
         var image = new short[256];
         for (int i = 0; i < 256; i++) image[i] = (short)((i & 0x1F) << 10 | (i >> 3 & 0x1F) << 5 | (31 - (i & 0x1F)));

         ImportTrueColor(image, rows);

         var spritesRun = (ISpriteRun)Model.GetNextRun(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.sprites"));
         var palettesRun = (IPaletteRun)Model.GetNextRun(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.palettes"));
         var written = spritesRun.GetPixels(Model, 1, -1);
         foreach (var p in written) Assert.InRange(p, 0, 63);
         Assert.True(written.Cast<int>().Distinct().Count() > 32); // a real 64-colour quantization, not a few colours
         Assert.Contains(palettesRun.GetPalette(Model, 1), c => c != 0);
         Assert.All(palettesRun.GetPalette(Model, 0), c => Assert.Equal(0, c)); // other rows untouched
      }

      [Fact]
      public void ImportSpriteAndPalette_TrueColorUnder64_KeepsExactColors() {
         var colors = Enumerable.Range(0, 10).Select(i => (short)(i * 0x0C63)).ToArray();
         var image = Enumerable.Range(0, 256).Select(i => colors[i % 10]).ToArray();

         ImportTrueColor(image, 2);

         var spritesRun = (ISpriteRun)Model.GetNextRun(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.sprites"));
         var palettesRun = (IPaletteRun)Model.GetNextRun(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.palettes"));
         var palette = palettesRun.GetPalette(Model, 1);
         var written = spritesRun.GetPixels(Model, 1, -1);
         for (int i = 0; i < 256; i++) Assert.Equal(image[i], palette[written[i % 16, i / 16]]);
      }

      [Fact]
      public void FitToPalettePage_FitsAlready_Unchanged() {
         var (pixels, palette) = BuildTestImage();
         var (fitPixels, fitPalette) = HavenSoft.HexManiac.Core.ViewModels.Tools.SpriteTool.FitToPalettePage(pixels, palette, 64, 64);
         Assert.Same(pixels, fitPixels);
         Assert.Same(palette, fitPalette);
      }
   }
}
