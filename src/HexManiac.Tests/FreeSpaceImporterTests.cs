using HavenSoft.HexManiac.Core.Models;
using HavenSoft.HexManiac.Core.Models.Runs.Sprites;
using HavenSoft.HexManiac.Core.ViewModels;
using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   /// <summary>File > Import (FreeSpaceImporter): into free space, or overwriting the bytes at a chosen address.</summary>
   public class FreeSpaceImporterTests : BaseViewModelTestClass {
      public FreeSpaceImporterTests() : base(0x1000) { }

      private readonly List<string> messages = new();

      // an 8x8 4bpp indexed image: pixel = (x + y) % 16, a 16-color palette
      private void SetupImage(bool compressed, string placement, string address = "", bool? confirm = true) {
         Array.Fill(Model.RawData, (byte)0xFF, 0x800, 0x800);
         var pixels = new int[8, 8];
         for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) pixels[x, y] = (x + y) % 16;
         var palette = Enumerable.Range(0, 16).Select(i => (short)(i * 0x421)).ToArray();
         FileSystem.TryLoadIndexImage = (ref string fileName, out int[,] image, out IReadOnlyList<short> pal) => {
            fileName = "test.png";
            (image, pal) = (pixels, palette);
            return true;
         };
         FileSystem.RequestText = (title, prompt) => title switch {
            "Overwrite" => address,
            "Free Space" => "",
            _ => "imported", // the name
         };
         FileSystem.ShowOptions = (title, prompt, details, options) => title switch {
            "Compression" => compressed ? 0 : 1,
            "Placement" => placement == "overwrite" ? 1 : 0,
            _ => -1,
         };
         FileSystem.ShowCustomMessageBox = (message, yesNo, links) => {
            messages.Add(message);
            if (message.StartsWith("Also import a matching palette")) return false;
            return confirm;
         };
      }

      [Fact]
      public void ImportSprite_FreeSpace_GoesToFreeSpace() {
         SetupImage(compressed: false, placement: "free");

         FreeSpaceImporter.ImportSprite(ViewPort, FileSystem);

         var address = Model.GetAddressFromAnchor(Token, -1, "imported");
         Assert.True(address >= 0x800);
         Assert.IsType<SpriteRun>(Model.GetNextRun(address));
      }

      [Theory]
      [InlineData(false)]
      [InlineData(true)]
      public void ImportSprite_OverwriteAddress_WritesThereOverExistingBytes(bool compressed) {
         SetupImage(compressed, placement: "overwrite", address: "100");
         for (int i = 0x100; i < 0x140; i++) Model.RawData[i] = 0x5A; // not free space

         FreeSpaceImporter.ImportSprite(ViewPort, FileSystem);

         Assert.Equal(0x100, Model.GetAddressFromAnchor(Token, -1, "imported"));
         var run = (ISpriteRun)Model.GetNextRun(0x100);
         Assert.Equal(0x100, run.Start);
         var pixels = run.GetPixels(Model, 0, -1);
         Assert.Equal(5, pixels[2, 3]);
         Assert.Equal(14, pixels[7, 7]); // (7 + 7) % 16
         Assert.All(Enumerable.Range(0x800, 0x800), i => Assert.Equal(0xFF, Model[i])); // its free-space copy was cleared
      }

      [Fact]
      public void ImportSprite_OverwriteBlank_UsesSelection() {
         SetupImage(compressed: false, placement: "overwrite", address: "");
         ViewPort.Goto.Execute(0x180);

         FreeSpaceImporter.ImportSprite(ViewPort, FileSystem);

         Assert.Equal(0x180, Model.GetAddressFromAnchor(Token, -1, "imported"));
      }

      [Fact]
      public void ImportSprite_OverwriteFormattedData_AsksAndClearsIt() {
         SetupImage(compressed: false, placement: "overwrite", address: "100");
         ViewPort.Edit("@110 ^existing.table[a:]4 @000 ");

         FreeSpaceImporter.ImportSprite(ViewPort, FileSystem);

         Assert.Contains(messages, m => m.Contains("will overwrite: existing.table"));
         Assert.Equal(0x100, Model.GetAddressFromAnchor(Token, -1, "imported"));
         Assert.Equal(Pointer.NULL, Model.GetAddressFromAnchor(Token, -1, "existing.table"));
      }

      [Fact]
      public void ImportSprite_OverwriteDeclined_StaysInFreeSpace() {
         SetupImage(compressed: false, placement: "overwrite", address: "100", confirm: false);
         ViewPort.Edit("@110 ^existing.table[a:]4 @000 ");

         FreeSpaceImporter.ImportSprite(ViewPort, FileSystem);

         Assert.True(Model.GetAddressFromAnchor(Token, -1, "imported") >= 0x800);
         Assert.Equal(0x110, Model.GetAddressFromAnchor(Token, -1, "existing.table"));
      }

      [Fact]
      public void ImportPalette_Overwrite_WritesColorsAtAddress() {
         SetupImage(compressed: false, placement: "overwrite", address: "0x200");

         FreeSpaceImporter.ImportPalette(ViewPort, FileSystem);

         var run = Assert.IsAssignableFrom<IPaletteRun>(Model.GetNextRun(0x200));
         Assert.Equal(0x200, run.Start);
         Assert.Equal((short)(3 * 0x421), run.GetPalette(Model, 0)[3]);
      }
   }
}
