using System;
using System.Collections.Generic;
using System.Globalization;

namespace HavenSoft.HexManiac.Core.Models.Code {
   /// <summary>
   /// Looks up address->label names from this project's own assembled-code label listings
   /// (e.g. expansion-patch symbol dumps like HUBOL_CFRU.ini/HUBOL_DPE.ini: one
   /// "LabelName:    0xAAAAAAAA" pair per line). Purely static/local - built once from
   /// whatever *.ini files are bundled in resources/, no network fetch involved.
   /// </summary>
   public class ProjectLabels {
      private readonly Dictionary<int, string> labels = new();

      public ProjectLabels(IEnumerable<string> fileContents = null) {
         if (fileContents == null) return;
         foreach (var text in fileContents) Add(text);
      }

      private void Add(string text) {
         if (string.IsNullOrEmpty(text)) return;
         foreach (var line in text.Split('\n')) {
            var trimmed = line.Trim();
            var colonIndex = trimmed.IndexOf(':');
            if (colonIndex < 1) continue;
            var name = trimmed.Substring(0, colonIndex).Trim();
            var addressText = trimmed.Substring(colonIndex + 1).Trim();
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(addressText)) continue;
            if (!int.TryParse(addressText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)) continue;
            labels[address] = name;
         }
      }

      public string TryGetSymbol(int address) => labels.TryGetValue(address, out var name) ? name : null;

      /// <summary>
      /// Same Thumb-bit (+1) convention as PokefireredSymbols.TryGetFunctionPointerSymbol:
      /// a stored function-pointer value has bit0 set, so the real label is at value-1.
      /// </summary>
      public string TryGetFunctionPointerSymbol(int value) {
         if ((value & 1) == 0) return null;
         return TryGetSymbol(value - 1);
      }
   }
}
