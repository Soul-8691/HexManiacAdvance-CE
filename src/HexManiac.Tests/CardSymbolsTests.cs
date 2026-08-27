using HavenSoft.HexManiac.Core.Models.Code;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class CardSymbolsTests {
      [Fact]
      public void CardSymbols_SingleDefine_ResolvesByValue() {
         var symbols = new CardSymbols(new[] { "#define KONAMI_THE_AGENT_OF_JUDGMENT_SATURN                 5951" });

         Assert.Equal("KONAMI_THE_AGENT_OF_JUDGMENT_SATURN", symbols.TryGetSymbol(5951));
      }

      [Fact]
      public void CardSymbols_UnknownValue_ReturnsNull() {
         var symbols = new CardSymbols(new[] { "#define KONAMI_THE_AGENT_OF_JUDGMENT_SATURN 5951" });

         Assert.Null(symbols.TryGetSymbol(1234));
      }

      [Fact]
      public void CardSymbols_IgnoresNonDefineLines() {
         var text = "#ifndef GUARD_CARD_H\n#define GUARD_CARD_H\nenum Race {\n  RACE_NONE = 0,\n};\n#define KONAMI_MYSTICAL_ELF 4008\n";

         var symbols = new CardSymbols(new[] { text });

         Assert.Equal("KONAMI_MYSTICAL_ELF", symbols.TryGetSymbol(4008));
         Assert.Null(symbols.TryGetSymbol(0)); // RACE_NONE = 0 is not a #define line, must not be picked up
      }

      [Fact]
      public void CardSymbols_MultipleFiles_BothLoaded() {
         var symbols = new CardSymbols(new[] {
            "#define KONAMI_BLUE_EYES_WHITE_DRAGON 4007",
            "#define KONAMI_DANDYLION 6654",
         });

         Assert.Equal("KONAMI_BLUE_EYES_WHITE_DRAGON", symbols.TryGetSymbol(4007));
         Assert.Equal("KONAMI_DANDYLION", symbols.TryGetSymbol(6654));
      }

      [Fact]
      public void CardSymbols_NoFileContents_NeverThrows() {
         var symbols = new CardSymbols();

         Assert.Null(symbols.TryGetSymbol(5951));
      }
   }
}
