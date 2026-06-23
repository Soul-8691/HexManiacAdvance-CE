using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Http;

namespace HavenSoft.HexManiac.Core.Models.Code {
   /// <summary>
   /// Looks up pret/pokefirered RAM/ROM symbol names by absolute GBA address, so the Code Tool
   /// can label disassembly comments (branch/call targets, .word/ldr literal values) with
   /// names like "AddBattlerToSomeArray" or "gPlayerParty" instead of bare hex.
   ///
   /// Starts from a bundled snapshot (resources/pokefireredReference.txt, or a previously
   /// downloaded copy cached under %AppData%/HexManiacAdvance), so lookups always work
   /// offline. RequestBackgroundRefresh re-downloads the latest version in the background
   /// (best-effort: never blocks, never throws) and updates the cache for next time.
   /// </summary>
   public class PokefireredSymbols {
      public const string SymbolUrl = "https://raw.githubusercontent.com/pret/pokefirered/refs/heads/symbols/pokefirered.sym";
      public const int RomStart = 0x08000000;

      private static string CacheDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HexManiacAdvance");
      private static string CacheFile => Path.Combine(CacheDirectory, "pokefirered.sym");

      private readonly IWorkDispatcher dispatcher;
      private readonly string staticFallbackContent;
      private readonly object lockObject = new();
      private Dictionary<int, string> symbols;
      private int rodataStart = int.MaxValue; // everything at/after this address is data, not code
      private bool refreshRequested;

      public PokefireredSymbols(IWorkDispatcher dispatcher, string staticFallbackContent) {
         this.dispatcher = dispatcher ?? InstantDispatch.Instance;
         this.staticFallbackContent = staticFallbackContent ?? string.Empty;
      }

      /// <param name="address">An absolute GBA address: ROM addresses are PointerOffset-based
      /// (0x08000000+file offset), matching branch/call targets; RAM addresses (0x02xxxxxx
      /// EWRAM, 0x03xxxxxx IWRAM) are looked up as the raw literal value, matching what a
      /// .word/ldr constant actually contains.</param>
      public string TryGetSymbol(int address) {
         EnsureLoaded();
         lock (lockObject) return symbols.TryGetValue(address, out var name) ? name : null;
      }

      /// <summary>
      /// A .word/ldr literal used as a Thumb function pointer (for an indirect call) has bit0
      /// set per the ARM calling convention - the real instruction address is value-1. Only
      /// trusts that interpretation if the resulting address actually falls within the .text
      /// (code) region, i.e. before .rodata, so an arbitrary odd data value doesn't get
      /// mislabeled as a function.
      /// </summary>
      public string TryGetFunctionPointerSymbol(int value) {
         if ((value & 1) == 0) return null;
         var candidate = value - 1;
         EnsureLoaded();
         lock (lockObject) {
            if (candidate < RomStart || candidate >= rodataStart) return null;
            return symbols.TryGetValue(candidate, out var name) ? name : null;
         }
      }

      /// <summary>
      /// Kicks off a one-time, best-effort background download of the latest symbol file.
      /// Safe to call repeatedly (e.g. every time the Code Tool is opened) - only the first
      /// call in a session actually starts a fetch.
      /// </summary>
      public void RequestBackgroundRefresh() {
         lock (lockObject) {
            if (refreshRequested) return;
            refreshRequested = true;
         }
         dispatcher.RunBackgroundWork(() => {
            try {
               using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
               var text = client.GetStringAsync(SymbolUrl).Result;
               if (string.IsNullOrWhiteSpace(text)) return;
               var (parsedSymbols, parsedRodataStart) = Parse(text);
               lock (lockObject) {
                  symbols = parsedSymbols;
                  rodataStart = parsedRodataStart;
               }
               Directory.CreateDirectory(CacheDirectory);
               File.WriteAllText(CacheFile, text);
            } catch {
               // Best-effort only: keep using whatever was already loaded (cache or bundled snapshot).
            }
         });
      }

      private void EnsureLoaded() {
         lock (lockObject) {
            if (symbols != null) return;
            string text = null;
            try {
               if (File.Exists(CacheFile)) text = File.ReadAllText(CacheFile);
            } catch {
               // fall back to the bundled snapshot below
            }
            (symbols, rodataStart) = Parse(text ?? staticFallbackContent);
         }
      }

      /// <summary>
      /// Parses nm/objdump-style .sym lines: "AAAAAAAA g SSSSSSSS name" (address, scope, size, name).
      /// Also records the start of the .rodata section, which marks the end of real code/functions.
      /// </summary>
      public static (Dictionary<int, string> Symbols, int RodataStart) Parse(string text) {
         var result = new Dictionary<int, string>();
         var rodataStart = int.MaxValue;
         if (string.IsNullOrEmpty(text)) return (result, rodataStart);
         foreach (var line in text.Split('\n')) {
            var parts = line.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4) continue;
            if (!int.TryParse(parts[0], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var address)) continue;
            var name = parts[^1];
            if (name == ".rodata") rodataStart = Math.Min(rodataStart, address);
            if (string.IsNullOrEmpty(name) || name.StartsWith(".")) continue;
            result[address] = name;
         }
         return (result, rodataStart);
      }
   }
}
