using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using HavenSoft.HexManiac.Core.ViewModels.Images;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HavenSoft.HexManiac.Core.Models.Runs {
   /// <summary>
   /// Scans raw ascii bytes for a terminator byte sequence.
   /// </summary>
   public static class AsciiString {
      public const int DefaultMaxLength = 1024;

      /// <returns>The length of the string INCLUDING the terminator, if the terminator is found within maxLength bytes.
      /// Otherwise, maxLength (the string is assumed to be un-terminated and gets capped).</returns>
      public static int ReadLength(IReadOnlyList<byte> data, int start, IReadOnlyList<byte> terminator, int maxLength = DefaultMaxLength) {
         int length = 0;
         while (length < maxLength && start + length < data.Count) {
            if (MatchesTerminatorAt(data, start + length, terminator)) return length + terminator.Count;
            length++;
         }
         return System.Math.Min(length, maxLength);
      }

      private static bool MatchesTerminatorAt(IReadOnlyList<byte> data, int index, IReadOnlyList<byte> terminator) {
         if (index + terminator.Count > data.Count) return false;
         for (int i = 0; i < terminator.Count; i++) {
            if (data[index + i] != terminator[i]) return false;
         }
         return true;
      }
   }

   /// <summary>
   /// A dynamic-length, terminator-scanned raw ascii string, found at (baseAnchorAddress + storedOffset) rather than
   /// pointed to by a normal ROM pointer. See ArrayRunIndexedAsciiSegment for the field that references these.
   ///
   /// PointerSources is always empty: the offset fields that reference this run store a *relative* index, not the
   /// absolute address, so the usual pointer-relocation machinery (which writes a raw absolute address into every
   /// PointerSources entry when a run moves) would corrupt them. Instead, the referencing fields are tracked in
   /// Sources and updated directly with the correct relative math in DeserializeRun.
   /// </summary>
   public class IndexedAsciiRun : BaseRun, IStreamRun {
      public const int MaxLength = AsciiString.DefaultMaxLength;

      private readonly IDataModel model;

      public IReadOnlyList<byte> Terminator { get; }

      /// <summary>Offset fields (and the base anchor address each is relative to) that reference this string.</summary>
      public IReadOnlyList<(int FieldAddress, int BaseAddress)> Sources { get; }

      public override int Length { get; }
      public override string FormatString => "`asc`" + Terminator.Select(b => b.ToString("X2")).Aggregate(string.Empty, (a, b) => a + b);

      public IndexedAsciiRun(IDataModel model, int start, IReadOnlyList<byte> terminator, IReadOnlyList<(int, int)> sources = null) : base(start, SortedSpan<int>.None) {
         this.model = model;
         Terminator = terminator;
         Sources = sources ?? new List<(int, int)>();
         Length = AsciiString.ReadLength(model, start, terminator, MaxLength);
      }

      public IndexedAsciiRun WithSource(int fieldAddress, int baseAddress) {
         if (Sources.Any(s => s.FieldAddress == fieldAddress)) return this;
         return new IndexedAsciiRun(model, Start, Terminator, Sources.Append((fieldAddress, baseAddress)).ToList());
      }

      public override IDataFormat CreateDataFormat(IDataModel data, int index) => new Ascii(Start, index - Start, ((char)data[index]).ToString());

      protected override BaseRun Clone(SortedSpan<int> newPointerSources) => new IndexedAsciiRun(model, Start, Terminator, Sources);

      public string SerializeRun() {
         var textLength = System.Math.Max(0, Length - Terminator.Count);
         var builder = new StringBuilder();
         for (int i = 0; i < textLength; i++) builder.Append((char)model[Start + i]);
         return builder.ToString();
      }

      public IStreamRun DeserializeRun(string content, ModelDelta token, out IReadOnlyList<int> changedOffsets, out IReadOnlyList<int> changedRuns) {
         if (content.Length > MaxLength - Terminator.Count) content = content.Substring(0, MaxLength - Terminator.Count);
         var bytes = new List<byte>();
         foreach (var c in content) bytes.Add((byte)c);
         bytes.AddRange(Terminator);

         var newRun = model.RelocateForExpansion(token, this, bytes.Count);

         // clear out excess bytes that are no longer in use (0xFF, matching how free space is recognized elsewhere)
         if (Start == newRun.Start) {
            for (int i = bytes.Count; i < Length; i++) token.ChangeData(model, Start + i, 0xFF);
         }

         token.ChangeData(model, newRun.Start, bytes);

         var changedAddresses = new List<int>();
         foreach (var (fieldAddress, baseAddress) in Sources) {
            var newValue = newRun.Start - baseAddress;
            if (model.ReadMultiByteValue(fieldAddress, 4) != newValue) {
               model.WriteMultiByteValue(fieldAddress, 4, token, newValue);
               changedAddresses.Add(fieldAddress);
            }
         }

         changedOffsets = changedAddresses;
         changedRuns = new List<int>();
         return new IndexedAsciiRun(model, newRun.Start, Terminator, Sources);
      }

      public IReadOnlyList<AutocompleteItem> GetAutoCompleteOptions(string line, int caretLineIndex, int caretCharacterIndex) => null;

      public bool DependsOn(string anchorName) => false;

      public IReadOnlyList<IPixelViewModel> Visualizations => new List<IPixelViewModel>();
   }
}
