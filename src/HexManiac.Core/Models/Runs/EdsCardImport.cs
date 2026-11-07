using HavenSoft.HexManiac.Core.Models.Code;
using HavenSoft.HexManiac.Core.Models.Images;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace HavenSoft.HexManiac.Core.Models.Runs {
   /// <summary>
   /// "Import from YGOProDeck" for Yu-Gi-Oh! The Eternal Duelist Soul (EDS) decomp builds, whose card tables
   /// differ from WCT06's (see CardImport): every per-card table is indexed by card ID, the stats are one packed
   /// word, names/descriptions are fixed ASCII slots, passwords are BCD in text order, and the art is 72x80 6bpp.
   /// The tables come from the decomp's tools/hma_toml.py; the conversion rules (stats word, extended type, Link
   /// Arrows, text clean-up, art crop, icons) are ports of its tools/newcards.py and tools/cardicons.py, so a card
   /// imported here matches one the decomp build adds. Card effects and Synchro/Xyz/Link summoning rules are code
   /// and compiled tables: those come from adding the card to the decomp (config/new_cards.txt) and rebuilding.
   /// Meant for the blank card slots `make release-demo-extra` / `make demo` leave for filling in.
   /// </summary>
   public static class EdsCardImport {
      public const string StatsName = "data.cards.stats";
      public const string NamesName = "data.cards.names";
      public const string DescriptionsName = "data.cards.descriptions";
      public const string PasswordsName = "data.cards.passwords";
      public const string NumbersName = "data.cards.numbers";
      public const string NameOrderName = "data.cards.nameorder";
      public const string TypeExName = "data.cards.typeex";
      public const string LinkMarkersName = "data.cards.linkmarkers";
      public const string ArtName = "graphics.cards.images";
      public const string ArtPalettesName = "graphics.cards.palettes";
      public const string IconsName = "graphics.cards.icons.images";
      public const string IconPalettesName = "graphics.cards.icons.palettes";
      public const string MiniaturesName = "graphics.cards.fieldicons.miniatures";
      public const string MiniaturePaletteName = "graphics.cards.fieldicons.artpalette";

      /// <summary>Longest name a Deck Edit list row shows (21 tiles of 2 characters; wiki game/added-cards.md).</summary>
      public const int MaxNameLength = 41;

      public readonly record struct Result(bool Success, string Message);

      private static bool Has(IDataModel model, string anchor) => model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, anchor) != Pointer.NULL;

      /// <summary>An EDS build's metadata: a tuple stats word plus the name/description slots (WCT06 has neither).</summary>
      public static bool CanImport(IDataModel model) =>
         Has(model, NamesName) && Has(model, DescriptionsName) && Has(model, PasswordsName) &&
         model.GetTable(StatsName) is ITableRun stats && stats.ElementContent.Count == 1 && stats.ElementContent[0] is ArrayRunTupleSegment;

      #region conversion (tools/newcards.py)

      private static readonly Dictionary<string, int> MonsterTypes = new() {
         ["Dragon"] = 1, ["Zombie"] = 2, ["Fiend"] = 3, ["Pyro"] = 4, ["Sea Serpent"] = 5, ["Rock"] = 6, ["Machine"] = 7, ["Fish"] = 8,
         ["Dinosaur"] = 9, ["Insect"] = 10, ["Beast"] = 11, ["Beast-Warrior"] = 12, ["Plant"] = 13, ["Aqua"] = 14, ["Warrior"] = 15,
         ["Winged Beast"] = 16, ["Fairy"] = 17, ["Spellcaster"] = 18, ["Thunder"] = 19, ["Reptile"] = 20,
      };
      private const int CardTypeTrap = 21, CardTypeMagic = 22;
      // races EDS lacks: (gCardTypeEx race, the closest EDS monster type the stats word keeps)
      private static readonly Dictionary<string, (int ex, string type)> ExtraRaces = new() {
         ["Psychic"] = (1, "Spellcaster"), ["Wyrm"] = (2, "Dragon"), ["Cyberse"] = (3, "Machine"), ["Illusion"] = (4, "Spellcaster"),
         ["Creator-God"] = (5, "Fairy"), ["Divine-Beast"] = (6, "Fairy"),
      };
      private static readonly Dictionary<string, int> Attributes = new() { ["LIGHT"] = 1, ["DARK"] = 2, ["WATER"] = 3, ["FIRE"] = 4, ["EARTH"] = 5, ["WIND"] = 6 };
      private static readonly Dictionary<string, int> Subtypes = new() { ["Normal"] = 0, ["Counter"] = 1, ["Field"] = 2, ["Equip"] = 3, ["Continuous"] = 4, ["Quick-Play"] = 5, ["Ritual"] = 6 };
      private static readonly Dictionary<string, string> SubtypeOverrides = new() { ["Blast with Chain"] = "Equip" };
      private static readonly Dictionary<string, int> LinkMarkerBits = new() {
         ["Top-Left"] = 0x01, ["Top"] = 0x02, ["Top-Right"] = 0x04, ["Left"] = 0x08, ["Right"] = 0x10, ["Bottom-Left"] = 0x20, ["Bottom"] = 0x40, ["Bottom-Right"] = 0x80,
      };
      private const int KindExSynchro = 1, KindExXyz = 2, KindExLink = 3, TypeExEffect = 0x04, TypeExTuner = 0x08;
      private static readonly (string from, string to)[] TextFixes = {
         ("●", ""), ("’", "'"), ("‘", "'"), ("“", "\""), ("”", "\""), ("–", "-"), ("—", "-"), ("•", ""), (" ", " "),
      };

      private static bool IsSpellOrTrap(YgoProDeckCard card) => card.Type == "Spell Card" || card.Type == "Trap Card";

      /// <summary>struct CardStats: def:9 atk:9 kind:2 type:5 level:4 attr:3; a Spell/Trap keeps its subtype in bits 17-19.</summary>
      public static bool TryGetStatsWord(YgoProDeckCard card, out uint word, out string error) {
         word = 0;
         error = null;
         if (IsSpellOrTrap(card)) {
            var subtypeName = SubtypeOverrides.TryGetValue(card.Name ?? string.Empty, out var over) ? over : card.Race;
            if (subtypeName == null || !Subtypes.TryGetValue(subtypeName, out var sub)) { error = $"{card.Race} {card.Type}s do not exist in EDS."; return false; }
            word = card.Type == "Spell Card"
               ? (uint)(CardTypeMagic << 20 | sub << 17)
               : (uint)(1 << 29 | CardTypeTrap << 20 | sub << 17);
            return true;
         }
         var type = card.Type ?? string.Empty;
         if (type == "Token") type = "Normal Monster"; // stats like the Insect Monster Token's: kind 0
         var race = card.Race != null && ExtraRaces.TryGetValue(card.Race, out var extra) ? extra.type : card.Race;
         if (race == null || !MonsterTypes.TryGetValue(race, out var monsterType) || card.Attribute == null || !Attributes.TryGetValue(card.Attribute, out var attribute)) {
            error = $"A {card.Race}/{card.Attribute} {card.Type} cannot be shown in EDS (no such monster type or attribute).";
            return false;
         }
         // an Xyz Monster's Rank (the API's level) and a Link Monster's Link Rating go in the Level field; a Link Monster has no DEF
         var isLink = type.Contains("Link");
         var level = isLink ? card.LinkVal ?? 0 : card.Level ?? 0;
         if (type.Contains("Pendulum") || level < 1 || level > 12) { error = $"{card.Type} cards do not exist in EDS."; return false; }
         var kind = new[] { "Fusion", "Synchro", "XYZ", "Link" }.Any(type.Contains) ? 2 : type.Contains("Ritual") ? 3 : type.StartsWith("Normal") ? 0 : 1;
         int atk = Math.Max(card.Atk ?? 0, 0), def = isLink ? 0 : Math.Max(card.Def ?? 0, 0);
         if (atk % 10 != 0 || def % 10 != 0 || atk > 5110 || def > 5110) { error = $"ATK/DEF {atk}/{def} cannot be stored (multiples of 10 up to 5110)."; return false; }
         word = (uint)attribute << 29 | (uint)level << 25 | (uint)monsterType << 20 | (uint)kind << 18 | (uint)(atk / 10) << 9 | (uint)(def / 10);
         return true;
      }

      /// <summary>gCardTypeEx: bits 0-1 Synchro/Xyz/Link, bit 2 has an effect, bit 3 Tuner, bits 4-7 a race EDS lacks.</summary>
      public static int GetTypeEx(YgoProDeckCard card) {
         var type = card.Type ?? string.Empty;
         if (IsSpellOrTrap(card) || type == "Token") return 0;
         var b = type.Contains("Synchro") ? KindExSynchro : type.Contains("XYZ") ? KindExXyz : type.Contains("Link") ? KindExLink : 0;
         if (type.Contains("Tuner")) b |= TypeExTuner;
         var extraDeck = new[] { "Synchro", "Fusion", "XYZ", "Link" }.Any(type.Contains);
         var descLines = (card.Desc ?? string.Empty).Trim().Split('\n').Length;
         if (type.Contains("Effect") || type == "Tuner Monster" || type == "Synchro Tuner Monster" || (extraDeck && descLines > 1)) b |= TypeExEffect;
         if (card.Race != null && ExtraRaces.TryGetValue(card.Race, out var extra)) b |= extra.ex << 4;
         return b;
      }

      public static int GetLinkMarkers(YgoProDeckCard card) =>
         (card.Type ?? string.Empty).Contains("Link") && card.LinkMarkers != null
            ? card.LinkMarkers.Sum(marker => LinkMarkerBits.TryGetValue(marker, out var bit) ? bit : 0)
            : 0;

      /// <summary>Printable ASCII on one line, cut to fit `size` bytes with its NUL (a description's ends in "...").</summary>
      public static string CleanText(string text, int size) {
         text ??= string.Empty;
         foreach (var (from, to) in TextFixes) text = text.Replace(from, to);
         text = Regex.Replace(text, @"\s+", " ").Trim();
         text = new string(text.Where(c => c >= ' ' && c <= '~').ToArray());
         if (text.Length >= size) {
            var cut = text.Substring(0, size - 4);
            var space = cut.LastIndexOf(' ');
            text = (space > 0 ? cut.Substring(0, space) : cut) + "...";
         }
         return text;
      }

      /// <summary>The password's 8 digits as BCD bytes in text order (EDS's FindCardByPassword); a Token has none.</summary>
      public static byte[] PasswordBytes(YgoProDeckCard card) {
         if (card.Type == "Token" || card.Id <= 0 || card.Id > 99999999) return new byte[] { 0xFF, 0xFF, 0xFF, 0xFF };
         var digits = card.Id.ToString("D8");
         return Enumerable.Range(0, 4).Select(i => (byte)((digits[i * 2] - '0') << 4 | (digits[i * 2 + 1] - '0'))).ToArray();
      }

      #endregion

      /// <param name="cardId">The data.cards.stats row: every per-card table of EDS is indexed by card ID.</param>
      /// <param name="gameName">The in-game name (at most MaxNameLength characters), or null for the card's own name.</param>
      public static Result Import(IDataModel model, ModelDelta token, int cardId, YgoProDeckCard card, string gameName = null) {
         var stats = model.GetTable(StatsName);
         var names = model.GetTable(NamesName);
         var descriptions = model.GetTable(DescriptionsName);
         var passwords = model.GetTable(PasswordsName);
         if (stats == null || names == null || descriptions == null || passwords == null) return new(false, "This ROM's EDS card tables were not found.");
         if (cardId < 1 || cardId >= stats.ElementCount) return new(false, "Card ID 0 is empty in EDS: pick a card's row.");
         if (!TryGetStatsWord(card, out var word, out var error)) return new(false, $"{card.Name}: {error}");

         var notes = new List<string>();
         var name = CleanText(gameName ?? card.Name, names.ElementLength);
         if (name.Length > MaxNameLength) {
            name = name.Substring(0, MaxNameLength).TrimEnd();
            notes.Add($"The name was cut to {MaxNameLength} characters to fit a Deck Edit row: \"{name}\".");
         }
         var passwordBytes = PasswordBytes(card);
         var duplicate = FindPassword(model, passwords, passwordBytes, cardId);
         if (duplicate > 0) notes.Add($"This ROM already has this card's password at card ID {duplicate}; entering the password finds that one.");

         model.WriteMultiByteValue(stats.Start + cardId * stats.ElementLength, 4, token, unchecked((int)word));
         WriteByte(model, token, TypeExName, cardId, GetTypeEx(card));
         WriteByte(model, token, LinkMarkersName, cardId, GetLinkMarkers(card));
         WriteText(model, token, names, cardId, name);
         WriteText(model, token, descriptions, cardId, CleanText(card.Desc, descriptions.ElementLength));
         for (int i = 0; i < 4; i++) token.ChangeData(model, passwords.Start + cardId * passwords.ElementLength + i, passwordBytes[i]);

         // the lists label every card field: by card ID, and (through data.cards.numbers) by card number
         CardImport.UpdateListEntry(model, token, "cardnumbers", cardId, name);
         var numbers = model.GetTable(NumbersName);
         if (numbers != null && cardId < numbers.ElementCount) {
            var number = model.ReadMultiByteValue(numbers.Start + cardId * numbers.ElementLength, numbers.ElementLength);
            CardImport.UpdateListEntry(model, token, "cardinternalnumbers", number, name);
         }

         if (!UpdateNameOrder(model, token, cardId, name, out var orderNote)) notes.Add(orderNote);
         else if (orderNote != null) notes.Add(orderNote);

         var type = card.Type ?? string.Empty;
         if (!IsSpellOrTrap(card) && type != "Token" && !type.StartsWith("Normal")) {
            notes.Add("Its effect (and any Synchro/Xyz/Link summoning rule) is code: add the card to the decomp's config/new_cards.txt and rebuild for it.");
         } else if (IsSpellOrTrap(card)) {
            notes.Add("Spell/Trap effects are code: add the card to the decomp's config/new_cards.txt and rebuild for its effect.");
         }
         if (type == "Token") notes.Add("A token also needs its token kind's card number (tools/newcards.py TOKEN_NUMBERS) to be summoned by its effect.");

         return new(true, $"Imported '{name}' as card ID {cardId}." + (notes.Count > 0 ? Environment.NewLine + Environment.NewLine + string.Join(Environment.NewLine, notes) : string.Empty));
      }

      private static void WriteByte(IDataModel model, ModelDelta token, string tableName, int row, int value) {
         var table = model.GetTable(tableName);
         if (table == null || row >= table.ElementCount) return;
         token.ChangeData(model, table.Start + row * table.ElementLength, (byte)value);
      }

      private static void WriteText(IDataModel model, ModelDelta token, ITableRun table, int row, string text) {
         var start = table.Start + row * table.ElementLength;
         for (int i = 0; i < table.ElementLength; i++) token.ChangeData(model, start + i, i < text.Length ? (byte)text[i] : (byte)0);
      }

      private static string ReadText(IDataModel model, ITableRun table, int row) {
         var start = table.Start + row * table.ElementLength;
         var chars = new List<char>();
         for (int i = 0; i < table.ElementLength && model[start + i] != 0; i++) chars.Add((char)model[start + i]);
         return new string(chars.ToArray());
      }

      private static int FindPassword(IDataModel model, ITableRun passwords, byte[] bytes, int exceptRow) {
         if (bytes.All(b => b == 0xFF)) return -1;
         for (int row = 1; row < passwords.ElementCount; row++) {
            if (row == exceptRow) continue;
            var start = passwords.Start + row * passwords.ElementLength;
            if (Enumerable.Range(0, 4).All(i => model[start + i] == bytes[i])) return row;
         }
         return -1;
      }

      /// <summary>The order of IDs 1..813 and the added cards (wiki data/card-name-table.md, tools/newcards.py name_key).</summary>
      public static string NameKey(string name) => (name ?? string.Empty).ToLowerInvariant().Replace("-", string.Empty);

      /// <summary>
      /// Puts the card into gCardIdsByName (data.cards.nameorder), the 0-terminated list Deck Edit, the Trunk and
      /// Card Trading walk, at its place in name order (a blank slot isn't in it until it's filled in). The list is
      /// the sorted cards, then IDs 814-820 out of order: the sorted part ends where the order first goes down. It
      /// grows by one entry when the card is new to it, moving it (and the code's pointers to it) if it has to.
      /// </summary>
      private static bool UpdateNameOrder(IDataModel model, ModelDelta token, int cardId, string name, out string note) {
         note = null;
         var names = model.GetTable(NamesName);
         if (model.GetTable(NameOrderName) is not TableStreamRun order) {
            note = $"{NameOrderName} was not found, so Deck Edit won't list the card.";
            return false;
         }
         var ids = Enumerable.Range(0, order.ElementCount).Select(i => model.ReadMultiByteValue(order.Start + i * 2, 2)).ToList();
         var key = NameKey(name);
         string KeyOf(int id) => id == cardId ? key : NameKey(ReadText(model, names, id));

         // the card's own entry is left out while finding the sorted part: a re-import may have changed its name
         var existing = ids.IndexOf(cardId);
         var grows = existing < 0;
         if (!grows) ids.RemoveAt(existing);
         var sortedEnd = Math.Min(1, ids.Count);
         while (sortedEnd < ids.Count && string.CompareOrdinal(KeyOf(ids[sortedEnd - 1]), KeyOf(ids[sortedEnd])) <= 0) sortedEnd++;
         if (existing >= sortedEnd) return true; // one of the appended IDs (814-820): it keeps its place
         var position = 0;
         while (position < sortedEnd) {
            var compare = string.CompareOrdinal(KeyOf(ids[position]), key);
            if (compare > 0 || (compare == 0 && ids[position] > cardId)) break;
            position++;
         }
         ids.Insert(position, cardId);

         var run = order;
         if (grows) {
            run = (TableStreamRun)order.Append(token, 1);
            model.ObserveRunWritten(token, run);
            if (run.Start != order.Start) note = $"{NameOrderName} grew, so it moved to {run.Start:X6} (the code's pointers to it were updated).";
         }
         for (int i = 0; i < ids.Count; i++) model.WriteMultiByteValue(run.Start + i * 2, 2, token, ids[i]);
         model.WriteMultiByteValue(run.Start + ids.Count * 2, 2, token, 0);
         return true;
      }

      #region graphics (tools/newcards.py to_art, tools/cardicons.py)

      private const int ArtWidth = 72, ArtHeight = 80, ArtColors = 64;
      private const int WindowWidth = 15, WindowHeight = 16, WindowCropHeight = 76; // the centre 72x76 (15:16) of the art
      private const int IconColors = 15, MiniaturePad = 0xFF;
      // a Synchro/Xyz/Link Monster's ring frame takes palette entries 1, 4, 10, 12, 15 (BGR555 5-bit channels);
      // its art keeps to the other ten
      private static readonly Dictionary<int, (int r, int g, int b)>[] RingFrames = {
         null,
         new() { [1] = (26, 26, 26), [4] = (28, 28, 29), [10] = (18, 18, 19), [12] = (4, 4, 5), [15] = (1, 0, 0) },  // Synchro: silver
         new() { [1] = (26, 26, 26), [4] = (6, 6, 7), [10] = (3, 3, 4), [12] = (1, 1, 2), [15] = (0, 0, 0) },        // Xyz: black
         new() { [1] = (26, 26, 26), [4] = (6, 12, 26), [10] = (3, 7, 18), [12] = (1, 2, 8), [15] = (0, 0, 0) },     // Link: blue
      };

      public static bool CanImportGraphics(IDataModel model) => Has(model, ArtName) && Has(model, ArtPalettesName);

      public static Result ImportGraphics(IDataModel model, ModelDelta token, IFileSystem fs, int cardId, byte[] imageBytes) {
         if (fs == null || imageBytes == null || !fs.TryDecodeImage(imageBytes, out var raw, out var width, out var height) || raw == null || width <= 0 || height <= 0) {
            return new(false, "Could not decode the downloaded card image.");
         }
         var art = ImageQuantizer.ToRgbGrid(raw, width, height);
         return ImportGraphics(model, token, cardId, art);
      }

      public static Result ImportGraphics(IDataModel model, ModelDelta token, int cardId, Rgb[,] image) {
         var artRun = model.GetNextRun(model.GetAddressFromAnchor(token, -1, ArtName)) as ISpriteRun;
         var paletteRun = model.GetNextRun(model.GetAddressFromAnchor(token, -1, ArtPalettesName)) as IPaletteRun;
         if (artRun == null || paletteRun == null) return new(false, "The card art tables were not found.");

         // centre-crop to the 9:10 frame, scale to 72x80, at BGR555 precision, then 64 colours
         var art = SnapToGba(ImageQuantizer.Resize(CropToAspect(image, ArtWidth, ArtHeight), ArtWidth, ArtHeight));
         var (indices, palette) = ImageQuantizer.MedianCutQuantize(art, ArtColors);
         artRun.SetPixels(model, token, cardId, indices);
         paletteRun.SetPalette(model, token, cardId, Pad(palette, ArtColors));

         // the 15x16 window art the icons share: the centre 72x76 of the art as it now is in the ROM
         var shown = ImageQuantizer.FromIndexed(indices, Pad(palette, ArtColors));
         var window = ImageQuantizer.Resize(Crop(shown, 0, (ArtHeight - WindowCropHeight) / 2, ArtWidth, WindowCropHeight), WindowWidth, WindowHeight);

         var typeEx = model.GetTable(TypeExName) is ITableRun typeExTable && cardId < typeExTable.ElementCount ? model[typeExTable.Start + cardId * typeExTable.ElementLength] & 3 : 0;
         var notes = new List<string>();
         if (!WriteRingIcon(model, token, cardId, window, typeEx)) notes.Add("Deck Edit's icon tables were not found, so its ring icon is unchanged.");
         if (!WriteMiniature(model, token, cardId, window)) notes.Add("The field miniature tables were not found, so its field icon is unchanged.");
         return new(true, "Imported the card art and icons." + (notes.Count > 0 ? " " + string.Join(" ", notes) : string.Empty));
      }

      /// <summary>Deck Edit's ring icon: 16x16 4bpp, its own 16 colours (0 transparent), the window art at (0, 0).</summary>
      private static bool WriteRingIcon(IDataModel model, ModelDelta token, int cardId, Rgb[,] window, int kindEx) {
         var iconRun = model.GetNextRun(model.GetAddressFromAnchor(token, -1, IconsName)) as ISpriteRun;
         var iconPaletteRun = model.GetNextRun(model.GetAddressFromAnchor(token, -1, IconPalettesName)) as IPaletteRun;
         if (iconRun == null || iconPaletteRun == null) return false;

         var frame = kindEx > 0 && kindEx < RingFrames.Length ? RingFrames[kindEx] : null;
         var artEntries = frame == null ? Enumerable.Range(1, IconColors).ToArray() : Enumerable.Range(1, IconColors).Where(i => !frame.ContainsKey(i)).ToArray();
         var (indices, palette) = ImageQuantizer.MedianCutQuantize(window, artEntries.Length);
         var colors = new short[16];
         if (frame != null) foreach (var (entry, (r, g, b)) in frame) colors[entry] = (short)(r << 10 | g << 5 | b);
         for (int i = 0; i < palette.Length && i < artEntries.Length; i++) colors[artEntries[i]] = palette[i];

         var pixels = new int[16, 16];
         for (int y = 0; y < WindowHeight; y++) {
            for (int x = 0; x < WindowWidth; x++) pixels[x, y] = artEntries[Math.Min(indices[x, y], artEntries.Length - 1)];
         }
         iconPaletteRun.SetPalette(model, token, cardId, colors);
         iconRun.SetPixels(model, token, cardId, pixels);
         return true;
      }

      /// <summary>The duel field's miniature: 16x16 8bpp on the shared 64-colour palette (0-63), 0xFF padding.</summary>
      private static bool WriteMiniature(IDataModel model, ModelDelta token, int cardId, Rgb[,] window) {
         var miniRun = model.GetNextRun(model.GetAddressFromAnchor(token, -1, MiniaturesName)) as ISpriteRun;
         var sharedRun = model.GetNextRun(model.GetAddressFromAnchor(token, -1, MiniaturePaletteName)) as IPaletteRun;
         if (miniRun == null || sharedRun == null) return false;

         var shared = sharedRun.AllColors(model);
         var candidates = Enumerable.Range(0, Math.Min(ArtColors, shared.Count)).Select(i => (i, shared[i])).ToList();
         var indices = ImageQuantizer.QuantizeToPalette(SnapToGba(window), candidates);
         var pixels = new int[16, 16];
         for (int y = 0; y < 16; y++) {
            for (int x = 0; x < 16; x++) pixels[x, y] = x < WindowWidth && y < WindowHeight ? indices[x, y] : MiniaturePad;
         }
         miniRun.SetPixels(model, token, cardId, pixels);
         return true;
      }

      private static Rgb[,] CropToAspect(Rgb[,] image, int aspectWidth, int aspectHeight) {
         int w = image.GetLength(0), h = image.GetLength(1);
         if (w * aspectHeight > h * aspectWidth) {           // too wide: crop the sides
            var cw = h * aspectWidth / aspectHeight;
            return Crop(image, (w - cw) / 2, 0, cw, h);
         }
         var ch = w * aspectHeight / aspectWidth;             // too tall: crop top and bottom
         return Crop(image, 0, (h - ch) / 2, w, ch);
      }

      private static Rgb[,] Crop(Rgb[,] image, int x0, int y0, int width, int height) {
         var result = new Rgb[width, height];
         for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) result[x, y] = image[x0 + x, y0 + y];
         }
         return result;
      }

      private static Rgb[,] SnapToGba(Rgb[,] image) {
         int w = image.GetLength(0), h = image.GetLength(1);
         var result = new Rgb[w, h];
         for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) result[x, y] = Rgb.FromGbaColor(image[x, y].ToGbaColor());
         }
         return result;
      }

      private static short[] Pad(IReadOnlyList<short> palette, int count) {
         var result = new short[count];
         for (int i = 0; i < Math.Min(count, palette.Count); i++) result[i] = palette[i];
         return result;
      }

      #endregion
   }
}
