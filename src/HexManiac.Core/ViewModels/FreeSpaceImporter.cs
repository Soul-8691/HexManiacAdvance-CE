using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Linq;

namespace HavenSoft.HexManiac.Core.ViewModels {
   /// <summary>
   /// Imports a sprite/tileset/tilemap/palette from an indexed PNG into a brand-new anchor at
   /// free ROM space, rather than replacing an existing anchor (which is all SpriteTool.TryImport
   /// supports). Reuses the existing per-run SetPixels/SetPalette growth logic (decompress/modify/
   /// recompress/relocate) by writing an all-0xFF placeholder into freshly-found free space first,
   /// then immediately overwriting it with the real imported data through the normal run API.
   ///
   /// Each new run can instead be placed at an address the user picks, writing over whatever bytes are
   /// there (e.g. to replace graphics the game already points to): the run is still built in free space
   /// first, so its final size is known, then its exact bytes move to that address (see Place).
   /// </summary>
   public static class FreeSpaceImporter {
      public static void ImportSprite(ViewPort viewPort, IFileSystem fs) {
         if (!TryPickIndexedImage(fs, out var image, out var palette)) return;
         if (!TryGetTileDimensions(fs, image, out var tileWidth, out var tileHeight)) return;
         var bits = DeriveBitsPerPixel(palette);
         if (!TryReadName(fs, viewPort.Model, "sprite", out var name)) return;
         var compressed = AskCompressed(fs);
         if (!TryPickPlacement(viewPort, fs, name, out var placement)) return;
         var paletteHint = OfferMatchingPalette(viewPort, fs, name, palette, bits, compressed);

         var format = new SpriteFormat(bits, tileWidth, tileHeight, paletteHint);
         Func<IDataModel, int, ISpriteRun> construct = (model, start) => compressed ? new LzSpriteRun(format, model, start) : new SpriteRun(model, start, format);
         var run = CreateAndRegister(viewPort, name, format.ExpectedByteLength, compressed, construct, out var romLength);
         if (run == null) return;
         run.SetPixels(viewPort.Model, viewPort.CurrentChange, 0, image);
         Place(viewPort, fs, name, placement, construct, romLength);
         viewPort.Goto.Execute(name);
      }

      public static void ImportTileset(ViewPort viewPort, IFileSystem fs) {
         if (!TryPickIndexedImage(fs, out var image, out var palette)) return;
         if (!TryGetTileDimensions(fs, image, out var tileWidth, out var tileHeight)) return;
         var (tileset, name) = CreateTilesetFromImage(viewPort, fs, image, palette, tileWidth, tileHeight, "tileset");
         if (tileset == null) return;
         viewPort.Goto.Execute(name);
      }

      /// <summary>
      /// A tilemap is meaningless without a backing tileset (it only stores tile indices). Rather
      /// than asking for a second image, the tileset is built automatically: the single imported
      /// image's 8x8 blocks are deduplicated into a unique-tiles image, which becomes the tileset;
      /// the original image is then matched against it tile-for-tile to produce the tilemap (every
      /// block has an exact match by construction, since the tileset was built from those exact blocks).
      /// </summary>
      public static void ImportTilemap(ViewPort viewPort, IFileSystem fs) {
         if (!TryPickIndexedImage(fs, out var mapImage, out var palette)) return;
         if (!TryGetTileDimensions(fs, mapImage, out var mapTileWidth, out var mapTileHeight)) return;

         var tilesetImage = BuildDeduplicatedTileset(mapImage, mapTileWidth, mapTileHeight, out var uniqueTileCount);
         var (tileset, tilesetName) = CreateTilesetFromImage(viewPort, fs, tilesetImage, palette, uniqueTileCount, 1, "tileset (auto-built from this image's unique tiles)");
         if (tileset == null) return;

         if (!TryReadName(fs, viewPort.Model, "tilemap", out var name)) return;
         var compressed = AskCompressed(fs);
         if (!TryPickPlacement(viewPort, fs, name, out var placement)) return;

         var format = new TilemapFormat(tileset.SpriteFormat.BitsPerPixel, mapTileWidth, mapTileHeight, tilesetName);
         Func<IDataModel, int, ISpriteRun> construct = (model, start) => compressed ? new LzTilemapRun(format, model, start) : new TilemapRun(model, start, format);
         var run = CreateAndRegister(viewPort, name, format.ExpectedUncompressedLength, compressed, construct, out var romLength);
         if (run == null) return;
         run.SetPixels(viewPort.Model, viewPort.CurrentChange, 0, mapImage);
         Place(viewPort, fs, name, placement, construct, romLength);
         viewPort.Goto.Execute(name);
      }

      /// <summary>
      /// Slices a pixel-index image into 8x8 tiles and deduplicates identical tiles, returning a
      /// new image that's just those unique tiles laid out in a single row (uniqueTileCount tiles
      /// wide, 1 tile tall) - i.e. exactly what a from-scratch tileset for this image should contain.
      /// </summary>
      private static int[,] BuildDeduplicatedTileset(int[,] mapImage, int tileWidth, int tileHeight, out int uniqueTileCount) {
         var uniqueTiles = new List<int[,]>();
         for (int ty = 0; ty < tileHeight; ty++) {
            for (int tx = 0; tx < tileWidth; tx++) {
               var block = new int[8, 8];
               for (int x = 0; x < 8; x++) for (int y = 0; y < 8; y++) block[x, y] = mapImage[tx * 8 + x, ty * 8 + y];
               if (!uniqueTiles.Exists(existing => TilesMatch(existing, block))) uniqueTiles.Add(block);
            }
         }

         uniqueTileCount = uniqueTiles.Count;
         var tilesetImage = new int[uniqueTileCount * 8, 8];
         for (int i = 0; i < uniqueTiles.Count; i++) {
            for (int x = 0; x < 8; x++) for (int y = 0; y < 8; y++) tilesetImage[i * 8 + x, y] = uniqueTiles[i][x, y];
         }
         return tilesetImage;
      }

      private static bool TilesMatch(int[,] a, int[,] b) {
         for (int x = 0; x < 8; x++) for (int y = 0; y < 8; y++) if (a[x, y] != b[x, y]) return false;
         return true;
      }

      /// <summary>
      /// Shared by ImportTileset and ImportTilemap: asks for a name/compression/placement/optional
      /// matching palette, then creates+populates a new tileset run from an already-loaded indexed image.
      /// Returns (null, null) if the user cancels at any step.
      /// </summary>
      private static (ISpriteRun run, string name) CreateTilesetFromImage(ViewPort viewPort, IFileSystem fs, int[,] image, IReadOnlyList<short> palette, int tileWidth, int tileHeight, string kind) {
         var bits = DeriveBitsPerPixel(palette);
         var tiles = tileWidth * tileHeight;
         if (!TryReadName(fs, viewPort.Model, kind, out var name)) return (null, null);
         var compressed = AskCompressed(fs);
         if (!TryPickPlacement(viewPort, fs, name, out var placement)) return (null, null);
         var paletteHint = OfferMatchingPalette(viewPort, fs, name, palette, bits, compressed);

         var format = new TilesetFormat(bits, tiles, -1, paletteHint);
         Func<IDataModel, int, ISpriteRun> construct = (model, start) => compressed ? new LzTilesetRun(format, model, start) : new TilesetRun(format, model, start);
         var run = CreateAndRegister(viewPort, name, tiles * bits * 8, compressed, construct, out var romLength);
         if (run == null) return (null, null);
         run.SetPixels(viewPort.Model, viewPort.CurrentChange, 0, image);
         Place(viewPort, fs, name, placement, construct, romLength);
         run = viewPort.Model.GetNextRun(viewPort.Model.GetAddressFromAnchor(viewPort.CurrentChange, -1, name)) as ISpriteRun ?? run;
         return (run, name);
      }

      public static void ImportPalette(ViewPort viewPort, IFileSystem fs) {
         if (!TryPickIndexedImage(fs, out var _, out var palette)) return;
         var bits = DeriveBitsPerPixel(palette);
         if (!TryReadName(fs, viewPort.Model, "palette", out var name)) return;
         var compressed = AskCompressed(fs);
         if (!TryPickPlacement(viewPort, fs, name, out var placement)) return;
         CreatePalette(viewPort, fs, name, palette, bits, compressed, placement);
         viewPort.Goto.Execute(name);
      }

      // ---- shared helpers ----

      private static bool TryPickIndexedImage(IFileSystem fs, out int[,] image, out IReadOnlyList<short> palette) {
         string fileName = null;
         if (!fs.TryLoadIndexedImage(ref fileName, out image, out palette)) {
            if (fileName != null) {
               fs.ShowCustomMessageBox(
                  $"'{fileName}' isn't an indexed image (16 or 256 colors). Free-space import requires an indexed PNG.",
                  showYesNoCancel: false);
            }
            return false;
         }
         return true;
      }

      private static bool TryGetTileDimensions(IFileSystem fs, int[,] image, out int tileWidth, out int tileHeight) {
         var (pixelWidth, pixelHeight) = (image.GetLength(0), image.GetLength(1));
         tileWidth = pixelWidth / 8;
         tileHeight = pixelHeight / 8;
         if (pixelWidth % 8 == 0 && pixelHeight % 8 == 0) return true;
         fs.ShowCustomMessageBox($"Image is {pixelWidth}x{pixelHeight}, but width and height must both be multiples of 8.", showYesNoCancel: false);
         return false;
      }

      private static int DeriveBitsPerPixel(IReadOnlyList<short> palette) => palette.Count <= 16 ? 4 : 8;

      private static bool TryReadName(IFileSystem fs, IDataModel model, string kind, out string name) {
         name = fs.RequestText($"Name the new {kind}", $"Enter a new anchor name for the {kind}:");
         if (string.IsNullOrWhiteSpace(name)) { name = null; return false; }
         name = name.Trim();
         if (model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, name) != Pointer.NULL) {
            fs.ShowCustomMessageBox($"An anchor named '{name}' already exists. Choose a different name.", showYesNoCancel: false);
            name = null;
            return false;
         }
         return true;
      }

      /// <summary>Where a new run goes: found free space (searching from SearchStart), or written over the bytes at OverwriteAddress.</summary>
      public readonly record struct Placement(int SearchStart, int OverwriteAddress) {
         public bool Overwrite => OverwriteAddress >= 0;
      }

      private static bool TryPickPlacement(ViewPort viewPort, IFileSystem fs, string name, out Placement placement) {
         placement = new Placement(0, Pointer.NULL);
         var choice = fs.ShowOptions("Placement", $"Where should '{name}' go?", null,
            new VisualOption { Index = 0, Option = "Free space", Description = "Search for unused (0xFF) bytes, so nothing else is overwritten." },
            new VisualOption { Index = 1, Option = "Overwrite at an address", Description = "Write over the bytes at an address you choose (for example, to replace graphics the game already uses). Anything formatted there is cleared." });
         if (choice == 1) {
            var selected = viewPort.ConvertViewPointToAddress(viewPort.SelectionStart);
            var text = fs.RequestText("Overwrite", $"Enter the hex address to write '{name}' at, or leave blank for the current selection ({selected:X6}):");
            if (text == null) return false;
            var address = string.IsNullOrWhiteSpace(text) ? selected : ParseHex(text);
            if (address < 0 || address >= viewPort.Model.Count) {
               fs.ShowCustomMessageBox($"'{text}' isn't an address in this ROM.", showYesNoCancel: false);
               return false;
            }
            placement = new Placement(0, address);
            return true;
         }
         if (choice != 0) return false;
         var start = fs.RequestText("Free Space", "Enter a hex address to search from, or leave blank to auto-search from the ROM's configured free-space start:");
         placement = new Placement(string.IsNullOrWhiteSpace(start) ? 0 : Math.Max(0, ParseHex(start)), Pointer.NULL);
         return true;
      }

      private static int ParseHex(string text) {
         text = text.Trim();
         if (text.StartsWith("<") && text.EndsWith(">")) text = text.Substring(1, text.Length - 2);
         if (text.StartsWith("0x") || text.StartsWith("0X")) text = text.Substring(2);
         return int.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var address) ? address : -1;
      }

      private static bool AskCompressed(IFileSystem fs) {
         var choice = fs.ShowOptions("Compression", "How should this be stored in the ROM?", null,
            new VisualOption { Index = 0, Option = "Compressed (LZ)", Description = "Smaller in the ROM. Recommended for most imports." },
            new VisualOption { Index = 1, Option = "Uncompressed", Description = "Larger, but simpler/faster to read." });
         return choice == 0;
      }

      private static string OfferMatchingPalette(ViewPort viewPort, IFileSystem fs, string primaryName, IReadOnlyList<short> colors, int bits, bool compressed) {
         var answer = fs.ShowCustomMessageBox($"Also import a matching palette for '{primaryName}'?");
         if (answer != true) return null;
         var paletteName = primaryName + ".palette";
         if (viewPort.Model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, paletteName) != Pointer.NULL) {
            fs.ShowCustomMessageBox($"An anchor named '{paletteName}' already exists, so the matching palette was skipped.", showYesNoCancel: false);
            return null;
         }
         if (!TryPickPlacement(viewPort, fs, paletteName, out var placement)) return null;
         CreatePalette(viewPort, fs, paletteName, colors, bits, compressed, placement);
         return paletteName;
      }

      private static void CreatePalette(ViewPort viewPort, IFileSystem fs, string name, IReadOnlyList<short> colors, int bits, bool compressed, Placement placement) {
         var format = new PaletteFormat(bits, 1);
         Func<IDataModel, int, IPaletteRun> construct = (model, start) => compressed ? new LzPaletteRun(format, model, start) : new PaletteRun(start, format);
         var run = CreateAndRegister(viewPort, name, format.ExpectedByteLengthPerPage, compressed, construct, out var romLength);
         if (run == null) return;
         run.SetPalette(viewPort.Model, viewPort.CurrentChange, 0, colors);
         Place(viewPort, fs, name, placement, construct, romLength);
      }


      /// <summary>Where every import is built before it's placed: past any real ROM data (32 MB, the largest GBA ROM).</summary>
      public const int BuildArea = 0x2000000;

      /// <summary>
      /// Writes an all-0xFF placeholder of the right (raw or compressed) size at BuildArea (or just past the data,
      /// if the ROM already reaches it), with as much 0xFF after it as the finished run could need, constructs the
      /// real run there, and registers it as a named anchor. No free-space search is involved, so building never
      /// fails for lack of room and has no length limit. The caller is expected to immediately call
      /// SetPixels/SetPalette on the result to fill in the real data (which settles the run into its final size in
      /// place), then Place, which moves it into the ROM and trims the build area off again.
      /// </summary>
      /// <param name="romLength">The ROM's length before the build area was added, for Place.</param>
      private static T CreateAndRegister<T>(ViewPort viewPort, string name, int rawLength, bool compressed, Func<IDataModel, int, T> construct, out int romLength) where T : IFormattedRun {
         var model = viewPort.Model;
         var token = viewPort.CurrentChange;
         romLength = model.Count;

         byte[] placeholder;
         if (compressed) {
            var raw = new byte[rawLength];
            for (int i = 0; i < raw.Length; i++) raw[i] = 0xFF;
            var compressedBytes = LZRun.Compress(raw, 0, raw.Length);
            placeholder = new byte[compressedBytes.Count];
            for (int i = 0; i < placeholder.Length; i++) placeholder[i] = compressedBytes[i];
         } else {
            placeholder = new byte[rawLength];
            for (int i = 0; i < placeholder.Length; i++) placeholder[i] = 0xFF;
         }

         // room for the finished run to grow into in place: LZ data is at most its raw length + a flag byte per 8 + its header
         var start = Align4(Math.Max(BuildArea, romLength));
         var room = Math.Max(placeholder.Length, rawLength + rawLength / 8 + 0x20) + 0x40;
         model.ExpandData(token, start + room);

         for (int i = 0; i < placeholder.Length; i++) token.ChangeData(model, start + i, placeholder[i]);

         var run = construct(model, start);
         model.ObserveAnchorWritten(token, name, run);
         return run;
      }

      /// <summary>
      /// Moves the finished run named `name` from the build area into the ROM, then trims the build area off:
      /// - overwrite placement: at the chosen address, clearing whatever was formatted over its bytes first (after a
      ///   confirmation listing those anchors; declining places it in free space instead);
      /// - free space: the first 4-byte-aligned spot from the search start where exactly its bytes are free
      ///   (0xFF, no runs), or else appended to the end of the ROM.
      /// The ROM grows only if the run ends past its old end (up to BuildArea). Returns false if it can't be placed
      /// at all, in which case the import is removed.
      /// </summary>
      private static bool Place<T>(ViewPort viewPort, IFileSystem fs, string name, Placement placement, Func<IDataModel, int, T> construct, int romLength) where T : IFormattedRun {
         var model = viewPort.Model;
         var token = viewPort.CurrentChange;
         var current = model.GetNextRun(model.GetAddressFromAnchor(token, -1, name));
         var (from, length) = (current.Start, current.Length);
         var bytes = new byte[length];
         Array.Copy(model.RawData, from, bytes, 0, length);

         var target = Pointer.NULL;
         var overwrite = placement.Overwrite;
         if (overwrite) {
            target = placement.OverwriteAddress;
            if (target + length > BuildArea) {
               fs.ShowCustomMessageBox($"'{name}' is {length} bytes, which doesn't fit at {target:X6} (a GBA ROM can't be bigger than {BuildArea:X6}). It goes in free space instead.", showYesNoCancel: false);
               overwrite = false;
            } else if (!ConfirmOverwrite(model, fs, name, target, length, Math.Min(romLength, target + length), from)) {
               fs.ShowCustomMessageBox($"'{name}' goes in free space instead.", showYesNoCancel: false);
               overwrite = false;
            }
         }

         // the building copy goes first: the build area is about to be trimmed
         model.ClearFormatAndData(token, from, length);
         if (!overwrite) {
            var start = placement.SearchStart > 0 ? placement.SearchStart : model.FreeSpaceStart > 0 ? model.FreeSpaceStart : 0x100;
            target = FindExactFreeSpace(model, start, length, romLength);
            if (target < 0) target = Align4(romLength); // no room in the ROM: add it to the end
         }
         if (target + length > BuildArea) {
            model.ContractData(token, romLength - 1);
            fs.ShowCustomMessageBox($"There's no room for '{name}' ({length} bytes): the ROM has no free space that big, and adding it to the end would make the ROM bigger than {BuildArea:X6}.", showYesNoCancel: false);
            return false;
         }

         if (overwrite) model.ClearFormat(token, target, Math.Max(0, Math.Min(length, romLength - target)));
         var finalLength = Math.Max(romLength, target + length);
         model.ContractData(token, finalLength - 1); // (an alignment gap before an appended run is the build area's 0xFF)
         for (int i = 0; i < length; i++) token.ChangeData(model, target + i, bytes[i]);
         model.ObserveAnchorWritten(token, name, construct(model, target));
         if (finalLength > romLength) viewPort.RaiseMessage($"'{name}' was added to the end of the ROM at {target:X6}; the ROM grew to {finalLength:X6} bytes.");
         return true;
      }

      /// <summary>True if nothing formatted is in [target, end), or the user agrees to overwrite what is.</summary>
      private static bool ConfirmOverwrite(IDataModel model, IFileSystem fs, string name, int target, int length, int end, int buildingCopy) {
         var overwritten = new List<string>();
         for (var run = model.GetNextRun(target); run.Start < end; run = model.GetNextRun(run.Start + Math.Max(1, run.Length))) {
            if (run.Start + run.Length <= target || run.Start == buildingCopy) continue;
            var anchor = model.GetAnchorFromAddress(-1, run.Start);
            overwritten.Add(string.IsNullOrEmpty(anchor) ? $"{run.Start:X6}" : anchor);
         }
         if (overwritten.Count == 0) return true;
         var list = string.Join(", ", overwritten.Take(8)) + (overwritten.Count > 8 ? $", and {overwritten.Count - 8} more" : string.Empty);
         return fs.ShowCustomMessageBox($"Writing '{name}' ({length} bytes) at {target:X6} will overwrite: {list}. Continue?") == true;
      }

      private static int Align4(int address) => (address + 3) & ~3;

      /// <summary>The first 4-byte-aligned address from `start` where `length` bytes before `limit` are all 0xFF and no run overlaps them, or -1.</summary>
      public static int FindExactFreeSpace(IDataModel model, int start, int length, int limit = int.MaxValue) {
         limit = Math.Min(limit, model.Count);
         var address = Align4(Math.Max(0, start));
         while (address + length <= limit) {
            var run = model.GetNextRun(address);
            if (run.Start < address + length && run.Start + Math.Max(1, run.Length) > address) {
               address = Align4(Math.Max(address + 1, run.Start + Math.Max(1, run.Length))); // past that run
               continue;
            }
            var conflict = -1;
            for (int i = length - 1; i >= 0; i--) {
               if (model[address + i] != 0xFF) { conflict = address + i; break; }
            }
            if (conflict < 0) return address;
            address = Align4(conflict + 1); // past the last non-free byte
         }
         return -1;
      }
   }
}
