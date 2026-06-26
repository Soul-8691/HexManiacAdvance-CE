using HavenSoft.HexManiac.Core.ViewModels;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class TableGridTabTests : BaseViewModelTestClass {
      public TableGridTabTests() : base(0x300) { }

      // Multi-word names (like "Iron Barbs") come back quoted from ArrayRunEnumSegment.GetOptions,
      // while the current value of a cell (ReadDisplayValue/GetEnumValue) comes back unquoted -
      // without stripping quotes in GetEnumOptions too, a quoted option's current value would
      // never string-match anything in its own dropdown list.
      private TableGridTab SetupAbilityTable() {
         CreateTextTable("abilities", 0x100, "Levitate", "Iron Barbs");
         CreateEnumTable("table", 0x200, "abilities", 0, 1);
         return new TableGridTab(FileSystem, ViewPort, "table");
      }

      [Fact]
      public void GetEnumOptions_MultiWordOption_ReturnsUnquoted() {
         var tab = SetupAbilityTable();

         var options = tab.GetEnumOptions("data");

         Assert.Contains("Iron Barbs", options);
         Assert.DoesNotContain("\"Iron Barbs\"", options);
      }

      [Fact]
      public void ReadDisplayValue_MultiWordOption_MatchesGetEnumOptionsEntry() {
         var tab = SetupAbilityTable();

         var options = tab.GetEnumOptions("data");
         var currentValue = tab.ReadDisplayValue(1, "data");

         Assert.Contains((string)currentValue, options);
      }

      [Fact]
      public void WriteDisplayValue_MultiWordOption_RoundTrips() {
         var tab = SetupAbilityTable();

         tab.WriteDisplayValue(0, "data", "Iron Barbs");

         Assert.Equal("Iron Barbs", tab.ReadDisplayValue(0, "data"));
      }
   }
}
