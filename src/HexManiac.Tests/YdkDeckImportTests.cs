using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class YdkDeckImportTests : BaseViewModelTestClass {
      [Fact]
      public void ArrayRunEnumSegment_PipeSuffix_ParsesAsPasswordTableHintAndRoundTrips() {
         var segment = new ArrayRunEnumSegment("card", 2, "cardnames|data.cards.passwords");

         Assert.Equal("cardnames", segment.EnumName);
         Assert.Equal("data.cards.passwords", segment.PasswordTableHint);
         Assert.Equal("card:cardnames|data.cards.passwords", segment.SerializeFormat);
      }

      [Fact]
      public void ArrayRunEnumSegment_NoPipeSuffix_PasswordTableHintIsNull() {
         var segment = new ArrayRunEnumSegment("card", 2, "cardnames");

         Assert.Null(segment.PasswordTableHint);
         Assert.Equal("card:cardnames", segment.SerializeFormat);
      }

      // data.cards.passwords has no name field of its own - its row count (and so its per-row
      // ElementNames labels) comes from the cardstatsnames list, exactly like the real TOML:
      //   Format = '[password::|h]cardstatsnames'
      // cardnames is a *separate*, differently-ordered list used for display/storage - the
      // whole point of these tests is that resolving must go through cardstatsnames's label,
      // not the password table's raw row index, since that index means nothing in cardnames.
      //
      // passwords, in row order: 89631139(BEW) 12345678(DDM) 00000001(BB1) 99999999(RA)
      // cardstatsnames (aligned with passwords): BEW DDM BB1 RA
      // cardnames (separate order):              RA  BB1 DDM BEW  -> RA=0 BB1=1 DDM=2 BEW=3
      private void SetupCardTables() {
         Model.SetList(Token, "cardstatsnames", "BEW", "DDM", "BB1", "RA");
         Model.SetList(Token, "cardnames", "RA", "BB1", "DDM", "BEW");
         ViewPort.Edit("@00 ^data.cards.passwords[password::|h]cardstatsnames 89631139 12345678 00000001 99999999 ");
      }

      private const string Ydk =
         "#created by test\n" +
         "#main\n" +
         "99999999\n" +
         "89631139\n" +
         "00000001\n" +
         "#extra\n" +
         "12345678\n";

      [Fact]
      public void ImportYdk_NoSectionNameMatch_ConcatenatesAllSectionsAndClampsToTableLength() {
         SetupCardTables();
         ViewPort.Edit("@20 ^data.decks.test[card:cardnames|data.cards.passwords]3 0 0 0 ");
         var table = Model.GetTable("data.decks.test");
         var segment = (ArrayRunEnumSegment)table.ElementContent[0];

         var result = YdkDeckImport.Import(Model, Token, table, segment, Ydk);

         Assert.Equal(3, result.Imported);
         Assert.Equal(0, result.Skipped);
         Assert.Equal(0, Model.ReadMultiByteValue(0x20, 2)); // 99999999 -> RA -> cardnames index 0
         Assert.Equal(3, Model.ReadMultiByteValue(0x22, 2)); // 89631139 -> BEW -> cardnames index 3
         Assert.Equal(1, Model.ReadMultiByteValue(0x24, 2)); // 00000001 -> BB1 -> cardnames index 1
      }

      [Fact]
      public void ImportYdk_AnchorNamedMain_OnlyUsesMainSection() {
         SetupCardTables();
         ViewPort.Edit("@20 ^data.decks.main[card:cardnames|data.cards.passwords]3 0 0 0 ");
         var table = Model.GetTable("data.decks.main");
         var segment = (ArrayRunEnumSegment)table.ElementContent[0];

         var result = YdkDeckImport.Import(Model, Token, table, segment, Ydk);

         Assert.Equal(3, result.Imported);
         Assert.Equal(0, Model.ReadMultiByteValue(0x20, 2)); // 99999999 -> RA -> cardnames index 0
         Assert.Equal(3, Model.ReadMultiByteValue(0x22, 2)); // 89631139 -> BEW -> cardnames index 3
         Assert.Equal(1, Model.ReadMultiByteValue(0x24, 2)); // 00000001 -> BB1 -> cardnames index 1
      }

      [Fact]
      public void ImportYdk_AnchorNamedExtra_OnlyUsesExtraSection() {
         SetupCardTables();
         ViewPort.Edit("@20 ^data.decks.extra[card:cardnames|data.cards.passwords]3 5 5 5 ");
         var table = Model.GetTable("data.decks.extra");
         var segment = (ArrayRunEnumSegment)table.ElementContent[0];

         var result = YdkDeckImport.Import(Model, Token, table, segment, Ydk);

         Assert.Equal(1, result.Imported);
         Assert.Equal(2, Model.ReadMultiByteValue(0x20, 2)); // 12345678 -> DDM -> cardnames index 2
         Assert.Equal(5, Model.ReadMultiByteValue(0x22, 2)); // untouched, no second extra card
         Assert.Equal(5, Model.ReadMultiByteValue(0x24, 2)); // untouched
      }

      [Fact]
      public void ImportYdk_FewerCardsThanSlots_LeavesRemainderUnchanged() {
         SetupCardTables();
         ViewPort.Edit("@20 ^data.decks.main[card:cardnames|data.cards.passwords]3 6 7 8 ");
         var table = Model.GetTable("data.decks.main");
         var segment = (ArrayRunEnumSegment)table.ElementContent[0];

         var result = YdkDeckImport.Import(Model, Token, table, segment, "#main\n00000001\n");

         Assert.Equal(1, result.Imported);
         Assert.Equal(1, Model.ReadMultiByteValue(0x20, 2)); // 00000001 -> BB1 -> cardnames index 1
         Assert.Equal(7, Model.ReadMultiByteValue(0x22, 2)); // left as-is
         Assert.Equal(8, Model.ReadMultiByteValue(0x24, 2)); // left as-is
      }

      [Fact]
      public void ImportYdk_UnknownPassword_SkippedAndLeftUnchanged() {
         SetupCardTables();
         ViewPort.Edit("@20 ^data.decks.main[card:cardnames|data.cards.passwords]3 9 9 9 ");
         var table = Model.GetTable("data.decks.main");
         var segment = (ArrayRunEnumSegment)table.ElementContent[0];

         var result = YdkDeckImport.Import(Model, Token, table, segment, "#main\n11111111\n00000001\n");

         Assert.Equal(1, result.Imported);
         Assert.Equal(1, result.Skipped);
         Assert.Equal(9, Model.ReadMultiByteValue(0x20, 2)); // 11111111 not found, left as-is
         Assert.Equal(1, Model.ReadMultiByteValue(0x22, 2)); // 00000001 -> BB1 -> cardnames index 1
      }

      [Fact]
      public void ImportYdk_IntoStructRunWithRepeatedInlineField_FillsEveryRepetitionNotJustOne() {
         // StructRun.ElementCount is always 1 (one struct, not a repeating table), and Expand
         // reuses the same segment instance for every repetition of an inline array - this test
         // guards against both: importing must fill every "main" slot, not stop after the first.
         SetupCardTables();
         Model.WriteMultiByteValue(0x48, 2, Token, 2); // mainCount = 2
         Model.WriteMultiByteValue(0x4A, 2, Token, 9); // main[0], will be overwritten
         Model.WriteMultiByteValue(0x4C, 2, Token, 9); // main[1], will be overwritten
         Model.WriteMultiByteValue(0x4E, 2, Token, 0); // extraCount = 0

         ViewPort.Goto.Execute(0x20);
         ViewPort.Edit("<040> ");
         ViewPort.Goto.Execute(0x20);
         ViewPort.Edit("^data.decks.offsets.actual[offset<padding:: padding:: mainCount: " +
            "[main:cardnames|data.cards.passwords]/mainCount extraCount: " +
            "[extra:cardnames|data.cards.passwords]/extraCount>]1 ");

         var structRun = (StructRun)Model.GetNextRun(0x40);
         var mainSegment = (ArrayRunEnumSegment)structRun.ElementContent[3];

         var result = YdkDeckImport.Import(Model, Token, structRun, mainSegment, "#main\n89631139\n99999999\n");

         Assert.Equal(2, result.Imported);
         Assert.Equal(3, Model.ReadMultiByteValue(0x4A, 2)); // 89631139 -> BEW -> cardnames index 3
         Assert.Equal(0, Model.ReadMultiByteValue(0x4C, 2)); // 99999999 -> RA -> cardnames index 0
      }

      private void SetupDeckStruct() {
         ViewPort.Goto.Execute(0x20);
         ViewPort.Edit("<040> ");
         ViewPort.Goto.Execute(0x20);
         ViewPort.Edit("^data.decks.offsets.actual[offset<padding:: padding:: mainCount: " +
            "[main:cardnames|data.cards.passwords]/mainCount extraCount: " +
            "[extra:cardnames|data.cards.passwords]/extraCount>]1 ");
      }

      [Fact]
      public void ImportYdk_GrowsMainArrayInPlace_WhenRoomIsAvailable() {
         SetupCardTables();
         Model.WriteMultiByteValue(0x48, 2, Token, 1); // mainCount = 1
         Model.WriteMultiByteValue(0x4A, 2, Token, 9); // main[0], will be overwritten
         Model.WriteMultiByteValue(0x4C, 2, Token, 0); // extraCount = 0
         SetupDeckStruct();
         var structRun = (StructRun)Model.GetNextRun(0x40);
         var mainSegment = (ArrayRunEnumSegment)structRun.ElementContent[3];

         var result = YdkDeckImport.Import(Model, Token, structRun, mainSegment, "#main\n89631139\n12345678\n00000001\n");

         Assert.Equal(3, result.Imported);
         Assert.Equal(0, result.Skipped);
         Assert.Equal(0x40, Model.ReadPointer(0x20)); // grew in place, no relocation needed
         Assert.Equal(3, Model.ReadMultiByteValue(0x48, 2)); // mainCount is now 3
         Assert.Equal(3, Model.ReadMultiByteValue(0x4A, 2)); // BEW
         Assert.Equal(2, Model.ReadMultiByteValue(0x4C, 2)); // DDM
         Assert.Equal(1, Model.ReadMultiByteValue(0x4E, 2)); // BB1
         Assert.Equal(0, Model.ReadMultiByteValue(0x50, 2)); // extraCount preserved at its new (shifted) offset
      }

      [Fact]
      public void ImportYdk_RelocatesWhenGrowthDoesNotFit_AndFixesUpPointer() {
         SetupCardTables();
         Model.WriteMultiByteValue(0x48, 2, Token, 1); // mainCount = 1
         Model.WriteMultiByteValue(0x4A, 2, Token, 9); // main[0]
         Model.WriteMultiByteValue(0x4C, 2, Token, 0); // extraCount = 0
         Model[0x4E] = 0x01; // block in-place growth
         SetupDeckStruct();
         var structRun = (StructRun)Model.GetNextRun(0x40);
         var mainSegment = (ArrayRunEnumSegment)structRun.ElementContent[3];

         var result = YdkDeckImport.Import(Model, Token, structRun, mainSegment, "#main\n89631139\n12345678\n00000001\n");

         Assert.Equal(3, result.Imported);
         var newAddress = Model.ReadPointer(0x20);
         Assert.NotEqual(0x40, newAddress); // pointer was fixed up to the relocated struct
         Assert.Equal(3, Model.ReadMultiByteValue(newAddress + 8, 2)); // mainCount
         Assert.Equal(3, Model.ReadMultiByteValue(newAddress + 10, 2)); // BEW
         Assert.Equal(2, Model.ReadMultiByteValue(newAddress + 12, 2)); // DDM
         Assert.Equal(1, Model.ReadMultiByteValue(newAddress + 14, 2)); // BB1
         Assert.Equal(0, Model.ReadMultiByteValue(newAddress + 16, 2)); // extraCount preserved
         Assert.Equal(0xFF, Model[0x40]); // old location cleared
      }

      [Fact]
      public void ImportYdk_ShrinksArray_ClearsTrailingBytes() {
         SetupCardTables();
         Model.WriteMultiByteValue(0x48, 2, Token, 3); // mainCount = 3
         Model.WriteMultiByteValue(0x4A, 2, Token, 9);
         Model.WriteMultiByteValue(0x4C, 2, Token, 9);
         Model.WriteMultiByteValue(0x4E, 2, Token, 9);
         Model.WriteMultiByteValue(0x50, 2, Token, 0); // extraCount = 0
         SetupDeckStruct();
         var structRun = (StructRun)Model.GetNextRun(0x40);
         var mainSegment = (ArrayRunEnumSegment)structRun.ElementContent[3];

         var result = YdkDeckImport.Import(Model, Token, structRun, mainSegment, "#main\n00000001\n");

         Assert.Equal(1, result.Imported);
         Assert.Equal(1, Model.ReadMultiByteValue(0x48, 2)); // mainCount is now 1
         Assert.Equal(1, Model.ReadMultiByteValue(0x4A, 2)); // BB1
         Assert.Equal(0, Model.ReadMultiByteValue(0x4C, 2)); // extraCount preserved at its new (shifted) offset
         Assert.Equal(0, Model[0x4E]); // stale trailing byte cleared
         Assert.Equal(0, Model[0x50]); // stale trailing byte cleared
      }

      // Reproduces "changing 40 to 41 manually doesn't expand the deck": typing a new count field
      // value directly (no .ydk import involved at all) must also grow/shrink+repoint the array,
      // via NotifyChildren -> StructRun.Resize.
      [Fact]
      public void EditMainCountFieldDirectly_GrowsArrayAndZeroFillsNewSlot() {
         SetupCardTables();
         Model.WriteMultiByteValue(0x48, 2, Token, 1); // mainCount = 1
         Model.WriteMultiByteValue(0x4A, 2, Token, 3); // main[0] = BEW
         Model.WriteMultiByteValue(0x4C, 2, Token, 0); // extraCount = 0
         SetupDeckStruct();

         var fieldViewModel = new FieldArrayElementViewModel(ViewPort, "mainCount", 0x48, 2, new NumericFieldStrategy());
         fieldViewModel.Content = "2";

         Assert.Equal(0x40, Model.ReadPointer(0x20)); // grew in place, no relocation needed
         Assert.Equal(2, Model.ReadMultiByteValue(0x48, 2)); // mainCount is now 2
         Assert.Equal(3, Model.ReadMultiByteValue(0x4A, 2)); // BEW - untouched
         Assert.Equal(0, Model.ReadMultiByteValue(0x4C, 2)); // new slot, zero-filled
         Assert.Equal(0, Model.ReadMultiByteValue(0x4E, 2)); // extraCount preserved at its new (shifted) offset
      }

      [Fact]
      public void EditMainCountFieldDirectly_RelocatesWhenGrowthDoesNotFit() {
         SetupCardTables();
         Model.WriteMultiByteValue(0x48, 2, Token, 1); // mainCount = 1
         Model.WriteMultiByteValue(0x4A, 2, Token, 3); // main[0] = BEW
         Model.WriteMultiByteValue(0x4C, 2, Token, 0); // extraCount = 0
         Model[0x4E] = 0x01; // block in-place growth
         SetupDeckStruct();

         var fieldViewModel = new FieldArrayElementViewModel(ViewPort, "mainCount", 0x48, 2, new NumericFieldStrategy());
         fieldViewModel.Content = "2";

         var newAddress = Model.ReadPointer(0x20);
         Assert.NotEqual(0x40, newAddress); // pointer was fixed up to the relocated struct
         Assert.Equal(2, Model.ReadMultiByteValue(newAddress + 8, 2)); // mainCount
         Assert.Equal(3, Model.ReadMultiByteValue(newAddress + 10, 2)); // BEW - untouched
         Assert.Equal(0, Model.ReadMultiByteValue(newAddress + 12, 2)); // new slot, zero-filled
         Assert.Equal(0, Model.ReadMultiByteValue(newAddress + 14, 2)); // extraCount preserved
      }

      [Fact]
      public void EditMainCountFieldDirectly_Shrinking_TruncatesAndClearsTrailingBytes() {
         SetupCardTables();
         Model.WriteMultiByteValue(0x48, 2, Token, 2); // mainCount = 2
         Model.WriteMultiByteValue(0x4A, 2, Token, 3); // main[0] = BEW
         Model.WriteMultiByteValue(0x4C, 2, Token, 1); // main[1] = BB1
         Model.WriteMultiByteValue(0x4E, 2, Token, 0); // extraCount = 0
         SetupDeckStruct();

         var fieldViewModel = new FieldArrayElementViewModel(ViewPort, "mainCount", 0x48, 2, new NumericFieldStrategy());
         fieldViewModel.Content = "1";

         Assert.Equal(1, Model.ReadMultiByteValue(0x48, 2)); // mainCount is now 1
         Assert.Equal(3, Model.ReadMultiByteValue(0x4A, 2)); // BEW - untouched
         Assert.Equal(0, Model.ReadMultiByteValue(0x4C, 2)); // extraCount preserved at its new (shifted) offset
         Assert.Equal(0, Model[0x4E]); // stale trailing byte cleared
      }

      [Fact]
      public void ImportYdk_SharedInnerFieldName_ImportsBothMainAndExtraTogether() {
         // matches the real-world format where both inline arrays' inner field is named "card"
         // (not uniquely "main"/"extra" like SetupDeckStruct) - section discrimination has to come
         // from each InlineArraySegment's own CountFieldName, not the (duplicate) inner field name.
         SetupCardTables();
         Model.WriteMultiByteValue(0x48, 2, Token, 0); // main count = 0
         Model.WriteMultiByteValue(0x4A, 2, Token, 0); // extra count = 0
         ViewPort.Goto.Execute(0x20);
         ViewPort.Edit("<040> ");
         ViewPort.Goto.Execute(0x20);
         ViewPort.Edit("^data.decks.offsets.actual[offset<padding:: padding:: main: " +
            "[card:cardnames|data.cards.passwords]/main extra: " +
            "[card:cardnames|data.cards.passwords]/extra>]1 ");

         var structRun = (StructRun)Model.GetNextRun(0x40);
         var mainArraySpec = (InlineArraySegment)structRun.RawFieldSpecs.First(s => s is InlineArraySegment ia && ia.CountFieldName == "main");
         var cardSegment = (ArrayRunEnumSegment)mainArraySpec.Template;

         var result = YdkDeckImport.Import(Model, Token, structRun, cardSegment, "#main\n89631139\n#extra\n00000001\n");

         Assert.Equal(2, result.Imported);
         Assert.Equal(1, Model.ReadMultiByteValue(0x48, 2)); // main count = 1
         Assert.Equal(3, Model.ReadMultiByteValue(0x4A, 2)); // BEW
         Assert.Equal(1, Model.ReadMultiByteValue(0x4C, 2)); // extra count = 1 (shifted since main grew)
         Assert.Equal(1, Model.ReadMultiByteValue(0x4E, 2)); // BB1
      }
   }
}
