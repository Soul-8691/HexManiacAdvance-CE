using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class CardGraphicsImportTests : BaseViewModelTestClass {
      public CardGraphicsImportTests() : base(0x4000) { }

      private void SetupTables() {
         Model.SetList(Token, "cardgraphicsindexes", "A", "B");
         Model.SetList(Token, "cardgraphicsiconsmallindexes", "A1", "A2", "B1", "B2");
         Model.SetList(Token, "types", "None", "Effect");

         ViewPort.Edit("@0000 ^graphics.cards.sprites[`ucs6x10x10|graphics.cards.palettes`]cardgraphicsindexes ");
         ViewPort.Edit("@2600 ^graphics.cards.palettes[`ucp6`]cardgraphicsindexes ");
         ViewPort.Edit("@2700 ^graphics.cards.icons.large[`ucs8x4x6|graphics.cards.icons.palette`]cardgraphicsindexes ");
         ViewPort.Edit("@3300 ^graphics.cards.icons.small[`ucs8x3x3|graphics.cards.icons.palette`]cardgraphicsiconsmallindexes ");
         ViewPort.Edit("@3C00 ^graphics.cards.icons.palette`ucp4:012345678` ");

         ViewPort.Edit("@3D20 ^data.cards.art[unknown: art:]2 ");
         Model.WriteMultiByteValue(0x3D20, 2, Token, 0); // row 0 unknown
         Model.WriteMultiByteValue(0x3D22, 2, Token, 0); // row 0 art = gfx index 0

         ViewPort.Edit("@3D30 ^data.cards.stats.internal[type:types]2 ");
         Model.WriteMultiByteValue(0x3D30, 2, Token, 1); // row 0 type = Effect -> effect_monster templates
      }

      private static short SolidColor => (short)((10 << 10) | (20 << 5) | 5);

      private bool StubDecode(byte[] data, out short[] image, out int width, out int height) {
         width = height = 4;
         image = Enumerable.Repeat(SolidColor, 16).ToArray();
         return true;
      }

      [Fact]
      public void CanImport_RequiredAnchorsExist_True() {
         SetupTables();
         Assert.True(CardGraphicsImport.CanImport(Model));
      }

      [Fact]
      public void Import_MissingArtTable_Fails() {
         Assert.False(CardGraphicsImport.CanImport(Model));
      }

      [Fact]
      public void Import_WritesSolidCardArtToSingleQuantizedColor() {
         SetupTables();
         FileSystem.TryDecodeImageStub = StubDecode;

         var result = CardGraphicsImport.Import(Model, Token, FileSystem, 0, new byte[] { 1, 2, 3 });

         Assert.True(result.Success, result.Message);
         var spritesRun = (ISpriteRun)Model.GetNextRun(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.sprites"));
         var pixels = spritesRun.GetPixels(Model, 0, -1);
         var firstIndex = pixels[0, 0];
         for (int y = 0; y < pixels.GetLength(1); y++) {
            for (int x = 0; x < pixels.GetLength(0); x++) Assert.Equal(firstIndex, pixels[x, y]);
         }

         var palettesRun = (IPaletteRun)Model.GetNextRun(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.palettes"));
         var palette = palettesRun.GetPalette(Model, 0);
         Assert.True(palette.Count >= 1);
      }

      [Fact]
      public void Import_IconPixelsNeverUseReservedPaletteIndices() {
         SetupTables();
         FileSystem.TryDecodeImageStub = StubDecode;

         var result = CardGraphicsImport.Import(Model, Token, FileSystem, 0, new byte[] { 1, 2, 3 });
         Assert.True(result.Success, result.Message);

         var largeRun = (ISpriteRun)Model.GetNextRun(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.icons.large"));
         AssertNoReservedIndices(largeRun.GetPixels(Model, 0, -1));

         var smallRun = (ISpriteRun)Model.GetNextRun(Model.GetAddressFromAnchor(Token, -1, "graphics.cards.icons.small"));
         AssertNoReservedIndices(smallRun.GetPixels(Model, 0, -1)); // straight, row 2*0
         AssertNoReservedIndices(smallRun.GetPixels(Model, 1, -1)); // side, row 2*0+1
      }

      private static void AssertNoReservedIndices(int[,] pixels) {
         for (int y = 0; y < pixels.GetLength(1); y++) {
            for (int x = 0; x < pixels.GetLength(0); x++) {
               var value = pixels[x, y];
               Assert.True(value == 0 || value >= 16, $"Pixel at ({x},{y}) used reserved palette index {value}.");
            }
         }
      }

      [Fact]
      public void Import_DoesNotModifySharedIconPalette() {
         SetupTables();
         FileSystem.TryDecodeImageStub = StubDecode;

         var iconPaletteAddress = Model.GetAddressFromAnchor(Token, -1, "graphics.cards.icons.palette");
         var before = Model.RawData.Skip(iconPaletteAddress).Take(0x120).ToArray();

         var result = CardGraphicsImport.Import(Model, Token, FileSystem, 0, new byte[] { 1, 2, 3 });
         Assert.True(result.Success, result.Message);

         var after = Model.RawData.Skip(iconPaletteAddress).Take(0x120).ToArray();
         Assert.Equal(before, after);
      }

      [Fact]
      public void Import_UnsupportedCardType_Fails() {
         SetupTables();
         FileSystem.TryDecodeImageStub = StubDecode;
         Model.WriteMultiByteValue(0x3D30, 2, Token, 0); // "None" - index 0 has no template mapping... actually 0 is normal_monster; use an out-of-range instead
         Model.WriteMultiByteValue(0x3D30, 2, Token, 10); // one of the reserved "None" type slots (10-12) has no template

         var result = CardGraphicsImport.Import(Model, Token, FileSystem, 0, new byte[] { 1, 2, 3 });

         Assert.False(result.Success);
      }
   }
}
