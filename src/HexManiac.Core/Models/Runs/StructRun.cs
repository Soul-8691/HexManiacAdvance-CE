using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HavenSoft.HexManiac.Core.Models.Runs {
   /// <summary>
   /// A struct living at a pointer's destination: an ordered field list, like a single row of an
   /// ArrayRun, but able to contain InlineArraySegment sections whose repeat count is read from an
   /// earlier sibling field (by name) rather than known from the format string alone. ElementCount
   /// is always 1 - this represents one struct's fields, not a repeating table.
   /// </summary>
   public class StructRun : BaseRun, ITableRun {
      private readonly IDataModel model;

      public IReadOnlyList<ArrayRunElementSegment> RawFieldSpecs { get; }
      public IReadOnlyList<ArrayRunElementSegment> ElementContent { get; }
      public override int Length { get; }
      public override string FormatString { get; }

      public int ElementCount => 1;
      public int ElementLength => Length;
      public IReadOnlyList<string> ElementNames { get; } = new[] { string.Empty };
      public bool CanAppend => false;

      public static bool TryParseFields(string content, IDataModel model, out List<ArrayRunElementSegment> fields) {
         fields = null;
         if (string.IsNullOrWhiteSpace(content)) return false;
         try {
            var parsed = ArrayRun.ParseSegments(content, model);
            if (parsed.Count == 0) return false;
            fields = parsed;
            return true;
         } catch (ArrayRunParseException) {
            return false;
         } catch (Exception) {
            // model may be null here (this can run as a cheap syntactic gate check before a
            // real model is available) - any field type that dereferences it (e.g. a nested
            // pointer<...> segment touching model.FormatRunFactory) just means "not a struct".
            return false;
         }
      }

      public static int ComputeMinimalLength(IReadOnlyList<ArrayRunElementSegment> specs) =>
         specs.Sum(spec => spec is InlineArraySegment ? 0 : spec.Length);

      public StructRun(IDataModel model, int start, SortedSpan<int> sources, string formatString, IReadOnlyList<ArrayRunElementSegment> rawFieldSpecs) : base(start, sources) {
         this.model = model;
         FormatString = formatString;
         RawFieldSpecs = rawFieldSpecs;
         ElementContent = Expand(model, start, rawFieldSpecs);
         Length = ElementContent.Sum(seg => seg.Length);
         if (Length == 0) throw new ArrayRunParseException("Struct Content Length must not be zero.");
      }

      // resolves each InlineArraySegment by finding its count field among the segments already
      // placed (so the count field must precede its array - there's no other way to know where
      // the array's bytes end without already knowing how many of them there are).
      private static List<ArrayRunElementSegment> Expand(IDataModel model, int start, IReadOnlyList<ArrayRunElementSegment> specs) {
         var result = new List<ArrayRunElementSegment>();
         foreach (var spec in specs) {
            if (spec is InlineArraySegment arraySpec) {
               var countIndex = result.FindIndex(s => s.Name == arraySpec.CountFieldName);
               if (countIndex == -1) throw new ArrayRunParseException($"Inline array '{arraySpec.Name}' must be preceded by its count field '{arraySpec.CountFieldName}'.");
               var countOffset = result.Take(countIndex).Sum(s => s.Length);
               var count = model.ReadMultiByteValue(start + countOffset, result[countIndex].Length);
               for (int i = 0; i < count; i++) result.Add(arraySpec.Template);
            } else {
               result.Add(spec);
            }
         }
         return result;
      }

      /// <summary>
      /// Resizes (and relocates, if the new content doesn't fit where the struct currently lives)
      /// this struct so each key in `overrides` holds exactly its given values (its own count field
      /// is updated to match). Any InlineArraySegment not present in `overrides` keeps its current
      /// content, padded with 0s or truncated to whatever its count field's CURRENT model value
      /// says - this covers a user directly editing a count field (the new value is already written
      /// to the model before this runs; there's no new card data to go with it, just a longer or
      /// shorter array of defaults). Every other field is carried across unchanged, at its
      /// possibly-shifted new offset. Used by both .ydk import (explicit new card lists, see
      /// YdkDeckImport) and NotifyChildren (a bare count-field edit).
      /// </summary>
      public IFormattedRun Resize(IDataModel model, ModelDelta token, IReadOnlyDictionary<InlineArraySegment, IReadOnlyList<int>> overrides) {
         // how many elements each array currently has, according to THIS (possibly stale) instance:
         // ElementContent was fixed at construction time and doesn't follow later, unrelated writes
         // to the model, so it reflects "before" state even after the model's raw bytes have moved on.
         var oldCounts = new Dictionary<ArrayRunElementSegment, int>();
         {
            int i = 0;
            foreach (var spec in RawFieldSpecs) {
               if (spec is InlineArraySegment arraySpec) {
                  int count = 0;
                  while (i < ElementContent.Count && ReferenceEquals(ElementContent[i], arraySpec.Template)) { count++; i++; }
                  oldCounts[spec] = count;
               } else {
                  oldCounts[spec] = 1;
                  i++;
               }
            }
         }

         var currentValues = new Dictionary<ArrayRunElementSegment, IReadOnlyList<int>>();
         var currentOffsets = new Dictionary<ArrayRunElementSegment, int>();
         {
            int offset = 0;
            foreach (var spec in RawFieldSpecs) {
               currentOffsets[spec] = offset;
               var slotLength = spec is InlineArraySegment ia ? ia.Template.Length : spec.Length;
               var count = oldCounts[spec];
               var values = new List<int>();
               for (int i = 0; i < count; i++) values.Add(model.ReadMultiByteValue(Start + offset + i * slotLength, slotLength));
               currentValues[spec] = values;
               offset += slotLength * count;
            }
         }

         var finalValues = new Dictionary<ArrayRunElementSegment, IReadOnlyList<int>>(currentValues);
         foreach (var pair in overrides) {
            finalValues[pair.Key] = pair.Value;
            var countField = RawFieldSpecs.First(s => s.Name == pair.Key.CountFieldName);
            finalValues[countField] = new[] { pair.Value.Count };
         }

         // any InlineArraySegment NOT given explicit new content still needs to match whatever its
         // count field's CURRENT model value says - it may already have been written (e.g. the user
         // just typed a new count directly, with no accompanying card data).
         foreach (var spec in RawFieldSpecs) {
            if (spec is not InlineArraySegment arraySpec || overrides.ContainsKey(arraySpec)) continue;
            var countField = RawFieldSpecs.First(s => s.Name == arraySpec.CountFieldName);
            var liveCount = model.ReadMultiByteValue(Start + currentOffsets[countField], countField.Length);
            var old = currentValues[arraySpec];
            if (liveCount == old.Count) continue;
            var resized = old.Take(liveCount).ToList();
            while (resized.Count < liveCount) resized.Add(0);
            finalValues[arraySpec] = resized;
            finalValues[countField] = new[] { liveCount };
         }

         var finalOffsets = new Dictionary<ArrayRunElementSegment, int>();
         int newLength = 0;
         foreach (var spec in RawFieldSpecs) {
            finalOffsets[spec] = newLength;
            var slotLength = spec is InlineArraySegment ia ? ia.Template.Length : spec.Length;
            newLength += slotLength * finalValues[spec].Count;
         }

         var movedRun = model.RelocateForExpansion(token, this, newLength);
         if (movedRun.Start == Start && newLength < Length) {
            for (int i = newLength; i < Length; i++) token.ChangeData(model, Start + i, 0);
         }

         foreach (var spec in RawFieldSpecs) {
            var values = finalValues[spec];
            var slotLength = spec is InlineArraySegment ia ? ia.Template.Length : spec.Length;
            var baseOffset = movedRun.Start + finalOffsets[spec];
            for (int i = 0; i < values.Count; i++) model.WriteMultiByteValue(baseOffset + i * slotLength, slotLength, token, values[i]);
         }

         var finalRun = new StructRun(model, movedRun.Start, movedRun.PointerSources, FormatString, RawFieldSpecs);
         model.ObserveRunWritten(token, finalRun);
         return finalRun;
      }

      public override IDataFormat CreateDataFormat(IDataModel data, int index) => this.CreateSegmentDataFormat(data, index);

      protected override BaseRun Clone(SortedSpan<int> newPointerSources) => new StructRun(model, Start, newPointerSources, FormatString, RawFieldSpecs);

      public ITableRun Append(ModelDelta token, int length) => throw new NotImplementedException();

      public ITableRun Duplicate(int start, SortedSpan<int> pointerSources, IReadOnlyList<ArrayRunElementSegment> segments) => throw new NotImplementedException();

      public void AppendTo(IDataModel model, StringBuilder builder, int start, int length, int depth) => ITableRunExtensions.AppendTo(this, model, builder, start, length, depth);

      public void Clear(IDataModel model, ModelDelta changeToken, int start, int length) => ITableRunExtensions.Clear(this, model, changeToken, start, length);
   }
}
