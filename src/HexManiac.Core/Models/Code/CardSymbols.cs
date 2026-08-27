using System.Collections.Generic;

namespace HavenSoft.HexManiac.Core.Models.Code {
   /// <summary>
   /// Looks up value->name for C-style `#define NAME VALUE` macros (decimal, not hex - unlike
   /// ProjectLabels' "Name: 0xAAAAAAAA" ini format) - built from header files such as a
   /// project's include/card.h, so raw numeric literals in the Thumb view can optionally be
   /// shown as their symbolic Konami card ID name instead. Purely static/local - built once from
   /// whatever *.h files are bundled in resources/, no network fetch involved.
   /// </summary>
   public class CardSymbols {
      private readonly Dictionary<int, string> symbols = new();

      public CardSymbols(IEnumerable<string> fileContents = null) {
         if (fileContents == null) return;
         foreach (var text in fileContents) Add(text);
      }

      private void Add(string text) {
         if (string.IsNullOrEmpty(text)) return;
         foreach (var line in text.Split('\n')) {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("#define ")) continue;
            var tokens = trimmed.Substring("#define ".Length).Split(new[] { ' ', '\t' }, System.StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length != 2) continue;
            if (!int.TryParse(tokens[1], out var value)) continue;
            symbols[value] = tokens[0];
         }
      }

      public string TryGetSymbol(int value) => symbols.TryGetValue(value, out var name) ? name : null;
   }
}
