using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.Models.Images;
using HavenSoft.HexManiac.Core.Models.Runs;
using System;
using System.Linq;
using System.Text;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   /// <summary>EDS (Eternal Duelist Soul decomp) "Import from YGOProDeck": EdsCardImport.</summary>
   public class EdsCardImportTests : BaseViewModelTestClass {
      public EdsCardImportTests() : base(0x8000) { }

      // A miniature EDS: 5 card IDs (0 empty), the tables tools/hma_toml.py describes, at small addresses.
      private void SetupTables() {
         Array.Fill(Model.RawData, (byte)0xFF, 0x1000, 0x1000); // free space to move into
         Model.SetList(Token, "cardnumbers", "", "Alpha", "Blank 2", "Charlie", "Blank 4");
         Model.SetList(Token, "cardinternalnumbers", "", "Alpha", "Charlie", "Blank 2", "Blank 4");
         ViewPort.Edit("@000 ^data.cards.stats[cardstats::|t|defn::::.|atk::::.|kind:|race::.|stars::|attribute:.]cardnumbers ");
         ViewPort.Edit("@020 ^data.cards.passwords[password::|h]cardnumbers ");
         ViewPort.Edit("@040 ^data.cards.numbers[index:cardinternalnumbers]cardnumbers 0 1 3 2 4 ");
         ViewPort.Edit("@050 ^data.cards.typeex[typeex.]cardnumbers ");
         ViewPort.Edit("@058 ^data.cards.linkmarkers[arrows.]cardnumbers ");
         ViewPort.Edit("@100 ^data.cards.names[name`asc`64]cardnumbers ");
         WriteName(1, "Alpha");
         WriteName(3, "Charlie");
         ViewPort.Edit("@400 ^data.cards.descriptions[description`asc`480]cardnumbers ");
         // name order: Alpha, Charlie, 0 - with the gCardTypeEx-like data right after it, so it has to move to grow
         Model.WriteMultiByteValue(0x300, 2, Token, 1);
         Model.WriteMultiByteValue(0x302, 2, Token, 3);
         Model.WriteMultiByteValue(0x304, 2, Token, 0);
         ViewPort.Edit("@300 ^data.cards.nameorder[card:cardnumbers]!0000 ");
         ViewPort.Edit("@306 ^next[x:]1 ");
      }

      private void WriteName(int id, string name) {
         for (int i = 0; i < name.Length; i++) Model.RawData[0x100 + id * 64 + i] = (byte)name[i];
      }

      private string ReadName(int id) => Encoding.ASCII.GetString(Model.RawData, 0x100 + id * 64, 64).TrimEnd('\0');

      private static YgoProDeckCard Monster() => new() {
         Id = 89631139, Name = "Bravo Dragon", Type = "Effect Monster", Race = "Dragon", Attribute = "LIGHT",
         Atk = 3000, Def = 2500, Level = 8, Desc = "A test\n“dragon” – with   spaces.",
      };

      [Fact]
      public void StatsWord_Monster_PacksDefAtkKindTypeLevelAttribute() {
         Assert.True(EdsCardImport.TryGetStatsWord(Monster(), out var word, out _));
         Assert.Equal(250u, word & 0x1FF);           // DEF / 10
         Assert.Equal(300u, word >> 9 & 0x1FF);      // ATK / 10
         Assert.Equal(1u, word >> 18 & 3);           // effect
         Assert.Equal(1u, word >> 20 & 0x1F);        // Dragon
         Assert.Equal(8u, word >> 25 & 0xF);         // Level
         Assert.Equal(1u, word >> 29);               // LIGHT
      }

      [Theory]
      [InlineData("Spell Card", "Quick-Play", 22u << 20 | 5u << 17)]
      [InlineData("Trap Card", "Counter", 1u << 29 | 21u << 20 | 1u << 17)]
      public void StatsWord_SpellTrap_TypeAndSubtype(string type, string race, uint expected) {
         Assert.True(EdsCardImport.TryGetStatsWord(new YgoProDeckCard { Name = "X", Type = type, Race = race }, out var word, out _));
         Assert.Equal(expected, word);
      }

      [Fact]
      public void StatsWord_PendulumOrOddAtk_Refused() {
         Assert.False(EdsCardImport.TryGetStatsWord(new YgoProDeckCard { Type = "Pendulum Effect Monster", Race = "Dragon", Attribute = "DARK", Level = 4 }, out _, out _));
         Assert.False(EdsCardImport.TryGetStatsWord(new YgoProDeckCard { Type = "Effect Monster", Race = "Dragon", Attribute = "DARK", Level = 4, Atk = 1234 }, out _, out _));
      }

      [Fact]
      public void TypeExAndLinkMarkers_LinkCyberse() {
         var card = new YgoProDeckCard { Type = "Link Monster", Race = "Cyberse", Attribute = "DARK", LinkVal = 2, Atk = 1400, Desc = "2 monsters\nAn effect.", LinkMarkers = new[] { "Left", "Right" } };
         Assert.True(EdsCardImport.TryGetStatsWord(card, out var word, out _));
         Assert.Equal(2u, word >> 25 & 0xF);                                // Link Rating in Level
         Assert.Equal(7u, word >> 20 & 0x1F);                               // Cyberse -> Machine
         Assert.Equal(3 | 0x04 | 3 << 4, EdsCardImport.GetTypeEx(card));    // Link, effect, Cyberse
         Assert.Equal(0x08 | 0x10, EdsCardImport.GetLinkMarkers(card));
      }

      [Fact]
      public void PasswordBytes_TextOrderBcd() {
         Assert.Equal(new byte[] { 0x89, 0x63, 0x11, 0x39 }, EdsCardImport.PasswordBytes(Monster()));
         Assert.Equal(new byte[] { 0x08, 0x35, 0x37, 0x69 }, EdsCardImport.PasswordBytes(new YgoProDeckCard { Id = 8353769 }));
      }

      [Fact]
      public void CleanText_FixesPunctuationAndCutsAtWord() {
         Assert.Equal("A test \"dragon\" - with spaces.", EdsCardImport.CleanText(Monster().Desc, 480));
         Assert.Equal("one two...", EdsCardImport.CleanText("one two three", 12));
      }

      [Fact]
      public void CanImport_EdsTables_True_WithoutNames_False() {
         Assert.False(EdsCardImport.CanImport(Model));
         SetupTables();
         Assert.True(EdsCardImport.CanImport(Model));
      }

      [Fact]
      public void Import_BlankSlot_WritesCardAndListsIt() {
         SetupTables();

         var result = EdsCardImport.Import(Model, Token, 2, Monster());

         Assert.True(result.Success, result.Message);
         Assert.True(EdsCardImport.TryGetStatsWord(Monster(), out var word, out _));
         Assert.Equal(unchecked((int)word), Model.ReadMultiByteValue(0x008, 4));
         Assert.Equal(new byte[] { 0x89, 0x63, 0x11, 0x39 }, Model.RawData.Skip(0x028).Take(4).ToArray());
         Assert.Equal(0x04, Model[0x052]); // has an effect
         Assert.Equal("Bravo Dragon", ReadName(2));
         Assert.StartsWith("A test \"dragon\"", Encoding.ASCII.GetString(Model.RawData, 0x400 + 2 * 480, 480));
         Assert.True(Model.TryGetList("cardnumbers", out var byId) && byId[2] == "Bravo Dragon");
         Assert.True(Model.TryGetList("cardinternalnumbers", out var byNumber) && byNumber[3] == "Bravo Dragon"); // card ID 2 is card number 3

         var order = Model.GetTable("data.cards.nameorder");
         Assert.NotEqual(0x300, order.Start); // no room before `next`: moved
         Assert.Equal(new[] { 1, 2, 3, 0 }, Enumerable.Range(0, 4).Select(i => Model.ReadMultiByteValue(order.Start + i * 2, 2)).ToArray());
         Assert.Contains("rebuild", result.Message); // an effect monster's effect comes from the decomp
      }

      [Fact]
      public void Import_AgainWithAnotherName_MovesInOrderWithoutGrowing() {
         SetupTables();
         EdsCardImport.Import(Model, Token, 2, Monster());
         var count = Model.GetTable("data.cards.nameorder").ElementCount;

         var renamed = Monster();
         renamed.Name = "Delta Dragon";
         EdsCardImport.Import(Model, Token, 2, renamed);

         var order = Model.GetTable("data.cards.nameorder");
         Assert.Equal(count, order.ElementCount);
         Assert.Equal(new[] { 1, 3, 2 }, Enumerable.Range(0, 3).Select(i => Model.ReadMultiByteValue(order.Start + i * 2, 2)).ToArray());
      }

      [Fact]
      public void Import_LongName_CutAndNoted() {
         SetupTables();
         var card = Monster();
         card.Name = "Black Luster Soldier - Envoy of the Beginning Extra";

         var result = EdsCardImport.Import(Model, Token, 2, card);

         Assert.Equal(EdsCardImport.MaxNameLength, ReadName(2).Length);
         Assert.Contains("cut", result.Message);
      }

      [Fact]
      public void ImportGraphics_WritesArtPaletteIconAndMiniature() {
         SetupTables();
         ViewPort.Edit("@1800 ^graphics.cards.palettes[`ucp6`]cardnumbers ");
         ViewPort.Edit("@2000 ^graphics.cards.images[`ucs6x9x10|graphics.cards.palettes`]cardnumbers ");
         var image = new Rgb[90, 100];
         for (int y = 0; y < 100; y++) for (int x = 0; x < 90; x++) image[x, y] = new Rgb(x * 2, y * 2, 100);

         var result = EdsCardImport.ImportGraphics(Model, Token, 2, image);

         Assert.True(result.Success, result.Message);
         var art = Model.GetTable("graphics.cards.images");
         Assert.Contains(Enumerable.Range(0, art.ElementLength), i => Model[art.Start + 2 * art.ElementLength + i] != 0xFF);
         Assert.Contains("ring icon is unchanged", result.Message); // no icon tables in this ROM
      }
   }
}
