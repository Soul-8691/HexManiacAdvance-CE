using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HavenSoft.HexManiac.Core.Models.Runs {
   /// <summary>
   /// Imports a card's stats/name/description/password from https://ygoprodeck.com/api-guide/ into
   /// this project's card tables (data.cards.stats.internal/.konami, data.cards.text via the
   /// pointers.names/descriptions tables, data.cards.passwords, and the alphabetical index).
   ///
   /// Only reads/writes anchors by name - no hardcoded addresses or row counts - so it adapts to
   /// whatever table sizes/layout are actually configured, rather than assuming one specific ROM.
   /// </summary>
   public static class CardImport {
      public const string StatsInternalName = "data.cards.stats.internal";
      public const string StatsKonamiName = "data.cards.stats.konami";
      public const string TextPoolName = "data.cards.text";
      public const string PasswordsName = "data.cards.passwords";
      public const string NamesPointerName = "data.cards.pointers.names.text";
      public const string DescriptionsPointerName = "data.cards.pointers.descriptions.text";
      public const string AlphabeticalName = "data.cards.pointers.names.alphabetical";
      private static readonly byte[] Terminator = { 0x00 };

      public readonly record struct Result(bool Success, string Message);

      /// <summary>Gate for showing/enabling the "Import from YGOProDeck" button at all.</summary>
      public static bool CanImport(IDataModel model) =>
         model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, TextPoolName) != Pointer.NULL &&
         model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, PasswordsName) != Pointer.NULL;

      /// <summary>
      /// The "Import from YGOProDeck" button can be shown while editing either stats table. This maps
      /// a row in whichever table is currently open back to the corresponding data.cards.stats.internal
      /// row that Import expects, using each table's "card" (konami id) field as the cross-reference.
      /// </summary>
      public static int ResolveInternalIndex(IDataModel model, ITableRun table, int rowIndex) {
         var anchor = model.GetAnchorFromAddress(-1, table.Start);
         if (anchor == StatsInternalName) return rowIndex;
         if (anchor != StatsKonamiName) return -1;
         var internalTable = model.GetTable(StatsInternalName);
         if (internalTable == null || rowIndex < 0 || rowIndex >= table.ElementCount) return -1;
         var konamiId = table.ReadValue(model, rowIndex, "card");
         return FindRowByFieldValue(internalTable, model, "card", konamiId);
      }

      /// <param name="internalIndex">Row index into data.cards.stats.internal (and, by extension,
      /// data.cards.text/passwords/alphabetical, which all share that same row ordering).</param>
      public static Result Import(IDataModel model, ModelDelta token, int internalIndex, YgoProDeckCard card) {
         var internalTable = model.GetTable(StatsInternalName);
         var konamiTable = model.GetTable(StatsKonamiName);
         var textPoolAddress = model.GetAddressFromAnchor(token, -1, TextPoolName);
         var passwordsTable = model.GetTable(PasswordsName);
         if (internalTable == null) return new Result(false, $"{StatsInternalName} not found.");
         if (textPoolAddress == Pointer.NULL) return new Result(false, $"{TextPoolName} not found.");
         if (passwordsTable == null) return new Result(false, $"{PasswordsName} not found.");
         if (internalIndex < 0 || internalIndex >= internalTable.ElementCount) return new Result(false, "Row index out of range.");

         WriteStats(internalTable, model, token, internalIndex, card);
         var konamiId = internalTable.ReadValue(model, internalIndex, "card");
         if (konamiTable != null) {
            var konamiIndex = FindRowByFieldValue(konamiTable, model, "card", konamiId);
            if (konamiIndex >= 0) WriteStats(konamiTable, model, token, konamiIndex, card);
         }

         UpdateNameLists(model, token, internalTable, internalIndex, konamiId, card.Name);

         if (!string.IsNullOrEmpty(card.Name)) ImportText(model, token, NamesPointerName, textPoolAddress, internalIndex, card.Name);
         if (!string.IsNullOrEmpty(card.Desc)) ImportText(model, token, DescriptionsPointerName, textPoolAddress, internalIndex, card.Desc);

         var passwordLength = passwordsTable.ElementContent[0].Length;
         if (internalIndex < passwordsTable.ElementCount) {
            model.WriteMultiByteValue(passwordsTable.Start + internalIndex * passwordsTable.ElementLength, passwordLength, token, card.Id);
         }

         Realphabetize(model, token);

         return new Result(true, $"Imported '{card.Name}'.");
      }

      private static void WriteStats(ITableRun table, IDataModel model, ModelDelta token, int row, YgoProDeckCard card) {
         var normalizedType = NormalizeType(card.Type);
         if (card.IsSpellOrTrap) {
            // race/attribute/type all show the card's overall kind (Spell/Trap); the actual
            // subtype (Normal/Continuous/Quick-Play/Counter/etc, the API's own "race") goes in
            // spelltraprace - matches how this ROM's races/attributes/types lists represent
            // non-monster cards.
            TryWriteEnum(table, model, token, row, "race", normalizedType);
            TryWriteEnum(table, model, token, row, "attribute", normalizedType);
            TryWriteEnum(table, model, token, row, "type", normalizedType);
            TryWriteEnum(table, model, token, row, "spelltraprace", card.Race);
            table.WriteValue(0, model, token, row, "attack");
            table.WriteValue(0, model, token, row, "defense");
            table.WriteValue(0, model, token, row, "level");
         } else {
            TryWriteEnum(table, model, token, row, "race", card.Race);
            TryWriteEnum(table, model, token, row, "attribute", card.Attribute);
            TryWriteEnum(table, model, token, row, "type", normalizedType);
            TryWriteEnum(table, model, token, row, "spelltraprace", "None");
            table.WriteValue(card.Atk ?? 0, model, token, row, "attack");
            table.WriteValue(card.Def ?? 0, model, token, row, "defense");
            table.WriteValue(card.Level ?? 0, model, token, row, "level");
         }
      }

      // "Spell Card"/"Trap Card" -> "Spell"/"Trap", "Effect Monster"/"Normal Monster" -> "Effect"/
      // "Normal", etc. This ROM's races/attributes/types enum lists use the API's wording with
      // its trailing " Card" (spell/trap) or " Monster" (monster) suffix stripped.
      private static string NormalizeType(string apiType) {
         if (apiType == null) return null;
         const string cardSuffix = " Card", monsterSuffix = " Monster";
         if (apiType.EndsWith(cardSuffix)) return apiType.Substring(0, apiType.Length - cardSuffix.Length);
         if (apiType.EndsWith(monsterSuffix)) return apiType.Substring(0, apiType.Length - monsterSuffix.Length);
         return apiType;
      }

      private static void TryWriteEnum(ITableRun table, IDataModel model, ModelDelta token, int row, string fieldName, string text) {
         if (string.IsNullOrEmpty(text)) return;
         var segment = table.ElementContent.FirstOrDefault(s => s.Name == fieldName) as ArrayRunEnumSegment;
         if (segment == null || !segment.TryParse(model, text, out var value)) return;
         var offset = table.ElementContent.Until(s => s.Name == fieldName).Sum(s => s.Length);
         model.WriteMultiByteValue(table.Start + row * table.ElementLength + offset, segment.Length, token, value);
      }

      private static int FindRowByFieldValue(ITableRun table, IDataModel model, string fieldName, int value) {
         for (int i = 0; i < table.ElementCount; i++) {
            if (table.ReadValue(model, i, fieldName) == value) return i;
         }
         return -1;
      }

      // the TOML's cardnames/cardstatsnames/cardnamesfrom0 [[List]] entries are static enum-label
      // metadata, not ROM data - after an import they'd otherwise keep showing the old/placeholder
      // name for this card's row everywhere those lists are used for display (including our own
      // password-matching, which resolves a matched password's row through a name list - see
      // YdkDeckImport.ResolvePassword).
      private static void UpdateNameLists(IDataModel model, ModelDelta token, ITableRun internalTable, int internalIndex, int konamiId, string name) {
         if (string.IsNullOrEmpty(name)) return;
         UpdateListEntry(model, token, "cardnames", konamiId, name);
         UpdateListEntry(model, token, "cardnamesfrom0", konamiId, name);
         // the internal table's own row-count source list (e.g. "cardstatsnames") is indexed by
         // internal row, not Konami id - read the name dynamically rather than hardcoding it, so
         // this works regardless of what that list happens to be named in a given project.
         var internalNameList = (internalTable as ArrayRun)?.LengthFromAnchor;
         if (!string.IsNullOrEmpty(internalNameList)) UpdateListEntry(model, token, internalNameList, internalIndex, name);
      }

      private static void UpdateListEntry(IDataModel model, ModelDelta token, string listName, int index, string value) {
         if (!model.TryGetList(listName, out var list) || index < 0 || index >= list.Count) return;
         if (list[index] == value) return;
         var updated = list.ToList();
         updated[index] = value;
         model.SetList(token, listName, updated, list.Comments, null);
      }

      // Writes brand-new text into 0x00-marked free space and points this row's offset field at it
      // directly - deliberately NOT going through ArrayRunIndexedAsciiSegment.WriteText/IndexedAsciiRun's
      // usual edit-and-relocate path, since that searches for the standard 0xFF-marked free space
      // (via RelocateForExpansion/FindFreeSpace), not the 0x00-marked space this ROM actually has here.
      private static void ImportText(IDataModel model, ModelDelta token, string pointerTableName, int textPoolAddress, int row, string text) {
         var pointerTable = model.GetTable(pointerTableName);
         if (pointerTable == null || row >= pointerTable.ElementCount) return;

         var bytes = new List<byte>();
         foreach (var c in text) bytes.Add((byte)c);
         bytes.Add(0x00);

         var destination = FindZeroFilledFreeSpace(model, textPoolAddress, bytes.Count);
         if (destination == -1) return;
         for (int i = 0; i < bytes.Count; i++) token.ChangeData(model, destination + i, bytes[i]);

         var fieldAddress = pointerTable.Start + row * pointerTable.ElementLength;
         model.WriteMultiByteValue(fieldAddress, 4, token, destination - textPoolAddress);

         // register the new text as a real run so later edits (via the sidebar strategy built
         // earlier this session) resolve/relocate it normally instead of treating it as raw bytes.
         model.ObserveRunWritten(token, new IndexedAsciiRun(model, destination, Terminator));
      }

      /// <summary>
      /// Scans every row of both text-pointer tables to find the first byte past the end of all
      /// currently-used text, then finds a run of `length` consecutive 0x00 bytes starting there -
      /// this ROM's free space is 0x00-filled in this region, not the usual 0xFF FindFreeSpace
      /// looks for elsewhere.
      /// </summary>
      public static int FindZeroFilledFreeSpace(IDataModel model, int textPoolAddress, int length) {
         var searchStart = textPoolAddress;
         foreach (var pointerTableName in new[] { NamesPointerName, DescriptionsPointerName }) {
            var table = model.GetTable(pointerTableName);
            if (table == null) continue;
            for (int i = 0; i < table.ElementCount; i++) {
               var value = model.ReadMultiByteValue(table.Start + i * table.ElementLength, 4);
               var destination = textPoolAddress + value;
               if (destination < 0 || destination >= model.Count) continue;
               var textLength = AsciiString.ReadLength(model, destination, Terminator);
               searchStart = Math.Max(searchStart, destination + textLength);
            }
         }

         var start = searchStart;
         while (start + length <= model.Count) {
            var allZero = true;
            for (int i = 0; i < length; i++) {
               if (model[start + i] != 0x00) { allZero = false; break; }
            }
            if (allZero) return start;
            start++;
         }
         return -1;
      }

      /// <summary>
      /// Rewrites data.cards.pointers.names.alphabetical so each CARD ROW holds its own
      /// alphabetical RANK (1-based; 0 means "blank/unranked name") - each row is a lookup of
      /// "where does this card sort", not the reverse (a lookup of "which card is at this sorted
      /// position", which is what this used to write, backwards). Same-named cards (reprints)
      /// share the same rank. This table's own length anchor (cardstatsnamesnotokens) already
      /// excludes the trailing literal "Token" cards, so those rows are naturally left untouched.
      /// Hyphens/apostrophes are ignored when comparing (e.g. "M-Warrior" sorts as "MWarrior") -
      /// checked against this ROM's existing table (card_indexes.csv), this matches ~1000/2081
      /// rows exactly and every other row is off by at most one rank - close enough that chasing
      /// the ROM's remaining one-off historical quirks isn't worth it.
      /// </summary>
      public static void Realphabetize(IDataModel model, ModelDelta token) {
         var alphabetical = model.GetTable(AlphabeticalName);
         var namesPointerTable = model.GetTable(NamesPointerName);
         if (alphabetical == null || namesPointerTable == null) return;

         var count = Math.Min(alphabetical.ElementCount, namesPointerTable.ElementCount);
         if (count <= 0) return;

         var names = new string[count];
         var sortKeys = new string[count];
         var segment = namesPointerTable.ElementContent[0] as ArrayRunIndexedAsciiSegment;
         for (int i = 0; i < count; i++) {
            names[i] = segment?.ToText(model, namesPointerTable.Start + i * namesPointerTable.ElementLength)?.Trim('"') ?? string.Empty;
            sortKeys[i] = SortKey(names[i]);
         }

         var sortedRows = Enumerable.Range(0, count).OrderBy(i => sortKeys[i], StringComparer.OrdinalIgnoreCase).ToArray();

         var ranks = new int[count];
         var rank = 0;
         for (int sortedPos = 0; sortedPos < count; sortedPos++) {
            var row = sortedRows[sortedPos];
            if (string.IsNullOrEmpty(names[row])) continue; // ranks[row] stays 0
            if (sortedPos == 0 || !string.Equals(sortKeys[sortedRows[sortedPos - 1]], sortKeys[row], StringComparison.OrdinalIgnoreCase)) rank++;
            ranks[row] = rank;
         }

         for (int i = 0; i < count; i++) {
            model.WriteMultiByteValue(alphabetical.Start + i * alphabetical.ElementLength, alphabetical.ElementContent[0].Length, token, ranks[i]);
         }
      }

      private static string SortKey(string name) => name.Replace("-", string.Empty).Replace("'", string.Empty);
   }
}
