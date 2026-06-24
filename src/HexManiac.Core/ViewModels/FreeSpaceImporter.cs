using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;

namespace HavenSoft.HexManiac.Core.ViewModels {
   /// <summary>
   /// Imports a sprite/tileset/tilemap/palette from an indexed PNG into a brand-new anchor at
   /// free ROM space, rather than replacing an existing anchor (which is all SpriteTool.TryImport
   /// supports). Reuses the existing per-run SetPixels/SetPalette growth logic (decompress/modify/
   /// recompress/relocate) by writing an all-0xFF placeholder into freshly-found free space first,
   /// then immediately overwriting it with the real imported data through the normal run API.
   /// </summary>
   public static class FreeSpaceImporter {
      public static void ImportSprite(ViewPort viewPort, IFileSystem fs) {
         if (!TryPickIndexedImage(fs, out var image, out var palette)) return;
         if (!TryGetTileDimensions(fs, image, out var tileWidth, out var tileHeight)) return;
         var bits = DeriveBitsPerPixel(palette);
         if (!TryReadName(fs, viewPort.Model, "sprite", out var name)) return;
         var compressed = AskCompressed(fs);
         var searchStart = PickFreeSpaceStart(fs);
         var paletteHint = OfferMatchingPalette(viewPort, fs, name, palette, bits, compressed, searchStart);

         var format = new SpriteFormat(bits, tileWidth, tileHeight, paletteHint);
         var run = CreateAndRegister<ISpriteRun>(viewPort, name, searchStart, format.ExpectedByteLength, compressed,
            (model, start) => compressed ? new LzSpriteRun(format, model, start) : new SpriteRun(model, start, format));
         if (run == null) return;
         run.SetPixels(viewPort.Model, viewPort.CurrentChange, 0, image);
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
         var searchStart = PickFreeSpaceStart(fs);

         var format = new TilemapFormat(tileset.SpriteFormat.BitsPerPixel, mapTileWidth, mapTileHeight, tilesetName);
         var run = CreateAndRegister<ISpriteRun>(viewPort, name, searchStart, format.ExpectedUncompressedLength, compressed,
            (model, start) => compressed ? new LzTilemapRun(format, model, start) : new TilemapRun(model, start, format));
         if (run == null) return;
         run.SetPixels(viewPort.Model, viewPort.CurrentChange, 0, mapImage);
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
      /// Shared by ImportTileset and ImportTilemap: asks for a name/compression/optional matching
      /// palette, then creates+populates a new tileset run from an already-loaded indexed image.
      /// Returns (null, null) if the user cancels at any step.
      /// </summary>
      private static (ISpriteRun run, string name) CreateTilesetFromImage(ViewPort viewPort, IFileSystem fs, int[,] image, IReadOnlyList<short> palette, int tileWidth, int tileHeight, string kind) {
         var bits = DeriveBitsPerPixel(palette);
         var tiles = tileWidth * tileHeight;
         if (!TryReadName(fs, viewPort.Model, kind, out var name)) return (null, null);
         var compressed = AskCompressed(fs);
         var searchStart = PickFreeSpaceStart(fs);
         var paletteHint = OfferMatchingPalette(viewPort, fs, name, palette, bits, compressed, searchStart);

         var format = new TilesetFormat(bits, tiles, -1, paletteHint);
         var run = CreateAndRegister<ISpriteRun>(viewPort, name, searchStart, tiles * bits * 8, compressed,
            (model, start) => compressed ? new LzTilesetRun(format, model, start) : new TilesetRun(format, model, start));
         if (run == null) return (null, null);
         run.SetPixels(viewPort.Model, viewPort.CurrentChange, 0, image);
         return (run, name);
      }

      public static void ImportPalette(ViewPort viewPort, IFileSystem fs) {
         if (!TryPickIndexedImage(fs, out var _, out var palette)) return;
         var bits = DeriveBitsPerPixel(palette);
         if (!TryReadName(fs, viewPort.Model, "palette", out var name)) return;
         var compressed = AskCompressed(fs);
         var searchStart = PickFreeSpaceStart(fs);
         CreatePalette(viewPort, fs, name, palette, bits, compressed, searchStart);
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

      private static int PickFreeSpaceStart(IFileSystem fs) {
         var text = fs.RequestText("Free Space", "Enter a hex address to search from, or leave blank to auto-search from the ROM's configured free-space start:");
         if (string.IsNullOrWhiteSpace(text)) return 0;
         text = text.Trim();
         if (text.StartsWith("0x") || text.StartsWith("0X")) text = text.Substring(2);
         return int.TryParse(text, System.Globalization.NumberStyles.HexNumber, System.Globalization.CultureInfo.InvariantCulture, out var address) ? address : 0;
      }

      private static bool AskCompressed(IFileSystem fs) {
         var choice = fs.ShowOptions("Compression", "How should this be stored in the ROM?", null,
            new VisualOption { Index = 0, Option = "Compressed (LZ)", Description = "Smaller in the ROM. Recommended for most imports." },
            new VisualOption { Index = 1, Option = "Uncompressed", Description = "Larger, but simpler/faster to read." });
         return choice == 0;
      }

      private static string OfferMatchingPalette(ViewPort viewPort, IFileSystem fs, string primaryName, IReadOnlyList<short> colors, int bits, bool compressed, int searchStart) {
         var answer = fs.ShowCustomMessageBox($"Also import a matching palette for '{primaryName}'?");
         if (answer != true) return null;
         var paletteName = primaryName + ".palette";
         if (viewPort.Model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, paletteName) != Pointer.NULL) {
            fs.ShowCustomMessageBox($"An anchor named '{paletteName}' already exists, so the matching palette was skipped.", showYesNoCancel: false);
            return null;
         }
         CreatePalette(viewPort, fs, paletteName, colors, bits, compressed, searchStart);
         return paletteName;
      }

      private static void CreatePalette(ViewPort viewPort, IFileSystem fs, string name, IReadOnlyList<short> colors, int bits, bool compressed, int searchStart) {
         var format = new PaletteFormat(bits, 1);
         var run = CreateAndRegister<IPaletteRun>(viewPort, name, searchStart, format.ExpectedByteLengthPerPage, compressed,
            (model, start) => compressed ? new LzPaletteRun(format, model, start) : new PaletteRun(start, format));
         run?.SetPalette(viewPort.Model, viewPort.CurrentChange, 0, colors);
      }

      /// <summary>
      /// Writes an all-0xFF placeholder of the right (raw or compressed) size into freshly-found
      /// free space, constructs the real run there, and registers it as a named anchor. The
      /// caller is expected to immediately call SetPixels/SetPalette on the result to fill in the
      /// real data - that's what settles the run into its final size via the run's own existing
      /// relocate-on-grow logic.
      /// </summary>
      private static T CreateAndRegister<T>(ViewPort viewPort, string name, int searchStart, int rawLength, bool compressed, Func<IDataModel, int, T> construct) where T : IFormattedRun {
         var model = viewPort.Model;
         var token = viewPort.CurrentChange;

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

         var start = model.FindFreeSpace(searchStart, placeholder.Length);
         if (start < 0) {
            viewPort.RaiseError($"Couldn't find {placeholder.Length} bytes of free space.");
            return default;
         }

         for (int i = 0; i < placeholder.Length; i++) token.ChangeData(model, start + i, placeholder[i]);

         var run = construct(model, start);
         model.ObserveAnchorWritten(token, name, run);
         return run;
      }
   }
}
