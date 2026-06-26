using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class InlineImageTableTests : BaseViewModelTestClass {
      public InlineImageTableTests() : base(0x400) { }

      private void SetupDuelistTables() {
         Model.SetList(Token, "duelists", "Yugi", "Kaiba");
         ViewPort.Goto.Execute(0x00);
         ViewPort.Edit("^graphics.duelists.icons.palettes[`ucp4`]duelists ");
         ViewPort.Goto.Execute(0x40);
         ViewPort.Edit("^graphics.duelists.icons.sprites[`ucs4x3x3|graphics.duelists.icons.palettes`]duelists ");
      }

      [Fact]
      public void BarePaletteFormat_TableLengthFromList_CreatesInlinePaletteTableRun() {
         SetupDuelistTables();

         Assert.Empty(Errors);
         var run = Model.GetNextRun(0x00);
         var paletteTable = Assert.IsType<InlinePaletteTableRun>(run);
         Assert.Equal(2, paletteTable.ElementCount); // "duelists" list has 2 entries
         Assert.Equal(32, paletteTable.ElementLength); // 4bpp, 1 page = 16 colors * 2 bytes
         Assert.Equal(64, paletteTable.Length);
         Assert.Equal(2, paletteTable.Pages);
      }

      private void SetupDuelistTables6bpp() {
         // 6bpp sprites index 0-63 directly, so they need `ucp6` (one combined 64-color
         // palette), not `ucp4:0123` (4 separately-addressable 16-color pages) - mirrors
         // SetupDuelistTables but pairs a 6bpp inline sprite table with a `ucp6` inline
         // palette table. 1x1 tiles (not 3x3, like the 4bpp version) to keep this test's
         // footprint well inside the 0x400-byte test model.
         Model.SetList(Token, "duelists", "Yugi", "Kaiba");
         ViewPort.Goto.Execute(0x00);
         ViewPort.Edit("^graphics.duelists.icons.palettes6[`ucp6`]duelists ");
         ViewPort.Goto.Execute(0x100);
         ViewPort.Edit("^graphics.duelists.icons.sprites6[`ucs6x1x1|graphics.duelists.icons.palettes6`]duelists ");
      }

      [Fact]
      public void BarePaletteFormat6bpp_TableLengthFromList_CreatesInlinePaletteTableRunWithCombined64Colors() {
         SetupDuelistTables6bpp();

         Assert.Empty(Errors);
         var run = Model.GetNextRun(0x00);
         var paletteTable = Assert.IsType<InlinePaletteTableRun>(run);
         Assert.Equal(2, paletteTable.ElementCount);
         Assert.Equal(128, paletteTable.ElementLength); // 6bpp combined: 64 colors * 2 bytes
         Assert.Equal(256, paletteTable.Length);
         Assert.Equal(64, paletteTable.GetPalette(Model, 0).Count);
      }

      [Fact]
      public void FindRelatedPalettes_ResolvesCombined6bppPaletteTableFromHint() {
         SetupDuelistTables6bpp();
         var spriteRun = (ISpriteRun)Model.GetNextRun(0x100);

         var related = spriteRun.FindRelatedPalettes(Model);

         Assert.Equal(1, related.Count);
         Assert.IsType<InlinePaletteTableRun>(related[0]);
         Assert.Equal(0x00, related[0].Start);
      }

      [Fact]
      public void BareSpriteFormat_TableLengthFromList_CreatesInlineSpriteTableRun() {
         SetupDuelistTables();

         Assert.Empty(Errors);
         var run = Model.GetNextRun(0x40);
         var spriteTable = Assert.IsType<InlineSpriteTableRun>(run);
         Assert.Equal(2, spriteTable.ElementCount);
         Assert.Equal(288, spriteTable.ElementLength); // 4bpp, 3x3 tiles -> 3*3*8*4 bytes
         Assert.Equal(576, spriteTable.Length);
         Assert.Equal(2, spriteTable.Pages);
      }

      [Fact]
      public void InlineSpriteTableRun_SetPixelsOnOneRow_DoesNotAffectOtherRows() {
         SetupDuelistTables();
         var spriteRun = (ISpriteRun)Model.GetNextRun(0x40);

         var pixels = new int[24, 24];
         pixels[0, 0] = 5;
         spriteRun.SetPixels(Model, Token, 1, pixels);

         var row1 = spriteRun.GetPixels(Model, 1, -1);
         var row0 = spriteRun.GetPixels(Model, 0, -1);
         Assert.Equal(5, row1[0, 0]);
         Assert.Equal(0, row0[0, 0]); // untouched
      }

      [Fact]
      public void InlinePaletteTableRun_SetPaletteOnOneRow_DoesNotAffectOtherRows() {
         SetupDuelistTables();
         var paletteRun = (IPaletteRun)Model.GetNextRun(0x00);

         var colors = new short[16];
         colors[0] = 0x1234;
         paletteRun.SetPalette(Model, Token, 1, colors);

         var row1 = paletteRun.GetPalette(Model, 1);
         var row0 = paletteRun.GetPalette(Model, 0);
         Assert.Equal((short)0x1234, row1[0]);
         Assert.Equal((short)0, row0[0]); // untouched
      }

      [Fact]
      public void FindRelatedPalettes_ResolvesInlinePaletteTableRunFromHint() {
         SetupDuelistTables();
         var spriteRun = (ISpriteRun)Model.GetNextRun(0x40);

         var related = spriteRun.FindRelatedPalettes(Model);

         Assert.Equal(1, related.Count);
         Assert.IsType<InlinePaletteTableRun>(related[0]);
         Assert.Equal(0x00, related[0].Start);
      }

      [Fact]
      public void FormatString_DoesNotDuplicateLengthSuffix() {
         // format is the *entire* original string (already ending in "duelists") - concatenating
         // length again produced "...duelistsduelists".
         SetupDuelistTables();

         var paletteRun = Model.GetNextRun(0x00);
         var spriteRun = Model.GetNextRun(0x40);

         Assert.DoesNotContain("duelistsduelists", paletteRun.FormatString);
         Assert.DoesNotContain("duelistsduelists", spriteRun.FormatString);
         Assert.EndsWith("duelists", paletteRun.FormatString);
         Assert.EndsWith("duelists", spriteRun.FormatString);
      }

      [Fact]
      public void ClickingInlineSpriteTable_SwitchesToSpriteToolEvenIfTableToolWasSelected() {
         // InlineSpriteTableRun is both an ITableRun and an ISpriteRun - the row IS the sprite
         // directly, with nothing else worth viewing via the table tool, so selecting it should
         // always show the Sprite Tool, not get stuck on whatever tool was selected before.
         SetupDuelistTables();
         ViewPort.Tools.SelectedTool = ViewPort.Tools.TableTool;

         ViewPort.UpdateToolsFromSelection(0x40);

         Assert.Same(ViewPort.Tools.SpriteTool, ViewPort.Tools.SelectedTool);
      }

      [Fact]
      public void DoubleClickingRow1_UpdatesSpriteToolToRow1NotJustRow0() {
         // selecting a byte within row 1 (not row 0) must move the Sprite Tool to row 1 - it
         // used to always jump to the table's own Start (row 0) regardless of which row was
         // actually clicked, since SpriteAddress doesn't carry row granularity by itself.
         SetupDuelistTables();
         var tool = ViewPort.Tools.SpriteTool;
         var row1Start = 0x40 + 288; // InlineSpriteTableRun's ElementLength

         ViewPort.UpdateToolsFromSelection(row1Start);

         Assert.Equal(0x40, tool.SpriteAddress);
         Assert.Equal(1, tool.SpritePage);
         Assert.Equal(1, tool.PalettePage);
      }

      [Fact]
      public void DoubleClickingPaletteRow1_UpdatesSpriteToolPalettePageToRow1() {
         SetupDuelistTables();
         var tool = ViewPort.Tools.SpriteTool;
         var row1Start = 0x00 + 32; // InlinePaletteTableRun's ElementLength

         ViewPort.UpdateToolsFromSelection(row1Start);

         Assert.Equal(0x00, tool.PaletteAddress);
         Assert.Equal(1, tool.PalettePage);
      }

      [Fact]
      public void SpriteTool_SteppingSpritePage_StepsPalettePageInLockstep() {
         SetupDuelistTables();
         var tool = ViewPort.Tools.SpriteTool;

         tool.SpriteAddress = 0x40;
         Assert.Equal(0, tool.SpritePage);
         Assert.Equal(0x00, tool.PaletteAddress);
         Assert.Equal(0, tool.PalettePage);

         tool.NextSpritePage.Execute(null);

         Assert.Equal(1, tool.SpritePage);
         Assert.Equal(1, tool.PalettePage); // synced because palPages (2) == spritePages (2)

         tool.PreviousSpritePage.Execute(null);

         Assert.Equal(0, tool.SpritePage);
         Assert.Equal(0, tool.PalettePage);
      }
   }
}
