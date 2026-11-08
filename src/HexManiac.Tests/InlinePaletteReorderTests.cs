using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   /// <summary>
   /// Reordering (dragging) colors in the Sprite Tool's palette, for an inline palette table - one `ucp4` palette per
   /// row with its sprites in a sibling table, e.g. EDS's graphics.cards.icons.palettes / .images.
   /// </summary>
   public class InlinePaletteReorderTests : BaseViewModelTestClass {
      public InlinePaletteReorderTests() : base(0x1000) { }

      private void Setup(bool withPointer) {
         Model.SetList(Token, "cards", "A", "B", "C");
         ViewPort.Edit("@000 ^icons.palettes[`ucp4`]cards ");                     // 3 x 0x20
         ViewPort.Edit("@100 ^icons.images[`ucs4x1x1|icons.palettes`]cards ");    // 3 x 0x20 (8x8 4bpp)
         if (withPointer) ViewPort.Edit("@800 <000> ");
         for (int i = 0; i < 16; i++) Model.WriteMultiByteValue(0x20 + i * 2, 2, Token, i + 1); // row 1's colors: 1..16
         for (int i = 0; i < 0x20; i++) Model.RawData[0x120 + i] = 0x10;                      // row 1's pixels: 0 (low), 1 (high)
         ViewPort.Refresh();
         var tool = ViewPort.Tools.SpriteTool;
         tool.SpriteAddress = 0x100;
         tool.PaletteAddress = 0x000;
         tool.SpritePage = 1;
         tool.PalettePage = 1;
      }

      [Theory]
      [InlineData(false)]
      [InlineData(true)]
      public void MoveColor_InlinePaletteRow_SavesRowAndRemapsItsPixels(bool withPointer) {
         Setup(withPointer);
         var colors = ViewPort.Tools.SpriteTool.Colors;
         Assert.True(colors.CanEditColors);

         colors.SelectionStart = colors.SelectionEnd = 0;
         colors.HandleMove(0, 1); // color 0 moves to slot 1
         colors.CompleteCurrentInteraction();

         Assert.Equal(2, Model.ReadMultiByteValue(0x20, 2)); // old color 1 is now first
         Assert.Equal(1, Model.ReadMultiByteValue(0x22, 2)); // old color 0 is now second
         Assert.Equal(0x01, Model[0x120]);                    // pixels follow: 0 -> 1, 1 -> 0
         Assert.Equal(1, Model.ReadMultiByteValue(0x00, 2) + 1); // row 0 untouched (0)
         Assert.Equal(0, Model[0x100]);                       // row 0's pixels untouched
      }
   }
}
