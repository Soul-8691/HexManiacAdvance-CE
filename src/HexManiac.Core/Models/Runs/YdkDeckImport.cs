using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace HavenSoft.HexManiac.Core.Models.Runs {
   /// <summary>
   /// Imports EDOPro/Duelingbook-style .ydk deck files into a table whose card field is an
   /// ArrayRunEnumSegment with a PasswordTableHint (see ArrayRunEnumSegment.PasswordTableHint).
   /// Each .ydk line is an 8-digit card password. Passwords are stored in ROM as 4-byte packed
   /// BCD, little-endian, which (since every BCD digit is also a valid hex digit) means a
   /// password's bytes are exactly its text parsed as hex - so resolution is just "parse as hex,
   /// find the matching value in the passwords table, use that index."
   /// </summary>
   public static class YdkDeckImport {
      public readonly record struct Result(int Imported, int Skipped);

      public static Result Import(IDataModel model, ModelDelta token, ITableRun table, ArrayRunEnumSegment segment, string ydkText) {
         var passwordsTable = model.GetTable(segment.PasswordTableHint);
         var (main, extra, side) = ParseSections(ydkText);
         if (passwordsTable == null) return new Result(0, main.Count + extra.Count + side.Count);

         // a StructRun's InlineArraySegment fields (e.g. deckinfo's `main`/`extra` card lists) have
         // a size driven by their own count field, so growing/shrinking a deck needs to resize (and
         // possibly relocate) the struct - a repeating ArrayRun/TableStreamRun has no such per-field
         // count to resize, so it keeps the older "fill whatever slots already exist" behavior.
         if (table is StructRun structRun) return ImportIntoStruct(model, token, structRun, segment, main, extra, side, passwordsTable);

         var anchorName = model.GetAnchorFromAddress(-1, table.Start) ?? string.Empty;
         var passwords = SelectSection(anchorName, segment.Name, main, extra, side);

         // table.ElementCount/ElementLength assume one fixed-size row per element, which only
         // covers a repeating ArrayRun/TableStreamRun (one card field per row). A StructRun
         // instead has ElementCount 1 with this same field repeated many times within
         // ElementContent (one per InlineArraySegment repetition) - so find every occurrence by
         // name across every row, rather than assuming exactly one slot per row.
         var slots = FindSlots(table, segment);
         var passwordLength = passwordsTable.ElementContent[0].Length;

         int imported = 0, skipped = 0;
         var count = Math.Min(passwords.Count, slots.Count);
         for (int i = 0; i < count; i++) {
            if (!int.TryParse(passwords[i], NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var passwordValue)) { skipped++; continue; }

            var matchIndex = -1;
            for (int p = 0; p < passwordsTable.ElementCount; p++) {
               if (model.ReadMultiByteValue(passwordsTable.Start + p * passwordsTable.ElementLength, passwordLength) != passwordValue) continue;
               matchIndex = p;
               break;
            }
            if (matchIndex == -1) { skipped++; continue; }

            // data.cards.passwords has no name field of its own - its row count comes from a
            // named list (e.g. Format = '[password::|h]cardstatsnames'), which is exactly what
            // gives it per-row ElementNames labels (the same mechanism species/move tables use
            // to label rows with no literal text field). cardnames is a *different* list, so the
            // matched password's row index isn't meaningful there - resolve by name instead.
            var cardNames = passwordsTable.ElementNames;
            if (matchIndex >= cardNames.Count || !segment.TryParse(model, cardNames[matchIndex], out var finalIndex)) { skipped++; continue; }

            model.WriteMultiByteValue(slots[i], segment.Length, token, finalIndex);
            imported++;
         }

         return new Result(imported, skipped);
      }

      /// <summary>
      /// Resolves a password string to this segment's stored enum value via passwordsTable, or
      /// null if the password text is malformed, unknown, or doesn't resolve through cardNames.
      /// </summary>
      private static int? ResolvePassword(IDataModel model, ArrayRunEnumSegment segment, ITableRun passwordsTable, int passwordLength, IReadOnlyList<string> cardNames, string passwordText) {
         if (!int.TryParse(passwordText, NumberStyles.HexNumber, CultureInfo.CurrentCulture, out var passwordValue)) return null;
         for (int p = 0; p < passwordsTable.ElementCount; p++) {
            if (model.ReadMultiByteValue(passwordsTable.Start + p * passwordsTable.ElementLength, passwordLength) != passwordValue) continue;
            if (p >= cardNames.Count || !segment.TryParse(model, cardNames[p], out var finalIndex)) return null;
            return finalIndex;
         }
         return null;
      }

      /// <summary>
      /// Resizes (and relocates, if the new content doesn't fit where the struct currently lives)
      /// every InlineArraySegment in the struct whose template matches `segment`'s name/password
      /// hint to hold exactly the resolved cards for its own section (found via its CountFieldName,
      /// e.g. "main"/"extra"), then writes the struct back out - preserving every field that wasn't
      /// being imported into (including other InlineArraySegments) at its correct, possibly-shifted
      /// position.
      /// </summary>
      private static Result ImportIntoStruct(IDataModel model, ModelDelta token, StructRun structRun, ArrayRunEnumSegment segment, List<string> main, List<string> extra, List<string> side, ITableRun passwordsTable) {
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
               var value = ResolvePassword(model, segment, passwordsTable, passwordLength, cardNames, password);
               if (value == null) { skipped++; continue; }
               resolved.Add(value.Value);
            }
            resolvedPerArray[arraySpec] = resolved;
            imported += resolved.Count;
         }

         // snapshot every field's current value(s) - for fields we're not importing into, this is
         // what carries them across a resize that shifts their byte offset (e.g. growing "main"
         // pushes "extra" later in the struct).
         var currentValues = new Dictionary<ArrayRunElementSegment, IReadOnlyList<int>>();
         var currentOffsets = new Dictionary<ArrayRunElementSegment, int>();
         {
            int offset = 0;
            foreach (var spec in structRun.RawFieldSpecs) {
               currentOffsets[spec] = offset;
               if (spec is InlineArraySegment arraySpec) {
                  // a count field always precedes its own array (StructRun.Expand requires it), so
                  // its value/offset is already in these dictionaries by the time we need it here.
                  var countField = structRun.RawFieldSpecs.First(s => s.Name == arraySpec.CountFieldName);
                  var count = model.ReadMultiByteValue(structRun.Start + currentOffsets[countField], countField.Length);
                  var values = new List<int>();
                  for (int i = 0; i < count; i++) values.Add(model.ReadMultiByteValue(structRun.Start + offset + i * arraySpec.Template.Length, arraySpec.Template.Length));
                  currentValues[spec] = values;
                  offset += count * arraySpec.Template.Length;
               } else {
                  currentValues[spec] = new[] { model.ReadMultiByteValue(structRun.Start + offset, spec.Length) };
                  offset += spec.Length;
               }
            }
         }

         var finalValues = new Dictionary<ArrayRunElementSegment, IReadOnlyList<int>>(currentValues);
         foreach (var pair in resolvedPerArray) {
            finalValues[pair.Key] = pair.Value;
            // the array's own count field is a separate field earlier in the struct - it must be
            // updated to match, or StructRun.Expand will keep reading the array's *old* length.
            var countField = structRun.RawFieldSpecs.First(s => s.Name == pair.Key.CountFieldName);
            finalValues[countField] = new[] { pair.Value.Count };
         }

         var finalOffsets = new Dictionary<ArrayRunElementSegment, int>();
         int newLength = 0;
         foreach (var spec in structRun.RawFieldSpecs) {
            finalOffsets[spec] = newLength;
            var slotLength = spec is InlineArraySegment ia ? ia.Template.Length : spec.Length;
            newLength += slotLength * finalValues[spec].Count;
         }

         var movedRun = model.RelocateForExpansion(token, structRun, newLength);
         if (movedRun.Start == structRun.Start && newLength < structRun.Length) {
            for (int i = newLength; i < structRun.Length; i++) token.ChangeData(model, structRun.Start + i, 0);
         }

         foreach (var spec in structRun.RawFieldSpecs) {
            var values = finalValues[spec];
            var slotLength = spec is InlineArraySegment ia ? ia.Template.Length : spec.Length;
            var baseOffset = movedRun.Start + finalOffsets[spec];
            for (int i = 0; i < values.Count; i++) model.WriteMultiByteValue(baseOffset + i * slotLength, slotLength, token, values[i]);
         }

         var finalRun = new StructRun(model, movedRun.Start, movedRun.PointerSources, structRun.FormatString, structRun.RawFieldSpecs);
         model.ObserveRunWritten(token, finalRun);

         return new Result(imported, skipped);
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
