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

      [Fact]
      public void ThumbParser_ShowCardNamesOn_ReplacesMatchingWordLiteral() {
         Parser.CardSymbols = new CardSymbols(new[] { "#define KONAMI_THE_AGENT_OF_JUDGMENT_SATURN 5951" });
         Parser.ShowCardNames = true;
         Parser.Compile(Token, Model, 0, "ldr r0, [pc, <004>]", "bx r0", "004: .word 173F"); // 0x173F == 5951 decimal

         var text = Parser.Parse(Model, 0, 4);

         Assert.Contains("@ = KONAMI_THE_AGENT_OF_JUDGMENT_SATURN", text);
      }

      [Fact]
      public void ThumbParser_ShowCardNamesOff_ShowsRawNumber() {
         Parser.CardSymbols = new CardSymbols(new[] { "#define KONAMI_THE_AGENT_OF_JUDGMENT_SATURN 5951" });
         Parser.ShowCardNames = false;
         Parser.Compile(Token, Model, 0, "ldr r0, [pc, <004>]", "bx r0", "004: .word 173F");

         var text = Parser.Parse(Model, 0, 4);

         Assert.Contains("@ = 5951", text);
         Assert.DoesNotContain("KONAMI", text);
      }

      [Fact]
      public void ThumbParser_ShowCardNamesOn_NoMatch_ShowsRawNumber() {
         Parser.CardSymbols = new CardSymbols(new[] { "#define KONAMI_THE_AGENT_OF_JUDGMENT_SATURN 5951" });
         Parser.ShowCardNames = true;
         Parser.Compile(Token, Model, 0, "ldr r0, [pc, <004>]", "bx r0", "004: .word 1740"); // 0x1740 == 5952, unmatched

         var text = Parser.Parse(Model, 0, 4);

         Assert.Contains("@ = 5952", text);
         Assert.DoesNotContain("KONAMI", text);
      }

      [Fact]
      public void ThumbParser_PokefireredSymbolsParse_ParsesSymFormat() {
         // PokefireredSymbols.EnsureLoaded() prefers a real %AppData%/HexManiacAdvance/pokefirered.sym
         // cache over the constructor's fallback content when one exists on disk (from actual HMA
         // usage), so a full instance isn't reliably testable here - just confirm the parser itself
         // reads the "AAAAAAAA g SSSSSSSS name" format the BPRE0 gate depends on downstream.
         var (symbols, _) = PokefireredSymbols.Parse("02020630 g 00000004 gPlayerParty");

         Assert.Equal("gPlayerParty", symbols[0x02020630]);
      }

      [Fact]
      public void ThumbParser_PokefireredSymbols_NotBPRE0_HidesSymbol() {
         // default game code (from SetFullModel(0xFF) in the constructor) is not BPRE0 - whatever
         // PokefireredSymbols resolves to (fallback content or a real cached .sym) must be hidden.
         Parser.PokefireredSymbols = new PokefireredSymbols(InstantDispatch.Instance, "02020630 g 00000004 gPlayerParty");
         Parser.Compile(Token, Model, 0, "ldr r0, [pc, <004>]", "bx r0", "004: .word 02020630");

         var text = Parser.Parse(Model, 0, 4);

         Assert.DoesNotContain("gPlayerParty", text);
      }

      [Fact]
      public void ThumbParser_ProjectLabels_BPRE0_ShowsSymbol() {
         SetGameCode("BPRE0");
         Parser.ProjectLabels = new ProjectLabels(new[] { "HubolFunc: 02020630" });
         Parser.Compile(Token, Model, 0, "ldr r0, [pc, <004>]", "bx r0", "004: .word 02020630");

         var text = Parser.Parse(Model, 0, 4);

         Assert.Contains("HubolFunc", text);
      }

      [Fact]
      public void ThumbParser_ProjectLabels_NotBPRE0_HidesSymbol() {
         Parser.ProjectLabels = new ProjectLabels(new[] { "HubolFunc: 02020630" });
         Parser.Compile(Token, Model, 0, "ldr r0, [pc, <004>]", "bx r0", "004: .word 02020630");

         var text = Parser.Parse(Model, 0, 4);

         Assert.DoesNotContain("HubolFunc", text);
      }

      [Fact]
      public void ThumbParser_SelectedSymbolFile_OtherRom_ShowsSymbol() {
         Parser.SelectedSymbolFile = new SymbolFile("eds_demo.sym", "02013d90 g 00000050 gCardDetail");
         Parser.Compile(Token, Model, 0, "ldr r0, [pc, <004>]", "bx r0", "004: .word 02013D90");

         var text = Parser.Parse(Model, 0, 4);

         Assert.Contains("gCardDetail", text);
      }

      [Fact]
      public void ThumbParser_NoSymbolFileSelected_OtherRom_HidesSymbol() {
         Parser.Compile(Token, Model, 0, "ldr r0, [pc, <004>]", "bx r0", "004: .word 02013D90");

         var text = Parser.Parse(Model, 0, 4);

         Assert.DoesNotContain("gCardDetail", text);
      }

      [Fact]
      public void Singletons_BundledSymbolFiles_IncludeEdsBuildsAndResolve() {
         // the real resources/ folder, as the built app sees it
         var files = BaseViewModelTestClass.Singletons.SymbolFiles;
         var names = files.Select(file => file.Name).ToList();

         Assert.Contains("eds_demo.sym", names);
         Assert.Contains("eds_release_demo.sym", names);
         Assert.Contains("eds_release_demo_extra.sym", names);
         var extra = files.Single(file => file.Name == "eds_release_demo_extra.sym");
         Assert.Equal("DuelMainStep", extra.TryGetSymbol(0x0802224C));
      }

      [Theory]
      [InlineData("BPRE0")]
      [InlineData("BY6E0")]
      public void ThumbParser_SelectedSymbolFile_FireRedOrWct06_Ignored(string gameCode) {
         SetGameCode(gameCode);
         Parser.PokefireredSymbols = new PokefireredSymbols(InstantDispatch.Instance, string.Empty);
         Parser.ProjectLabels = new ProjectLabels();
         Parser.SelectedSymbolFile = new SymbolFile("eds_demo.sym", "02013d90 g 00000050 gCardDetail");
         Parser.Compile(Token, Model, 0, "ldr r0, [pc, <004>]", "bx r0", "004: .word 02013D90");

         var text = Parser.Parse(Model, 0, 4);

         Assert.DoesNotContain("gCardDetail", text);
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
      public void ViewingCodeWithBl_GenericAnchorThenSymbolFileSelected_RenamedToSymbol() {
         // eds_release_demo_extra.sym: 080083d4 g 00000012 CountActiveCardsOnField
         Model.ExpandData(Token, 0x8400);
         Parser.Compile(Token, Model, 0, "bl <83D4>");
         Tool.Mode = CodeMode.Thumb;
         Tool.IsSelected = true;
         SelectRange(0, 4); // no symbols selected: generic name
         Assert.Equal("code.0083D4", Model.GetAnchorFromAddress(-1, 0x83D4));

         Tool.SelectedSymbolFileIndex = Tool.SymbolFileOptions.ToList().IndexOf("eds_release_demo_extra.sym");

         Assert.Equal("CountActiveCardsOnField", Model.GetAnchorFromAddress(-1, 0x83D4));
         Assert.Contains("<CountActiveCardsOnField>", Tool.Content);
      }

      [Fact]
      public void ViewingCodeWithBl_UserNamedAnchor_NotRenamedBySymbolFile() {
         Model.ExpandData(Token, 0x8400);
         ViewPort.Refresh();
         ViewPort.Edit("@83D4 ^myFunction @000 ");
         Parser.Compile(Token, Model, 0, "bl <83D4>");
         Tool.Mode = CodeMode.Thumb;
         Tool.IsSelected = true;
         Tool.SelectedSymbolFileIndex = Tool.SymbolFileOptions.ToList().IndexOf("eds_release_demo_extra.sym");

         SelectRange(0, 4);

         Assert.Equal("myFunction", Model.GetAnchorFromAddress(-1, 0x83D4));
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
