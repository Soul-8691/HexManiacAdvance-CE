using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.Models.Runs;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class CardImportTests : BaseViewModelTestClass {
      public CardImportTests() : base(0x600) { }

      private const string Fields = "card:cardnames artwork: editedartwork:editedartwork attack: defense: level: " +
         "race:races attribute:attributes type:types spelltraprace:spelltrapraces padding:";

      private void SetupTables() {
         Model.SetList(Token, "cardnames", new[] { null, null, "CardA", "CardB" }, new System.Collections.Generic.Dictionary<int, string>(), null); // konami-id-keyed, starting at 0, ids 2/3 used
         Model.SetList(Token, "cardstatsnames", "CardA", "CardB"); // internal-row-keyed
         Model.SetList(Token, "cardnamesfrom0", "CardA", "CardB"); // konami-row-keyed (matches UME.toml's data.cards.stats.konami length anchor)
         Model.SetList(Token, "races", "None", "Dragon", "Spellcaster", "Spell", "Trap");
         Model.SetList(Token, "attributes", "None", "LIGHT", "DARK", "Spell", "Trap");
         // short-form labels, matching UME.toml's real "types" list (the API's own wording -
         // "Effect Monster", "Spell Card" - has to be stripped down to match these).
         Model.SetList(Token, "types", "None", "Effect", "Spell", "Trap");
         Model.SetList(Token, "spelltrapraces", "None", "Normal", "Continuous", "Quick-Play", "Counter");

         ViewPort.Edit($"@000 ^data.cards.stats.internal[{Fields}]cardstatsnames ");
         Model.WriteMultiByteValue(0x00, 2, Token, 2); // row 0 card = konami id 2 (CardA)
         Model.WriteMultiByteValue(0x16, 2, Token, 3); // row 1 card = konami id 3 (CardB)

         ViewPort.Edit($"@040 ^data.cards.stats.konami[{Fields}]cardnamesfrom0 ");
         Model.WriteMultiByteValue(0x40, 2, Token, 2);
         Model.WriteMultiByteValue(0x56, 2, Token, 3);

         ViewPort.Edit("@080 ^data.cards.passwords[password::|h]cardstatsnames ");
         ViewPort.Edit("@090 ^data.cards.pointers.names.text[text`asc`data.cards.text!00]cardstatsnames ");
         ViewPort.Edit("@0A0 ^data.cards.pointers.descriptions.text[text`asc`data.cards.text!00]cardstatsnames ");
         ViewPort.Edit("@0B0 ^data.cards.pointers.names.alphabetical[offset:]cardstatsnames ");
         ViewPort.Edit("@100 ^data.cards.text ");
      }

      private static YgoProDeckCard MonsterCard() => new() {
         Id = 12345678,
         Name = "Newly Imported Dragon",
         Desc = "A shiny new dragon.",
         Type = "Effect Monster",
         Race = "Dragon",
         Attribute = "LIGHT",
         Atk = 2500,
         Def = 2100,
         Level = 7,
      };

      private static YgoProDeckCard SpellCard() => new() {
         Id = 87654321,
         Name = "Newly Imported Spell",
         Desc = "A shiny new spell.",
         Type = "Spell Card",
         Race = "Quick-Play",
      };

      [Fact]
      public void CanImport_BothAnchorsExist_True() {
         SetupTables();
         Assert.True(CardImport.CanImport(Model));
      }

      [Fact]
      public void CanImport_MissingPasswordsAnchor_False() {
         ViewPort.Edit("@100 ^data.cards.text ");
         Assert.False(CardImport.CanImport(Model));
      }

      [Fact]
      public void Import_Monster_WritesStatsToBothTables() {
         SetupTables();

         var result = CardImport.Import(Model, Token, 0, MonsterCard());

         Assert.True(result.Success);
         var internalTable = Model.GetTable("data.cards.stats.internal");
         Assert.Equal(2500, internalTable.ReadValue(Model, 0, "attack"));
         Assert.Equal(2100, internalTable.ReadValue(Model, 0, "defense"));
         Assert.Equal(7, internalTable.ReadValue(Model, 0, "level"));
         Assert.Equal(1, internalTable.ReadValue(Model, 0, "race")); // "Dragon" is index 1 in races
         Assert.Equal(1, internalTable.ReadValue(Model, 0, "attribute")); // "LIGHT" is index 1 in attributes
         Assert.Equal(1, internalTable.ReadValue(Model, 0, "type")); // "Effect Monster" -> "Effect", index 1 in types
         Assert.Equal(0, internalTable.ReadValue(Model, 0, "spelltraprace")); // "None"

         // konami table's matching row (card id 2, found at its own row 0) gets the same stats
         var konamiTable = Model.GetTable("data.cards.stats.konami");
         Assert.Equal(2500, konamiTable.ReadValue(Model, 0, "attack"));
         Assert.Equal(1, konamiTable.ReadValue(Model, 0, "race"));
      }

      [Fact]
      public void Import_SpellCard_MapsRaceAttributeTypeToKindAndSpelltraprace() {
         SetupTables();

         var result = CardImport.Import(Model, Token, 0, SpellCard());

         Assert.True(result.Success);
         var internalTable = Model.GetTable("data.cards.stats.internal");
         Assert.Equal(3, internalTable.ReadValue(Model, 0, "race")); // "Spell" is index 3 in races
         Assert.Equal(3, internalTable.ReadValue(Model, 0, "attribute")); // "Spell" is index 3 in attributes
         Assert.Equal(2, internalTable.ReadValue(Model, 0, "type")); // "Spell Card" -> "Spell", index 2 in types
         Assert.Equal(3, internalTable.ReadValue(Model, 0, "spelltraprace")); // "Quick-Play"
         Assert.Equal(0, internalTable.ReadValue(Model, 0, "attack"));
         Assert.Equal(0, internalTable.ReadValue(Model, 0, "defense"));
         Assert.Equal(0, internalTable.ReadValue(Model, 0, "level"));
      }

      [Fact]
      public void Import_WritesPassword() {
         SetupTables();

         CardImport.Import(Model, Token, 0, MonsterCard());

         Assert.Equal(0x12345678, Model.ReadMultiByteValue(0x80, 4)); // BCD: the password's digits as hex digits
      }

      [Fact]
      public void Import_WritesNameAndDescriptionIntoZeroFilledFreeSpace() {
         SetupTables();

         CardImport.Import(Model, Token, 0, MonsterCard());

         var namesTable = Model.GetTable("data.cards.pointers.names.text");
         var nameOffset = Model.ReadMultiByteValue(namesTable.Start, 4);
         var nameAddress = 0x100 + nameOffset; // data.cards.text is at 0x100
         Assert.Equal("Newly Imported Dragon", System.Text.Encoding.ASCII.GetString(Model.RawData, nameAddress, "Newly Imported Dragon".Length));
         Assert.Equal(0, Model[nameAddress + "Newly Imported Dragon".Length]); // terminator

         var descTable = Model.GetTable("data.cards.pointers.descriptions.text");
         var descOffset = Model.ReadMultiByteValue(descTable.Start, 4);
         var descAddress = 0x100 + descOffset;
         Assert.Equal("A shiny new dragon.", System.Text.Encoding.ASCII.GetString(Model.RawData, descAddress, "A shiny new dragon.".Length));

         Assert.NotEqual(nameAddress, descAddress); // second text didn't overwrite the first
      }

      [Fact]
      public void Import_UpdatesNameListsAtCorrectIndices() {
         SetupTables();
         var internalTableBefore = Model.GetTable("data.cards.stats.internal");
         var konamiIdBefore = internalTableBefore.ReadValue(Model, 0, "card");

         var result = CardImport.Import(Model, Token, 0, MonsterCard()); // internal row 0, konami id 2
         Assert.True(result.Success, result.Message);
         Assert.Equal(2, konamiIdBefore);

         Assert.True(Model.TryGetList("cardnames", out var cardnames));
         Assert.Equal("Newly Imported Dragon", cardnames[2]);
         Assert.True(Model.TryGetList("cardstatsnames", out var cardstatsnames));
         Assert.Equal("Newly Imported Dragon", cardstatsnames[0]);
      }

      [Fact]
      public void Import_Realphabetizes() {
         SetupTables();

         // each ROW holds its own alphabetical RANK (1-based) - not a lookup of which row sorts
         // to a given position. Row 1 ("Aaa") sorts before row 0 ("Zzz"), so row 1 gets rank 1
         // and row 0 gets rank 2.
         CardImport.Import(Model, Token, 1, new YgoProDeckCard { Id = 1, Name = "Aaa", Desc = "x", Type = "Normal Monster", Race = "Dragon", Attribute = "LIGHT" });
         CardImport.Import(Model, Token, 0, new YgoProDeckCard { Id = 2, Name = "Zzz", Desc = "y", Type = "Normal Monster", Race = "Dragon", Attribute = "LIGHT" });

         var alphabetical = Model.GetTable("data.cards.pointers.names.alphabetical");
         Assert.Equal(2, Model.ReadMultiByteValue(alphabetical.Start, 2)); // row 0 ("Zzz") ranks 2nd
         Assert.Equal(1, Model.ReadMultiByteValue(alphabetical.Start + 2, 2)); // row 1 ("Aaa") ranks 1st
      }

      [Fact]
      public void Import_Realphabetizes_TiesSameNameToSameRank() {
         SetupTables();

         // both rows end up with the exact same name - they must share a rank (not get sequential
         // ranks the way plain "sorted position" numbering would).
         CardImport.Import(Model, Token, 0, new YgoProDeckCard { Id = 1, Name = "Same Name", Desc = "x", Type = "Normal Monster", Race = "Dragon", Attribute = "LIGHT" });
         CardImport.Import(Model, Token, 1, new YgoProDeckCard { Id = 2, Name = "Same Name", Desc = "y", Type = "Normal Monster", Race = "Dragon", Attribute = "LIGHT" });

         var alphabetical = Model.GetTable("data.cards.pointers.names.alphabetical");
         Assert.Equal(1, Model.ReadMultiByteValue(alphabetical.Start, 2));
         Assert.Equal(1, Model.ReadMultiByteValue(alphabetical.Start + 2, 2));
      }

      [Fact]
      public void Import_Realphabetizes_IgnoresHyphensAndApostrophesWhenComparing() {
         SetupTables();

         // "M-Warrior" and "MWarrior" must sort/tie identically - matches this ROM's own existing
         // alphabetical table (see card_indexes.csv), which ignores hyphens/apostrophes when
         // comparing names (e.g. "M-Warrior #1/#2" sort with the "MW..." names, not right after "M").
         CardImport.Import(Model, Token, 0, new YgoProDeckCard { Id = 1, Name = "M-Warrior", Desc = "x", Type = "Normal Monster", Race = "Dragon", Attribute = "LIGHT" });
         CardImport.Import(Model, Token, 1, new YgoProDeckCard { Id = 2, Name = "MWarrior", Desc = "y", Type = "Normal Monster", Race = "Dragon", Attribute = "LIGHT" });

         var alphabetical = Model.GetTable("data.cards.pointers.names.alphabetical");
         Assert.Equal(1, Model.ReadMultiByteValue(alphabetical.Start, 2));
         Assert.Equal(1, Model.ReadMultiByteValue(alphabetical.Start + 2, 2));
      }

      [Fact]
      public void FindZeroFilledFreeSpace_SkipsAlreadyUsedText() {
         SetupTables();
         CardImport.Import(Model, Token, 0, MonsterCard());

         var freeSpace = CardImport.FindZeroFilledFreeSpace(Model, 0x100, 5);

         // must not land inside the name/description text just written
         var namesTable = Model.GetTable("data.cards.pointers.names.text");
         var nameAddress = 0x100 + Model.ReadMultiByteValue(namesTable.Start, 4);
         var descTable = Model.GetTable("data.cards.pointers.descriptions.text");
         var descAddress = 0x100 + Model.ReadMultiByteValue(descTable.Start, 4);
         Assert.True(freeSpace >= nameAddress + "Newly Imported Dragon".Length + 1);
         Assert.True(freeSpace >= descAddress + "A shiny new dragon.".Length + 1);
      }
   }
}
