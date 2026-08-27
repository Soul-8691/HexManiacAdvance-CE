using HavenSoft.HexManiac.Core.Models.Images;
using Xunit;

namespace HavenSoft.HexManiac.Tests {
   public class ImageQuantizerTests {
      [Fact]
      public void Rotate90Clockwise_2x2_MatchesExpectedMapping() {
         // A B      C A
         // C D  ->  D B
         var src = new Rgb[2, 2];
         src[0, 0] = new Rgb(1, 0, 0); // A
         src[1, 0] = new Rgb(2, 0, 0); // B
         src[0, 1] = new Rgb(3, 0, 0); // C
         src[1, 1] = new Rgb(4, 0, 0); // D

         var dest = ImageQuantizer.Rotate90Clockwise(src);

         Assert.Equal(3, dest[0, 0].R); // C
         Assert.Equal(1, dest[1, 0].R); // A
         Assert.Equal(4, dest[0, 1].R); // D
         Assert.Equal(2, dest[1, 1].R); // B
      }

      [Fact]
      public void Rotate90Clockwise_NonSquare_SwapsDimensions() {
         var src = new Rgb[3, 2]; // width 3, height 2
         var dest = ImageQuantizer.Rotate90Clockwise(src);
         Assert.Equal(2, dest.GetLength(0)); // new width == old height
         Assert.Equal(3, dest.GetLength(1)); // new height == old width
      }

      [Fact]
      public void Paste_OverwritesOnlyTargetRegion() {
         var canvas = new Rgb[4, 4];
         for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) canvas[x, y] = new Rgb(1, 1, 1);
         var sprite = new Rgb[2, 2];
         for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++) sprite[x, y] = new Rgb(9, 9, 9);

         ImageQuantizer.Paste(canvas, sprite, 1, 1);

         Assert.Equal(1, canvas[0, 0].R); // untouched corner
         Assert.Equal(9, canvas[1, 1].R); // pasted region
         Assert.Equal(9, canvas[2, 2].R); // pasted region
         Assert.Equal(1, canvas[3, 3].R); // untouched corner
      }

      [Fact]
      public void MedianCutQuantize_SolidColor_ProducesSingleColorPalette() {
         var pixels = new Rgb[8, 8];
         for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) pixels[x, y] = new Rgb(100, 150, 200);

         var (indices, palette) = ImageQuantizer.MedianCutQuantize(pixels, 64);

         Assert.Single(palette);
         for (int y = 0; y < 8; y++) for (int x = 0; x < 8; x++) Assert.Equal(0, indices[x, y]);
      }

      [Fact]
      public void MedianCutQuantize_TwoDistinctColors_ProducesAtMostTwoPaletteEntries() {
         var pixels = new Rgb[4, 4];
         for (int y = 0; y < 4; y++) {
            for (int x = 0; x < 4; x++) pixels[x, y] = x < 2 ? new Rgb(255, 0, 0) : new Rgb(0, 0, 255);
         }

         var (indices, palette) = ImageQuantizer.MedianCutQuantize(pixels, 64);

         Assert.True(palette.Length <= 2);
         // pixels on opposite sides of the split must map to different palette entries
         Assert.NotEqual(indices[0, 0], indices[3, 0]);
      }

      [Fact]
      public void QuantizeToPalette_OnlyEverReturnsGivenCandidateIndices() {
         var pixels = new Rgb[4, 4];
         for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) pixels[x, y] = new Rgb(x * 60, y * 60, 128);

         var candidates = new (int paletteIndex, short color)[] {
            (0, new Rgb(0, 0, 0).ToGbaColor()),
            (16, new Rgb(255, 255, 255).ToGbaColor()),
            (20, new Rgb(128, 128, 128).ToGbaColor()),
         };

         var indices = ImageQuantizer.QuantizeToPalette(pixels, candidates);

         for (int y = 0; y < 4; y++) {
            for (int x = 0; x < 4; x++) {
               var value = indices[x, y];
               Assert.True(value == 0 || value == 16 || value == 20);
            }
         }
      }

      [Fact]
      public void Resize_UpscalesUniformColor_StaysUniform() {
         var source = new Rgb[2, 2];
         for (int y = 0; y < 2; y++) for (int x = 0; x < 2; x++) source[x, y] = new Rgb(42, 84, 126);

         var resized = ImageQuantizer.Resize(source, 10, 10);

         Assert.Equal(10, resized.GetLength(0));
         Assert.Equal(10, resized.GetLength(1));
         for (int y = 0; y < 10; y++) {
            for (int x = 0; x < 10; x++) {
               Assert.Equal(42, resized[x, y].R);
               Assert.Equal(84, resized[x, y].G);
               Assert.Equal(126, resized[x, y].B);
            }
         }
      }

      [Fact]
      public void GbaColorRoundTrip_PreservesFiveBitChannels() {
         short original = (short)((17 << 10) | (9 << 5) | 3);
         var rgb = Rgb.FromGbaColor(original);
         var roundTripped = rgb.ToGbaColor();
         Assert.Equal(original, roundTripped);
      }
   }
}
