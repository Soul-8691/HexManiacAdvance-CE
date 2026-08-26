using HavenSoft.HexManiac.Core;
using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.ViewModels.Tools;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class PythonTests : BaseViewModelTestClass {
      private readonly PythonTool tool;
      private List<string> Prints { get; } = new();
      private string Execute(string code) {
         tool.Text = code;
         tool.RunPython();
         return tool.ResultText;
      }

      public PythonTests() {
         FileSystem.ShowCustomMessageBox = (text, _, _) => {
            Prints.Add(text);
            return true;
         };
         var editor = New.EditorViewModel();
         tool = new PythonTool(editor);
         editor.Add(ViewPort);
      }

      [Fact]
      public void SubtableWithLengthFromParent_EditFieldWithPython_SubtableLengthUpdated() {
         SetFullModel(0xFF);
         //              2         <child>     (1,2)        (3,4)
         ViewPort.Edit("02 00 00 00 <100> @100 01 00 02 00 03 00 04 00 ");
         ViewPort.Edit("@000 ^parent[length:: child<[x: y:]/length>]1 ");

         Execute("table['parent'][0].length = 3");

         var table = (ITableRun)Model.GetNextRun(0x100);
         Assert.Equal(3, table.ElementCount);
      }

      [Fact]
      public void TableWithTuple_AccessValueInPython_RightValue() {
         ViewPort.Edit("^elements[a:|t|x::|y::|z:: b:]2 (0 4 0) ");

         var result = Execute("table['elements'][0].a.y").Trim();

         Assert.Equal("4", result);
      }

      [Fact]
      public void TableWithTuple_SetValueInPython_RightValue() {
         ViewPort.Edit("^elements[a:|t|x::|y::|z:: b:]2 (0 4 0) ");

         Execute("table['elements'][0].a.y = 7").Trim();

         Assert.Equal(0x0070, Model.ReadMultiByteValue(0, 2));
      }

      // pythonnet uses .NET reflection, not the DLR DynamicObject protocol, so the
      // IronPython-era __len__/__getindex__ helpers were dropped. Confirm len(table)
      // and string-key column access still resolve through the .NET members.
      [Fact]
      public void Table_LenAndElementAccess_WorkUnderPythonnet() {
         ViewPort.Edit("^elements[a: b:]3 1 2 3 4 5 6 "); // rows: (1,2) (3,4) (5,6)

         Assert.Equal("3", Execute("len(table['elements'])").Trim()); // removed __len__ -> via .NET Count
         Assert.Equal("5", Execute("table['elements'][2].a").Trim()); // row 2, field a
      }

      // EditorViewModel.this[int] must return object, not ITabContent: pythonnet 3.x wraps a
      // returned value strictly in its declared interface type, which would hide ViewPort-only
      // members like Model and this[string] from Python, breaking editor[0]['table'] / editor[0].Model.
      [Fact]
      public void EditorIndexer_StringKeyAccess_WorksLikeTableGetter() {
         ViewPort.Edit("^elements[a: b:]2 1 2 3 4 ");

         var result = Execute("editor[0]['elements'][0].a").Trim();

         Assert.Equal("1", result);
      }

      [Fact]
      public void EditorIndexer_ModelProperty_IsAccessible() {
         var result = Execute("editor[0].Model is not None").Trim();

         Assert.Equal("True", result);
      }

      [Fact]
      public void RunPython_EditsField_UndoRevertsRedoReapplies() {
         ViewPort.Edit("^elements[a:|t|x::|y::|z:: b:]2 (0 4 0) ");
         ViewPort.ChangeHistory.ChangeCompleted(); // close the setup edit as its own step, isolating the python run below

         Execute("table['elements'][0].a.y = 7");
         Assert.Equal(0x0070, Model.ReadMultiByteValue(0, 2));

         ViewPort.Undo.Execute();
         Assert.Equal(0x0040, Model.ReadMultiByteValue(0, 2));

         ViewPort.Redo.Execute();
         Assert.Equal(0x0070, Model.ReadMultiByteValue(0, 2));
      }

      [Fact]
      public void RunPython_TwoSeparateRuns_AreTwoSeparateUndoSteps() {
         ViewPort.Edit("^elements[a: b:]2 1 2 3 4 ");
         ViewPort.ChangeHistory.ChangeCompleted(); // close the setup edit as its own step, isolating the python runs below

         Execute("table['elements'][0].a = 10");
         Execute("table['elements'][0].b = 20");
         Assert.Equal(10, Model.ReadMultiByteValue(0, 2));
         Assert.Equal(20, Model.ReadMultiByteValue(2, 2));

         ViewPort.Undo.Execute(); // undoes only the second run
         Assert.Equal(10, Model.ReadMultiByteValue(0, 2));
         Assert.Equal(2, Model.ReadMultiByteValue(2, 2));

         ViewPort.Undo.Execute(); // undoes the first run
         Assert.Equal(1, Model.ReadMultiByteValue(0, 2));
         Assert.Equal(2, Model.ReadMultiByteValue(2, 2));
      }

      [Fact]
      public void GetAutocomplete_AnchorPrefix_SuggestsFullDottedAnchorName() {
         ViewPort.Edit("@000 ^data.pokemon.stats[hp: atk:]2 (10 20) (30 40) ");

         var options = tool.GetAutocomplete("dat", 0, 3);

         Assert.Contains(options, o => o.LineText == "data.pokemon.stats");
      }

      [Fact]
      public void GetAutocomplete_KeywordPrefix_SuggestsKeyword() {
         var options = tool.GetAutocomplete("imp", 0, 3);

         Assert.Contains(options, o => o.LineText == "import");
      }

      [Fact]
      public void GetAutocomplete_BuiltinPrefix_SuggestsBuiltinFunction() {
         var options = tool.GetAutocomplete("pri", 0, 3);

         Assert.Contains(options, o => o.LineText == "print");
      }

      [Fact]
      public void GetAutocomplete_ModulePrefix_SuggestsImportableModule() {
         // 'os' is always present (stdlib), unlike the pip-bootstrapped packages,
         // which are allowed to be missing - see BundledPackage_ImportRequests_Succeeds.
         var options = tool.GetAutocomplete("o", 0, 1);

         Assert.Contains(options, o => o.LineText == "os");
      }

      [Fact]
      public void GetAutocomplete_DottedPrefixAfterImport_SuggestsRealModuleMember() {
         Execute("import os");

         var options = tool.GetAutocomplete("os.path.j", 0, 9);

         Assert.Contains(options, o => o.LineText == "os.path.join");
      }

      [Fact]
      public void GetAutocomplete_DottedModulePrefixBeforeAnyImport_StillSuggestsRealMember() {
         // the common case: a user typing a fresh script (import + usage together) who
         // hasn't actually run anything yet, so 'os' isn't bound in the live scope - dir()
         // completion must still work by importing the module fresh for introspection.
         var options = tool.GetAutocomplete("os.path.j", 0, 9);

         Assert.Contains(options, o => o.LineText == "os.path.join");
      }

      [Fact]
      public void GetAutocomplete_InsideStringLiteral_ReturnsNull() {
         var options = tool.GetAutocomplete("print('pri", 0, 10);

         Assert.Null(options);
      }

      [Fact]
      public void GetAutocomplete_ApostropheInClosedDoubleQuotedString_StillCompletes() {
         // the lone ' is inside a *closed* double-quoted string, so the cursor is not in a
         // literal - a naive quote count would miscount and wrongly suppress completion.
         var line = "x = \"it's \" + pri";

         var options = tool.GetAutocomplete(line, 0, line.Length);

         Assert.Contains(options, o => o.Text == "print");
      }

      [Fact]
      public void PrintCapture_WhenSet_ReceivesPrintInsteadOfMessageBox() {
         var captured = new List<string>();
         tool.PrintCapture = captured.Add;
         try {
            tool.RunForAutomation("print('hello')");
         } finally {
            tool.PrintCapture = null;
         }
         Assert.Contains("hello", captured);
         Assert.DoesNotContain("hello", Prints); // did NOT fall through to ShowCustomMessageBox
      }

      [Fact]
      public void RunForAutomation_TrailingExpression_ReturnsStringifiedValue() {
         var info = tool.RunForAutomation("1 + 1");
         Assert.True(info.HasError);      // ErrorInfo carries the result text here
         Assert.True(info.IsWarning);     // warning-level == a value, not an exception
         Assert.Equal("2", info.ErrorMessage.Trim());
      }

      [Fact]
      public void RunForAutomation_Exception_IsNotWarningLevel() {
         var info = tool.RunForAutomation("raise ValueError('boom')");
         Assert.True(info.HasError);
         Assert.False(info.IsWarning);    // an exception, not a value
         Assert.Contains("boom", info.ErrorMessage);
      }

      [SkippableFact]
      public void BundledPackage_ImportRequests_Succeeds() {
         // guards the pip-bootstrap pipeline in Directory.Build.targets: the embeddable
         // CPython package ships with site-packages disabled, so this only works if that
         // pipeline successfully enabled it and installed requests. That pipeline is
         // intentionally non-fatal (a network blip must never break the editor build), so
         // a missing package here is a real, expected possibility - skip rather than fail.
         var importResult = tool.RunPythonScript("import requests");
         Skip.If(importResult.HasError && !importResult.IsWarning, importResult.ErrorMessage);

         var result = Execute("import requests; 'requests imported ok'").Trim();
         Assert.Equal("requests imported ok", result);
      }

      [Fact]
      public void Introspect_Namespaces_IncludesAnchorGroup() {
         ViewPort.Edit("^elements[a: b:]3 1 2 3 4 5 6 ");
         var result = (System.Collections.Generic.IDictionary<string, object>)
            HavenSoft.HexManiac.Core.Models.PythonIntrospection.Namespaces(Model);
         var names = (System.Collections.Generic.IEnumerable<string>)result["namespaces"];
         Assert.Contains("elements", names);
      }

      [Fact]
      public void Introspect_TableSchema_ListsFields() {
         ViewPort.Edit("^elements[a: b:]3 1 2 3 4 5 6 ");
         var run = HavenSoft.HexManiac.Core.Models.PythonIntrospection.ResolveTable(Model, "elements");
         Assert.NotNull(run);
         var schema = (System.Collections.Generic.IDictionary<string, object>)
            HavenSoft.HexManiac.Core.Models.PythonIntrospection.TableSchema(Model, run, "elements");
         Assert.Equal("table", schema["kind"]);
         Assert.Equal(3, schema["count"]);
         var fields = (System.Collections.Generic.IEnumerable<object>)schema["fields"];
         var fieldNames = fields
            .Select(f => (string)((System.Collections.Generic.IDictionary<string, object>)f)["name"]).ToList();
         Assert.Contains("a", fieldNames);
         Assert.Contains("b", fieldNames);
      }

      [Fact]
      public void Introspect_NonTableTarget_ResolveTableReturnsNull() {
         ViewPort.Edit("^elements[a: b:]3 1 2 3 4 5 6 ");
         Assert.Null(HavenSoft.HexManiac.Core.Models.PythonIntrospection.ResolveTable(Model, "not_a_table"));
      }

      [Fact]
      public void DescribeExpression_Evaluates_ValueAndMembers() {
         var result = (System.Collections.Generic.IDictionary<string, object>)tool.DescribeExpression("1 + 1");
         Assert.Equal("value", result["kind"]);
         Assert.Equal("2", ((string)result["value"]).Trim());
         var members = (System.Collections.Generic.IEnumerable<string>)result["members"];
         Assert.NotEmpty(members); // dir(2) has int members like bit_length
      }
   }
}
