using System;
using System.Collections.Generic;

namespace HavenSoft.HexManiac.Core.Models.Code {
   /// <summary>
   /// A user-selectable nm/objdump-style .sym file (same "AAAAAAAA g SSSSSSSS name" format as
   /// pokefirered.sym) bundled in resources/, e.g. a decomp's eds_demo.sym. Unlike
   /// PokefireredSymbols (auto-applied to BPRE0), these are only used when explicitly chosen
   /// in the Code Tool, since nothing in a ROM header reliably says which build a .sym matches.
   /// </summary>
   public class SymbolFile {
      private readonly Func<string> loadText;
      private readonly object lockObject = new();
      private Dictionary<int, string> symbols;
      private int rodataStart = int.MaxValue;

      public string Name { get; }

      public SymbolFile(string name, Func<string> loadText) {
         Name = name;
         this.loadText = loadText ?? (() => string.Empty);
      }

      public SymbolFile(string name, string content) : this(name, () => content) { }

      public string TryGetSymbol(int address) {
         EnsureLoaded();
         lock (lockObject) return symbols.TryGetValue(address, out var name) ? name : null;
      }

      /// <summary>Same Thumb-bit convention as PokefireredSymbols.TryGetFunctionPointerSymbol.</summary>
      public string TryGetFunctionPointerSymbol(int value) {
         if ((value & 1) == 0) return null;
         var candidate = value - 1;
         EnsureLoaded();
         lock (lockObject) {
            if (candidate < PokefireredSymbols.RomStart || candidate >= rodataStart) return null;
            return symbols.TryGetValue(candidate, out var name) ? name : null;
         }
      }

      private void EnsureLoaded() {
         lock (lockObject) {
            if (symbols != null) return;
            string text;
            try { text = loadText(); } catch { text = string.Empty; }
            (symbols, rodataStart) = PokefireredSymbols.Parse(text);
         }
      }

      public override string ToString() => Name;
   }
}
