using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using System.Text;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class IndexedAsciiTests : BaseViewModelTestClass {
      public IndexedAsciiTests() : base(0x600) { }

      private void WriteAscii(int address, string text, byte terminator = 0x00) {
         for (int i = 0; i < text.Length; i++) Model[address + i] = (byte)text[i];
         Model[address + text.Length] = terminator;
      }

      // Mirrors the user's actual declaration: the offsets table is named data.cards.names.text and lives at the
      // "text" address, while its offset field references a separately-named base anchor (data.cards.pointers.names.text)
      // for where the raw ascii bytes actually live. Names are just labels to the parser - this checks nothing about
      // the *content* of the names (e.g. one containing "pointers") trips up parsing or anchor resolution.
      [Fact]
      public void IndexedAsciiField_UserExactNamingConvention_ParsesAndResolves() {
         ViewPort.Edit("@100 ^data.cards.pointers.names.text ");
         WriteAscii(0x110, "Bulbasaur");
         ViewPort.Edit("@200 ^data.cards.names.text[offset`asc`data.cards.pointers.names.text!00]2 ");
         Model.WriteMultiByteValue(0x200, 4, Token, 0x10); // 0x100 + 0x10 = 0x110

         var run = (ArrayRun)Model.GetNextRun(0x200);
         var segment = Assert.IsType<ArrayRunIndexedAsciiSegment>(run.ElementContent[0]);
         Assert.Equal("data.cards.pointers.names.text", segment.BaseAnchorName);

         var text = segment.ToText(Model, 0x200);
         Assert.Equal("\"Bulbasaur\"", text);

         var table = Model.GetTableModel("data.cards.names.text", () => Token);
         Assert.Equal("Bulbasaur", table[0]["offset"]);
      }

      // Reproduces the "grid shows a huge garbage-looking decimal number" bug: before this was wired up, the
      // field fell through to the generic Integer display (raw signed decimal) and to NumericFieldStrategy in
      // the sidebar - neither of which resolve the text at all.
      [Fact]
      public void IndexedAsciiField_MainGridCell_ShowsHexNotRawDecimal() {
         ViewPort.Edit("^strings ");
         ViewPort.Edit("@40 ^table[offset`asc`strings!00]1 ");
         Model.WriteMultiByteValue(0x40, 4, Token, 0x1234);

         var format = ((ArrayRun)Model.GetNextRun(0x40)).CreateSegmentDataFormat(Model, 0x40);

         Assert.IsType<IntegerHex>(format);
      }

      [Fact]
      public void IndexedAsciiField_SidebarStrategy_ShowsAndEditsResolvedText() {
         ViewPort.Edit("@100 ^strings ");
         WriteAscii(0x110, "Hi");
         ViewPort.Edit("@40 ^table[offset`asc`strings!00]1 ");
         Model.WriteMultiByteValue(0x40, 4, Token, 0x10);

         var fieldVM = new FieldArrayElementViewModel(ViewPort, "offset", 0x40, 4, IndexedAsciiFieldStrategy.Instance);
         Assert.Equal("Hi", fieldVM.Content);

         fieldVM.Content = "Hello there";

         var newOffset = Model.ReadMultiByteValue(0x40, 4);
         Assert.Equal("Hello there", Encoding.ASCII.GetString(Model.RawData, 0x100 + newOffset, 11));
      }

      [Fact]
      public void IndexedAsciiField_Parse_CreatesSegmentWithAnchorAndTerminator() {
         ViewPort.Edit("^strings ");
         ViewPort.Edit("@40 ^table[offset`asc`strings!00]2 ");

         var run = (ArrayRun)Model.GetNextRun(0x40);
         var segment = Assert.IsType<ArrayRunIndexedAsciiSegment>(run.ElementContent[0]);
         Assert.Equal("strings", segment.BaseAnchorName);
         Assert.Equal(4, segment.Length);
         Assert.Single(segment.Terminator);
         Assert.Equal(0x00, segment.Terminator[0]);
      }

      [Fact]
      public void IndexedAsciiField_ToText_ResolvesTextFromBaseAnchorPlusOffset() {
         ViewPort.Edit("@100 ^strings ");
         WriteAscii(0x110, "Hi");
         ViewPort.Edit("@40 ^table[offset`asc`strings!00]1 ");
         Model.WriteMultiByteValue(0x40, 4, Token, 0x10); // 0x100 + 0x10 = 0x110

         var run = (ArrayRun)Model.GetNextRun(0x40);
         var text = run.ElementContent[0].ToText(Model, 0x40);

         Assert.Equal("\"Hi\"", text);
      }

      [Fact]
      public void IndexedAsciiField_MissingBaseAnchor_FallsBackToRawNumber() {
         ViewPort.Edit("@40 ^table[offset`asc`nonexistent.anchor!00]1 ");
         Model.WriteMultiByteValue(0x40, 4, Token, 42);

         var run = (ArrayRun)Model.GetNextRun(0x40);
         var text = run.ElementContent[0].ToText(Model, 0x40);

         Assert.Equal("42", text);
      }

      [Fact]
      public void IndexedAsciiField_WriteText_FitsInPlace_UpdatesBytesWithoutMoving() {
         ViewPort.Edit("@100 ^strings ");
         WriteAscii(0x110, "Hi");
         ViewPort.Edit("@40 ^table[offset`asc`strings!00]1 ");
         Model.WriteMultiByteValue(0x40, 4, Token, 0x10);
         var run = (ArrayRun)Model.GetNextRun(0x40);
         var segment = (ArrayRunIndexedAsciiSegment)run.ElementContent[0];

         segment.WriteText(Model, Token, 0x40, "Yo");

         Assert.Equal((byte)'Y', Model[0x110]);
         Assert.Equal((byte)'o', Model[0x111]);
         Assert.Equal(0, Model[0x112]);
         Assert.Equal(0x10, Model.ReadMultiByteValue(0x40, 4)); // offset unchanged, no relocation
      }

      [Fact]
      public void IndexedAsciiField_WriteText_Grows_RelocatesAndUpdatesOffsetField() {
         ViewPort.Edit("@100 ^strings ");
         WriteAscii(0x110, "Hi");
         Model[0x113] = 0x01; // block growth in place: this byte isn't free
         ViewPort.Edit("@40 ^table[offset`asc`strings!00]1 ");
         Model.WriteMultiByteValue(0x40, 4, Token, 0x10);
         var run = (ArrayRun)Model.GetNextRun(0x40);
         var segment = (ArrayRunIndexedAsciiSegment)run.ElementContent[0];

         segment.WriteText(Model, Token, 0x40, "Hello there");

         var newOffset = Model.ReadMultiByteValue(0x40, 4);
         Assert.NotEqual(0x10, newOffset);
         var newAddress = 0x100 + newOffset;
         Assert.Equal("Hello there", Encoding.ASCII.GetString(Model.RawData, newAddress, 11));
         Assert.Equal(0xFF, Model[0x110]); // old location cleared
      }

      [Fact]
      public void IndexedAsciiField_SharedString_BothRowsUpdateOnRelocation() {
         ViewPort.Edit("@100 ^strings ");
         WriteAscii(0x110, "Hi");
         Model[0x113] = 0x01; // block in-place growth
         ViewPort.Edit("@40 ^table[offset`asc`strings!00]2 ");
         Model.WriteMultiByteValue(0x40, 4, Token, 0x10); // row 0
         Model.WriteMultiByteValue(0x44, 4, Token, 0x10); // row 1, same string

         var run = (ArrayRun)Model.GetNextRun(0x40);
         var segment = (ArrayRunIndexedAsciiSegment)run.ElementContent[0];
         // touch both rows first so both get registered as sources of the same destination run
         segment.ResolveRun(Model, Token, 0x40, out var _);
         segment.ResolveRun(Model, Token, 0x44, out var _);

         segment.WriteText(Model, Token, 0x40, "Hello there");

         var offset0 = Model.ReadMultiByteValue(0x40, 4);
         var offset1 = Model.ReadMultiByteValue(0x44, 4);
         Assert.Equal(offset0, offset1);
         Assert.NotEqual(0x10, offset0);
      }

      [Fact]
      public void IndexedAsciiField_ModelTableDynamicAccess_ReadsAndWritesText() {
         ViewPort.Edit("@100 ^strings ");
         WriteAscii(0x110, "Hi");
         ViewPort.Edit("@40 ^table[offset`asc`strings!00]1 ");
         Model.WriteMultiByteValue(0x40, 4, Token, 0x10);

         var table = Model.GetTableModel("table", () => Token);
         Assert.Equal("Hi", table[0]["offset"]);

         table[0]["offset"] = "Yo";

         Assert.Equal((byte)'Y', Model[0x110]);
         Assert.Equal("Yo", table[0]["offset"]);
      }

      [Fact]
      public void IndexedAsciiRun_NoTerminatorWithinCap_LengthCapsAt1024() {
         for (int i = 0x100; i < 0x100 + 1200; i++) Model[i] = (byte)'A';

         var length = AsciiString.ReadLength(Model, 0x100, new byte[] { 0x00 });

         Assert.Equal(1024, length);
      }
   }
}
