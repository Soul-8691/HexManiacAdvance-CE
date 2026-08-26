using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class AsciiRunTests : BaseViewModelTestClass {
      [Fact]
      public void CanCreateAsciiRun() {
         var (model, viewPort) = (Model, ViewPort);
         model[0x10] = (byte)'a';
         model[0x11] = (byte)'b';
         model[0x12] = (byte)'c';
         model[0x13] = (byte)'d';

         viewPort.SelectionStart = new Point(0, 1);
         viewPort.Edit("^data`asc`4 ");

         var format = (Ascii)viewPort[1, 1].Format;
         Assert.Equal("b", format.ThisCharacter);
      }

      [Fact]
      public void CanEditAsciiRun() {
         var (model, viewPort) = (Model, ViewPort);
         model[0x10] = (byte)'a';
         model[0x11] = (byte)'b';
         model[0x12] = (byte)'c';
         model[0x13] = (byte)'d';

         model.ObserveRunWritten(new ModelDelta(), new AsciiRun(model, 0x10, 4));

         viewPort.SelectionStart = new Point(1, 1);
         viewPort.Edit("3");

         var format = (Ascii)viewPort[1, 1].Format;
         Assert.Equal("3", format.ThisCharacter);
         Assert.Equal(new Point(2, 1), viewPort.SelectionStart);
      }

      [Fact]
      public void AsciiText_TypeSpace_NoChange() {
         ViewPort.Edit("^test`asc`7  ");
         Assert.Equal(0, Model[0]);
         Assert.Equal(0, ViewPort.ConvertViewPointToAddress(ViewPort.SelectionStart));
      }

      [Fact]
      public void AsciiText_EscapeCharacter_EditCharacter() {
         ViewPort.Edit("^test`asc`7 \\");
         var format = (UnderEdit)ViewPort[0, 0].Format;
         Assert.Equal("\\", format.CurrentText);
      }

      [Fact]
      public void AsciiText_EscapeSpace_OneChange() {
         ViewPort.Edit("^test`asc`7  \\ ");
         Assert.Equal((byte)' ', Model[0]);
         Assert.Equal(1, ViewPort.ConvertViewPointToAddress(ViewPort.SelectionStart));
      }

      [Fact]
      public void AsciiText_NoEscapeAnchor_AnchorEdit() {
         ViewPort.Edit("^test`asc`7 ^");
         Assert.IsType<UnderEdit>(ViewPort[0, 0].Format);
      }

      [Fact]
      public void AsciiText_EscapeAnchor_WriteCharacter() {
         ViewPort.Edit("^test`asc`7 \\^");
         Assert.Equal((byte)'^', Model[0]);
         Assert.Equal(1, ViewPort.ConvertViewPointToAddress(ViewPort.SelectionStart));
      }

      [Fact]
      public void AsciiText_EscapeEscape_WriteOneCharacter() {
         ViewPort.Edit("^test`asc`7 \\\\");
         Assert.Equal((byte)'\\', Model[0]);
         Assert.Equal(1, ViewPort.ConvertViewPointToAddress(ViewPort.SelectionStart));
      }

      [Fact]
      public void AsciiFormat_ZeroLength_ConvertToLength1() {
         ViewPort.Edit("^test`asc`0 ");
         Assert.Equal(1, Model.GetNextRun(0).Length);
      }

      // TODO 'copy' should work correctly when including escape characters, like a leading space, ^, or \

      [Fact]
      public void AsciiPointerFieldInTable_Create_PointsAtAsciiRun() {
         ViewPort.Edit("@100 ^table[ptr<`asc`4>]1 <000> ");
         var run = Model.GetNextRun(0);
         Assert.IsType<AsciiRun>(run);
      }

      [Fact]
      public void AsciiInlineFieldInTable_Create_ParsesAsArrayRun() {
         ViewPort.Edit("^table[name`asc`4]2 ");
         var run = Model.GetNextRun(0);
         Assert.IsType<ArrayRun>(run);
         var arrayRun = (ArrayRun)run;
         Assert.Equal(ElementContentType.Ascii, arrayRun.ElementContent[0].Type);
         Assert.Equal(4, arrayRun.ElementContent[0].Length);
      }

      [Fact]
      public void AsciiInlineFieldInTable_ReadWriteViaModelTable_RoundTrips() {
         ViewPort.Edit("^table[name`asc`4]2 ");
         var table = Model.GetTableModel("table", () => ViewPort.ChangeHistory.CurrentChange);

         table[0]["name"] = "abcd";

         Assert.Equal((byte)'a', Model[0]);
         Assert.Equal((byte)'d', Model[3]);
         Assert.Equal("abcd", table[0]["name"]);
      }

      [Fact]
      public void AsciiInlineFieldInTable_CellFormat_IsAscii() {
         ViewPort.Edit("^table[name`asc`4]2 abcd wxyz ");

         var format = (Ascii)ViewPort[1, 0].Format;

         Assert.Equal("b", format.ThisCharacter);
      }

      [Fact]
      public void AsciiInlineFieldInTable_ToText_ReadsRawBytesQuoted() {
         ViewPort.Edit("^table[name`asc`8]1 ");
         var bytes = System.Text.Encoding.ASCII.GetBytes("hi there");
         for (int i = 0; i < bytes.Length; i++) Model[i] = bytes[i];

         var text = ((ArrayRun)Model.GetNextRun(0)).ElementContent[0].ToText(Model, 0);

         Assert.Equal("\"hi there\"", text);
      }

      // Write() is what TableStreamRun uses to parse a typed row of text (see TableStreamRun.cs:299) -
      // quoting keeps a space-containing ascii value from being split apart by the space-delimited tokenizer.
      // A plain fixed-size table's initial content, by contrast, is populated by replaying keystrokes through
      // the interactive cell editor, which never calls Write and treats '"' as a literal ascii character
      // (matching the existing top-level AsciiRun cell-editing behavior).
      [Fact]
      public void AsciiInlineFieldInTable_Write_ParsesQuotedTextWithSpaces() {
         ViewPort.Edit("^table[name`asc`8]1 ");
         var run = (ArrayRun)Model.GetNextRun(0);
         var segment = run.ElementContent[0];
         var data = "\"hi there\"";

         segment.Write(run.ElementContent, Model, new ModelDelta(), 0, ref data);

         Assert.Equal("hi there", segment.ToText(Model, 0).Trim('"'));
      }
   }
}
