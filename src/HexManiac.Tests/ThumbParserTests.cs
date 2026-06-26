using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using HavenSoft.HexManiac.Core.ViewModels.Visitors;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class ThumbParserTests : BaseViewModelTestClass {
      CodeTool Tool => ViewPort.Tools.CodeTool;
      ThumbParser Parser => Tool.Parser;
      ModelDelta Token => ViewPort.CurrentChange;

      public ThumbParserTests() {
         Model.ExpandData(ViewPort.CurrentChange, 0x400);
         SetFullModel(0xFF);
         ViewPort.Refresh();
      }

      [Fact]
      public void FindBranchLinkSources_FindsDirectCaller() {
         Parser.Compile(Token, Model, 0, "bl <200>");

         var results = Parser.FindBranchLinkSources(Model, 0x200);

         Assert.Equal(new[] { 0 }, results);
      }

      [Fact]
      public void FindBranchLinkSources_IgnoresUnrelatedCalls() {
         Parser.Compile(Token, Model, 0, "bl <200>");

         var results = Parser.FindBranchLinkSources(Model, 0x300);

         Assert.Empty(results);
      }

      [Fact]
      public void FindBranchLinkSources_FindsMultipleCallers() {
         Parser.Compile(Token, Model, 0, "bl <200>");
         Parser.Compile(Token, Model, 8, "bl <200>");

         var results = Parser.FindBranchLinkSources(Model, 0x200);

         Assert.Equal(new[] { 0, 8 }, results.OrderBy(i => i));
      }

      [Fact]
      public void GetAnchorSourceItems_BlOnlyCaller_DoesNotShowNothingPointsToThis() {
         ViewPort.Edit("@200 ^myFunction ");
         Parser.Compile(Token, Model, 0, "bl <myFunction>");

         var items = new ContextItemFactory(ViewPort).GetAnchorSourceItems(0x200).ToList();

         Assert.DoesNotContain(items, item => item.Text == "(Nothing points to this.)");
      }

      [Fact]
      public void FindBranchLinkTargets_FindsTargetWithinRange() {
         Parser.Compile(Token, Model, 0, "bl <200>");

         var targets = Parser.FindBranchLinkTargets(Model, 0, 4);

         Assert.Equal(new[] { 0x200 }, targets);
      }

      [Fact]
      public void FindBranchLinkTargets_IgnoresInstructionsOutsideRange() {
         Parser.Compile(Token, Model, 100, "bl <200>");

         var targets = Parser.FindBranchLinkTargets(Model, 0, 4);

         Assert.Empty(targets);
      }

      private void SelectRange(int start, int length) {
         ViewPort.SelectionStart = ViewPort.ConvertAddressToViewPoint(start);
         ViewPort.SelectionEnd = ViewPort.ConvertAddressToViewPoint(start + length - 1);
      }

      [Fact]
      public void ViewingCodeWithBl_AutoAnchorsTarget() {
         Parser.Compile(Token, Model, 0, "bl <200>");
         Tool.Mode = CodeMode.Thumb;
         Tool.IsSelected = true;

         SelectRange(0, 4);

         Assert.False(string.IsNullOrEmpty(Model.GetAnchorFromAddress(-1, 0x200)));
      }

      [Fact]
      public void ViewingCodeWithBl_SourcesListPicksUpCaller() {
         Parser.Compile(Token, Model, 0, "bl <200>");
         Tool.Mode = CodeMode.Thumb;
         Tool.IsSelected = true;

         SelectRange(0, 4);

         var items = new ContextItemFactory(ViewPort).GetAnchorSourceItems(0x200).ToList();
         Assert.DoesNotContain(items, item => item.Text == "(Nothing points to this.)");
      }

      [Fact]
      public void ViewingCodeWithBl_DoesNotClobberExistingFormattedRun() {
         ViewPort.Edit("@200 ^existingTable[a:]4 "); // a real, formatted run at the bl's target
         Parser.Compile(Token, Model, 0, "bl <200>");
         Tool.Mode = CodeMode.Thumb;
         Tool.IsSelected = true;

         SelectRange(0, 4);

         Assert.Equal("existingTable", Model.GetAnchorFromAddress(-1, 0x200));
      }

      [Fact]
      public void ViewingCodeWithBlTwice_DoesNotCreateDuplicateAnchor() {
         Parser.Compile(Token, Model, 0, "bl <200>");
         Tool.Mode = CodeMode.Thumb;
         Tool.IsSelected = true;

         SelectRange(0, 4);
         var firstName = Model.GetAnchorFromAddress(-1, 0x200);
         SelectRange(0, 2);
         SelectRange(0, 4);
         var secondName = Model.GetAnchorFromAddress(-1, 0x200);

         Assert.Equal(firstName, secondName);
      }
   }
}
