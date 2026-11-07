using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;

namespace HavenSoft.HexManiac.Core.Models.Runs {
   /// <summary>
   /// Imports EDOPro/Duelingbook-style .ydk deck files into a table whose card field is an
   /// ArrayRunEnumSegment with a PasswordTableHint (see ArrayRunEnumSegment.PasswordTableHint).
   /// Each .ydk line is an 8-digit card password. Passwords are stored in ROM as 4-byte packed
   /// BCD, so (since every BCD digit is also a valid hex digit) resolution is just "parse as hex,
   /// find the matching value in the passwords table, use that index." The byte order differs by
   /// game: WCT06 stores the value little-endian (password 12345678 is the bytes 78 56 34 12),
   /// EDS in text order (the bytes 12 34 56 78, read little-endian as 0x78563412). Neither the
   /// table nor its format says which, so each import uses whichever order matches more of the
   /// .ydk's passwords (see DetectTextOrder).
   /// </summary>
   public static class YdkDeckImport {
      /// <param name="Note">Extra detail for the user, e.g. that a fixed-size table didn't match the deck's size.</param>
      public readonly record struct Result(int Imported, int Skipped, string Note = null);

      public static Result Import(IDataModel model, ModelDelta token, ITableRun table, ArrayRunEnumSegment segment, string ydkText) {
         var passwordsTable = model.GetTable(segment.PasswordTableHint);
         var (main, extra, side) = ParseSections(ydkText);
         if (passwordsTable == null) return new Result(0, main.Count + extra.Count + side.Count);
         var textOrder = DetectTextOrder(model, passwordsTable, main.Concat(extra).Concat(side));

         // a StructRun's InlineArraySegment fields (e.g. deckinfo's `main`/`extra` card lists) have
         // a size driven by their own count field, so growing/shrinking a deck needs to resize (and
         // possibly relocate) the struct - a repeating ArrayRun/TableStreamRun has no such per-field
         // count to resize, so it keeps the older "fill whatever slots already exist" behavior.
         if (table is StructRun structRun) return ImportIntoStruct(model, token, structRun, segment, main, extra, side, passwordsTable, textOrder);

         var anchorName = model.GetAnchorFromAddress(-1, table.Start) ?? string.Empty;
         var passwords = SelectSection(anchorName, segment.Name, main, extra, side);

         // A deck kept as a plain card array, e.g. EDS's data.decks.mod.<deck>.main ([card:...]55),
         // reached through a DeckList row like `[cards<> count: unused:]` - the game reads exactly
         // `count` cards from it, so it can be resized to fit. With no sibling .extra table, a .main
         // array like that holds the Fusion (Extra) Deck after the Main Deck, which is how EDS's
         // LoadOpponentDeck expects it.
         if (table is ArrayRun arrayRun && arrayRun.ElementContent.Count == 1 && string.IsNullOrEmpty(arrayRun.LengthFromAnchor)) {
            var countField = FindDeckCountField(model, arrayRun);
            if (countField != null) {
               if (anchorName.EndsWith(".main", StringComparison.OrdinalIgnoreCase)
                  && model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, anchorName[..^".main".Length] + ".extra") == Pointer.NULL) {
                  passwords = main.Concat(extra).ToList();
               }
               return ImportIntoCountedArray(model, token, arrayRun, segment, passwords, passwordsTable, countField.Value, textOrder);
            }
         }

         // table.ElementCount/ElementLength assume one fixed-size row per element, which only
         // covers a repeating ArrayRun/TableStreamRun (one card field per row). A StructRun
         // instead has ElementCount 1 with this same field repeated many times within
         // ElementContent (one per InlineArraySegment repetition) - so find every occurrence by
         // name across every row, rather than assuming exactly one slot per row.
         var slots = FindSlots(table, segment);
         var passwordLength = passwordsTable.ElementContent[0].Length;
         var cardNames = passwordsTable.ElementNames;

         int imported = 0, skipped = 0;
         var count = Math.Min(passwords.Count, slots.Count);
         for (int i = 0; i < count; i++) {
            var finalIndex = ResolvePassword(model, segment, passwordsTable, passwordLength, cardNames, passwords[i], textOrder);
            if (finalIndex == null) { skipped++; continue; }
            model.WriteMultiByteValue(slots[i], segment.Length, token, finalIndex.Value);
            imported++;
         }

         // nothing records this table's size, so it can't grow or shrink to fit the deck
         string note = null;
         if (passwords.Count > slots.Count) {
            note = $"This table has a fixed size of {slots.Count} card(s), so the last {passwords.Count - slots.Count} card(s) of the .ydk section were left out.";
         } else if (passwords.Count < slots.Count) {
            note = $"This table has a fixed size of {slots.Count} card(s); the .ydk section has {passwords.Count}, so the last {slots.Count - passwords.Count} slot(s) were left unchanged.";
         }
         return new Result(imported, skipped, note);
      }

      /// <summary>
      /// Resolves a password string to this segment's stored enum value via passwordsTable, or
      /// null if the password text is malformed, unknown, or doesn't resolve through cardNames.
      /// data.cards.passwords has no name field of its own - its row count comes from a named
      /// list (e.g. Format = '[password::|h]cardstatsnames'), which gives it per-row ElementNames
      /// labels. The card field may use a *different* list (cardnames, cardinternalnumbers), so the
      /// matched row index isn't meaningful there - resolve by name instead.
      /// </summary>
      private static int? ResolvePassword(IDataModel model, ArrayRunEnumSegment segment, ITableRun passwordsTable, int passwordLength, IReadOnlyList<string> cardNames, string passwordText, bool textOrder) {
         var p = FindPasswordRow(model, passwordsTable, passwordLength, passwordText, textOrder);
         if (p < 0 || p >= cardNames.Count || !segment.TryParse(model, cardNames[p], out var finalIndex)) return null;
         return finalIndex;
      }

      /// <summary>The passwords table row holding this password, or -1.</summary>
      /// <param name="textOrder">true when the ROM stores the password's bytes in text order (EDS), false for little-endian (WCT06).</param>
      private static int FindPasswordRow(IDataModel model, ITableRun passwordsTable, int passwordLength, string passwordText, bool textOrder) {
         if (!int.TryParse(passwordText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var passwordValue)) return -1;
         if (textOrder) passwordValue = ReverseBytes(passwordValue, passwordLength);
         for (int p = 0; p < passwordsTable.ElementCount; p++) {
            if (model.ReadMultiByteValue(passwordsTable.Start + p * passwordsTable.ElementLength, passwordLength) == passwordValue) return p;
         }
         return -1;
      }

      private static int ReverseBytes(int value, int length) {
         int result = 0;
         for (int i = 0; i < length; i++) result |= ((value >> (8 * i)) & 0xFF) << (8 * (length - 1 - i));
         return result;
      }

      /// <summary>
      /// Whether the passwords table stores passwords in text byte order (EDS) rather than
      /// little-endian (WCT06): whichever order finds more of the .ydk's passwords. A tie (e.g. no
      /// passwords, or none found either way) keeps little-endian, the original behavior.
      /// </summary>
      private static bool DetectTextOrder(IDataModel model, ITableRun passwordsTable, IEnumerable<string> passwords) {
         var passwordLength = passwordsTable.ElementContent[0].Length;
         var stored = new HashSet<int>();
         for (int p = 0; p < passwordsTable.ElementCount; p++) {
            stored.Add(model.ReadMultiByteValue(passwordsTable.Start + p * passwordsTable.ElementLength, passwordLength));
         }
         int littleEndian = 0, textOrder = 0;
         foreach (var text in passwords) {
            if (!int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)) continue;
            if (stored.Contains(value)) littleEndian++;
            if (stored.Contains(ReverseBytes(value, passwordLength))) textOrder++;
         }
         return textOrder > littleEndian;
      }

      /// <summary>
      /// Resizes (and relocates, if the new content doesn't fit where the struct currently lives)
      /// every InlineArraySegment in the struct whose template matches `segment`'s name/password
      /// hint to hold exactly the resolved cards for its own section (found via its CountFieldName,
      /// e.g. "main"/"extra"), then writes the struct back out - preserving every field that wasn't
      /// being imported into (including other InlineArraySegments) at its correct, possibly-shifted
      /// position.
      /// </summary>
      private static Result ImportIntoStruct(IDataModel model, ModelDelta token, StructRun structRun, ArrayRunEnumSegment segment, List<string> main, List<string> extra, List<string> side, ITableRun passwordsTable, bool textOrder) {
         var passwordLength = passwordsTable.ElementContent[0].Length;
         var cardNames = passwordsTable.ElementNames;
         var anchorName = model.GetAnchorFromAddress(-1, structRun.Start) ?? string.Empty;

         var targets = structRun.RawFieldSpecs
            .OfType<InlineArraySegment>()
            .Where(spec => spec.Template is ArrayRunEnumSegment inner && inner.Name == segment.Name && inner.PasswordTableHint == segment.PasswordTableHint)
            .ToList();
         if (targets.Count == 0) return new Result(0, 0);

         int imported = 0, skipped = 0;
         var resolvedPerArray = new Dictionary<InlineArraySegment, List<int>>();
         foreach (var arraySpec in targets) {
            var passwords = SelectSection(arraySpec.CountFieldName, arraySpec.CountFieldName, main, extra, side, allowFallback: false)
                          ?? SelectSection(anchorName, segment.Name, main, extra, side);
            var resolved = new List<int>();
            foreach (var password in passwords) {
               var value = ResolvePassword(model, segment, passwordsTable, passwordLength, cardNames, password, textOrder);
               if (value == null) { skipped++; continue; }
               resolved.Add(value.Value);
            }
            resolvedPerArray[arraySpec] = resolved;
            imported += resolved.Count;
         }

         var overrides = resolvedPerArray.ToDictionary(pair => pair.Key, pair => (IReadOnlyList<int>)pair.Value);
         structRun.Resize(model, token, overrides);

         return new Result(imported, skipped);
      }

      /// <summary>
      /// The deck size field for a plain card array: the field right after a pointer to the
      /// array's start, when that field's name ends in "count" (e.g. EDS's DeckList
      /// `[cards&lt;&gt; count: unused:]`, or a recipe's `side&lt;&gt; sidecount::`). Null if nothing points
      /// to the array that way, i.e. its size is fixed (e.g. EDS's side decks, whose counts are
      /// compiled into the game's code).
      /// </summary>
      private static (int Address, int Length)? FindDeckCountField(IDataModel model, ArrayRun array) {
         foreach (var source in array.PointerSources ?? SortedSpan<int>.None) {
            if (model.GetNextRun(source) is not ITableRun parent || parent.Start > source) continue;
            var offsets = parent.ConvertByteOffsetToArrayOffset(source);
            if (offsets.SegmentIndex < 0 || offsets.SegmentIndex + 1 >= parent.ElementContent.Count) continue;
            if (parent.ElementContent[offsets.SegmentIndex].Type != ElementContentType.Pointer) continue;
            var next = parent.ElementContent[offsets.SegmentIndex + 1];
            if (next.Type != ElementContentType.Integer || !next.Name.EndsWith("count", StringComparison.OrdinalIgnoreCase)) continue;
            return (source + parent.ElementContent[offsets.SegmentIndex].Length, next.Length);
         }
         return null;
      }

      /// <summary>
      /// Resizes the card array (relocating it, with its pointers updated, if it has to grow past
      /// where it is) to hold exactly the resolved cards, writes them, then sets the deck's count.
      /// A deck where nothing resolves is left alone rather than emptied.
      /// </summary>
      private static Result ImportIntoCountedArray(IDataModel model, ModelDelta token, ArrayRun array, ArrayRunEnumSegment segment, List<string> passwords, ITableRun passwordsTable, (int Address, int Length) countField, bool textOrder) {
         var passwordLength = passwordsTable.ElementContent[0].Length;
         var cardNames = passwordsTable.ElementNames;
         var resolved = new List<int>();
         int skipped = 0;
         foreach (var password in passwords) {
            var value = ResolvePassword(model, segment, passwordsTable, passwordLength, cardNames, password, textOrder);
            if (value == null) skipped++;
            else resolved.Add(value.Value);
         }
         if (resolved.Count == 0) return new Result(0, skipped);
         var maxCount = (1L << (countField.Length * 8)) - 1;
         if (resolved.Count > maxCount) {
            skipped += resolved.Count - (int)maxCount;
            resolved = resolved.Take((int)maxCount).ToList();
         }

         var delta = resolved.Count - array.ElementCount;
         var run = array;
         if (delta > 0) {
            run = model.RelocateForExpansion(token, run, run.Length + delta * run.ElementLength);
            run = run.Append(token, delta);
            model.ObserveRunWritten(token, run);
         } else if (delta < 0) {
            var oldEnd = run.Start + run.Length;
            run = run.Append(token, delta);
            model.ObserveRunWritten(token, run);
            model.ClearData(token, run.Start + run.Length, oldEnd - (run.Start + run.Length));
         }

         for (int i = 0; i < resolved.Count; i++) {
            model.WriteMultiByteValue(run.Start + i * run.ElementLength, segment.Length, token, resolved[i]);
         }
         model.WriteMultiByteValue(countField.Address, countField.Length, token, resolved.Count);
         return new Result(resolved.Count, skipped);
      }

      private static List<int> FindSlots(ITableRun table, ArrayRunEnumSegment segment) {
         var slots = new List<int>();
         for (int row = 0; row < table.ElementCount; row++) {
            var offset = table.Start + row * table.ElementLength;
            foreach (var seg in table.ElementContent) {
               if (seg.Name == segment.Name && seg is ArrayRunEnumSegment enumSeg && enumSeg.PasswordTableHint == segment.PasswordTableHint) {
                  slots.Add(offset);
               }
               offset += seg.Length;
            }
         }
         return slots;
      }

      private static List<string> SelectSection(string anchorName, string segmentName, List<string> main, List<string> extra, List<string> side, bool allowFallback = true) {
         // an anonymous struct (reached only by following a pointer) has no anchor name of its
         // own, so fall back to the clicked field's name - "main"/"extra" are exactly the field
         // names used for this purpose, matching the .ydk section they should pull from.
         foreach (var name in new[] { anchorName, segmentName }) {
            if (name.Contains("main", StringComparison.OrdinalIgnoreCase)) return main;
            if (name.Contains("extra", StringComparison.OrdinalIgnoreCase)) return extra;
            if (name.Contains("side", StringComparison.OrdinalIgnoreCase)) return side;
         }
         return allowFallback ? main.Concat(extra).Concat(side).ToList() : null;
      }

      private static (List<string> main, List<string> extra, List<string> side) ParseSections(string ydkText) {
         var main = new List<string>();
         var extra = new List<string>();
         var side = new List<string>();
         var current = main;

         foreach (var rawLine in ydkText.Split('\n')) {
            var line = rawLine.Trim();
            if (line.Length == 0) continue;
            if (line.Equals("#main", StringComparison.OrdinalIgnoreCase)) { current = main; continue; }
            if (line.Equals("#extra", StringComparison.OrdinalIgnoreCase)) { current = extra; continue; }
            if (line.Equals("!side", StringComparison.OrdinalIgnoreCase)) { current = side; continue; }
            if (line.StartsWith("#") || line.StartsWith("!")) continue; // other comments/headers
            if (!line.All(char.IsDigit)) continue; // not a password line
            current.Add(line);
         }

         return (main, extra, side);
      }
   }
}
