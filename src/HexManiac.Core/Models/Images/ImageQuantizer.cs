using System;
using System.Collections.Generic;
using System.Linq;

namespace HavenSoft.HexManiac.Core.Models.Images {
   /// <summary>
   /// From-scratch image resize/quantize helpers for importing truecolor art (card art, card
   /// icons) into the GBA's indexed-color sprite formats. No third-party imaging dependency:
   /// bilinear resize plus a median-cut quantizer, both operating on 8-bit RGB before the final
   /// snap down to the ROM's 5-bit-per-channel palette.
   /// </summary>
   public static class ImageQuantizer {
      public static Rgb[,] ToRgbGrid(short[] flatGbaColors, int width, int height) {
         var grid = new Rgb[width, height];
         for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++) grid[x, y] = Rgb.FromGbaColor(flatGbaColors[y * width + x]);
         }
         return grid;
      }

      public static Rgb[,] FromIndexed(int[,] pixels, IReadOnlyList<short> palette) {
         int w = pixels.GetLength(0), h = pixels.GetLength(1);
         var grid = new Rgb[w, h];
         for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) grid[x, y] = Rgb.FromGbaColor(palette[pixels[x, y]]);
         }
         return grid;
      }

      /// <summary>
      /// Area/box-filter resize: every destination pixel is the weighted average of every source
      /// pixel its footprint overlaps (weighted by how much it overlaps). Plain bilinear (4-tap,
      /// nearest-neighbor-ish sampling) is correct for upscaling, but every use here is a large
      /// downscale (an 80x80+ photo shrinking down to sprite/icon size) - bilinear would only
      /// sample a 2x2 neighborhood per output pixel and alias away most of the source detail
      /// instead of averaging it in, which is what was producing the "garbled" look.
      /// </summary>
      public static Rgb[,] Resize(Rgb[,] source, int destWidth, int destHeight) {
         int srcW = source.GetLength(0), srcH = source.GetLength(1);
         var dest = new Rgb[destWidth, destHeight];
         double scaleX = (double)srcW / destWidth, scaleY = (double)srcH / destHeight;

         for (int dy = 0; dy < destHeight; dy++) {
            double srcY0 = dy * scaleY, srcY1 = (dy + 1) * scaleY;
            int iy0 = Clamp((int)Math.Floor(srcY0), 0, srcH - 1);
            int iy1 = Clamp((int)Math.Ceiling(srcY1) - 1, 0, srcH - 1);
            for (int dx = 0; dx < destWidth; dx++) {
               double srcX0 = dx * scaleX, srcX1 = (dx + 1) * scaleX;
               int ix0 = Clamp((int)Math.Floor(srcX0), 0, srcW - 1);
               int ix1 = Clamp((int)Math.Ceiling(srcX1) - 1, 0, srcW - 1);

               double sumR = 0, sumG = 0, sumB = 0, sumWeight = 0;
               for (int sy = iy0; sy <= iy1; sy++) {
                  var weightY = OverlapWeight(sy, srcY0, srcY1);
                  for (int sx = ix0; sx <= ix1; sx++) {
                     var weight = OverlapWeight(sx, srcX0, srcX1) * weightY;
                     var c = source[sx, sy];
                     sumR += c.R * weight; sumG += c.G * weight; sumB += c.B * weight;
                     sumWeight += weight;
                  }
               }

               if (sumWeight <= 0) { dest[dx, dy] = source[ix0, iy0]; continue; }
               dest[dx, dy] = new Rgb(
                  Clamp((int)Math.Round(sumR / sumWeight), 0, 255),
                  Clamp((int)Math.Round(sumG / sumWeight), 0, 255),
                  Clamp((int)Math.Round(sumB / sumWeight), 0, 255));
            }
         }
         return dest;
      }

      /// <summary>How much of source pixel [pixelIndex, pixelIndex+1) overlaps [rangeStart, rangeEnd).</summary>
      private static double OverlapWeight(int pixelIndex, double rangeStart, double rangeEnd) {
         var overlapStart = Math.Max(pixelIndex, rangeStart);
         var overlapEnd = Math.Min(pixelIndex + 1, rangeEnd);
         return Math.Max(0, overlapEnd - overlapStart);
      }

      public static void Paste(Rgb[,] canvas, Rgb[,] sprite, int offsetX, int offsetY) {
         int w = sprite.GetLength(0), h = sprite.GetLength(1);
         int cw = canvas.GetLength(0), ch = canvas.GetLength(1);
         for (int y = 0; y < h; y++) {
            var cy = offsetY + y;
            if (cy < 0 || cy >= ch) continue;
            for (int x = 0; x < w; x++) {
               var cx = offsetX + x;
               if (cx < 0 || cx >= cw) continue;
               canvas[cx, cy] = sprite[x, y];
            }
         }
      }

      /// <summary>Rotates a grid 90 degrees clockwise (matches PIL's rotate(-90)).</summary>
      public static Rgb[,] Rotate90Clockwise(Rgb[,] source) {
         int w = source.GetLength(0), h = source.GetLength(1);
         var dest = new Rgb[h, w];
         for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) dest[h - 1 - y, x] = source[x, y];
         }
         return dest;
      }

      /// <summary>
      /// Median-cut quantization to at most colorCount colors. Returns per-pixel palette
      /// indices plus the palette itself (as ROM-ready 15-bit GBA colors).
      /// </summary>
      public static (int[,] indices, short[] palette) MedianCutQuantize(Rgb[,] pixels, int colorCount) {
         int w = pixels.GetLength(0), h = pixels.GetLength(1);
         var flat = new List<Rgb>(w * h);
         for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) flat.Add(pixels[x, y]);
         }

         // Weight split priority by population*range (not just raw range): a pure range-based
         // split over-serves rare/background colors, while pure population-based splitting
         // (classic Heckbert) starves small-but-visually-important accent colors. This hybrid
         // favors boxes that are both populous and varied.
         var boxes = new List<List<Rgb>> { flat };
         while (boxes.Count < colorCount) {
            int splitIndex = -1, bestChannel = 0;
            long bestScore = -1;
            for (int i = 0; i < boxes.Count; i++) {
               if (boxes[i].Count <= 1) continue;
               var (channel, range) = WidestChannel(boxes[i]);
               if (range <= 0) continue; // box is a single solid color - nothing to gain by splitting
               long score = (long)boxes[i].Count * range;
               if (score > bestScore) (bestScore, splitIndex, bestChannel) = (score, i, channel);
            }
            if (splitIndex == -1) break;

            var toSplit = boxes[splitIndex];
            toSplit.Sort((a, b) => Channel(a, bestChannel).CompareTo(Channel(b, bestChannel)));
            int mid = toSplit.Count / 2;
            boxes[splitIndex] = toSplit.GetRange(0, mid);
            boxes.Add(toSplit.GetRange(mid, toSplit.Count - mid));
         }

         var palette = boxes.Select(Average).ToArray();
         palette = RefineWithKMeans(pixels, palette, iterations: 8);
         var indices = DitherToPalette(pixels, palette);
         return (indices, palette.Select(c => c.ToGbaColor()).ToArray());
      }

      /// <summary>
      /// Median-cut's box-average palette is a reasonable starting point but not a locally
      /// optimal one. Lloyd's algorithm (assign every pixel to its nearest current color,
      /// recompute each color as the mean of what it was assigned, repeat) tightens the
      /// palette around the image's actual color clusters instead of the cut boundaries.
      /// </summary>
      private static Rgb[] RefineWithKMeans(Rgb[,] pixels, Rgb[] palette, int iterations) {
         int w = pixels.GetLength(0), h = pixels.GetLength(1);
         var current = (Rgb[])palette.Clone();
         for (int iter = 0; iter < iterations; iter++) {
            var sumR = new long[current.Length];
            var sumG = new long[current.Length];
            var sumB = new long[current.Length];
            var count = new int[current.Length];
            for (int y = 0; y < h; y++) {
               for (int x = 0; x < w; x++) {
                  var color = pixels[x, y];
                  var index = NearestIndex(color, current);
                  sumR[index] += color.R; sumG[index] += color.G; sumB[index] += color.B;
                  count[index]++;
               }
            }
            var changed = false;
            for (int i = 0; i < current.Length; i++) {
               if (count[i] == 0) continue; // no pixels claimed this color this round - leave it where it was
               var updated = new Rgb((int)(sumR[i] / count[i]), (int)(sumG[i] / count[i]), (int)(sumB[i] / count[i]));
               if (updated.R != current[i].R || updated.G != current[i].G || updated.B != current[i].B) changed = true;
               current[i] = updated;
            }
            if (!changed) break;
         }
         return current;
      }

      /// <summary>
      /// Floyd-Steinberg error-diffusion dithering: at only 64 colors, a photographic image
      /// quantized via plain nearest-color lookup bands heavily and looks flat/posterized.
      /// Diffusing each pixel's quantization error into its not-yet-processed neighbors trades
      /// that banding for a fine dither pattern that reads as smooth gradients at sprite scale.
      /// </summary>
      private static int[,] DitherToPalette(Rgb[,] pixels, Rgb[] palette) {
         int w = pixels.GetLength(0), h = pixels.GetLength(1);
         var indices = new int[w, h];
         var errR = new double[w, h];
         var errG = new double[w, h];
         var errB = new double[w, h];

         for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
               var source = pixels[x, y];
               var adjusted = new Rgb(
                  Clamp((int)Math.Round(source.R + errR[x, y]), 0, 255),
                  Clamp((int)Math.Round(source.G + errG[x, y]), 0, 255),
                  Clamp((int)Math.Round(source.B + errB[x, y]), 0, 255));

               var index = NearestIndex(adjusted, palette);
               indices[x, y] = index;
               var chosen = palette[index];
               double dr = adjusted.R - chosen.R, dg = adjusted.G - chosen.G, db = adjusted.B - chosen.B;

               void Spread(int nx, int ny, double weight) {
                  if (nx < 0 || nx >= w || ny < 0 || ny >= h) return;
                  errR[nx, ny] += dr * weight; errG[nx, ny] += dg * weight; errB[nx, ny] += db * weight;
               }
               Spread(x + 1, y, 7.0 / 16);
               Spread(x - 1, y + 1, 3.0 / 16);
               Spread(x, y + 1, 5.0 / 16);
               Spread(x + 1, y + 1, 1.0 / 16);
            }
         }
         return indices;
      }

      /// <summary>
      /// Maps every pixel to the nearest color among a fixed, given set of (romPaletteIndex,
      /// color) candidates - for importing into a shared palette that must not be modified
      /// (e.g. the card icon palette), rather than generating a brand-new one.
      /// </summary>
      public static int[,] QuantizeToPalette(Rgb[,] pixels, IReadOnlyList<(int paletteIndex, short color)> candidates) {
         int w = pixels.GetLength(0), h = pixels.GetLength(1);
         var candidateColors = candidates.Select(c => Rgb.FromGbaColor(c.color)).ToArray();
         var indices = new int[w, h];
         for (int y = 0; y < h; y++) {
            for (int x = 0; x < w; x++) {
               var color = pixels[x, y];
               int best = 0, bestDist = int.MaxValue;
               for (int i = 0; i < candidateColors.Length; i++) {
                  var dist = color.DistanceSquaredTo(candidateColors[i]);
                  if (dist < bestDist) (bestDist, best) = (dist, i);
               }
               indices[x, y] = candidates[best].paletteIndex;
            }
         }
         return indices;
      }

      private static (int channel, int range) WidestChannel(List<Rgb> box) {
         int minR = 255, maxR = 0, minG = 255, maxG = 0, minB = 255, maxB = 0;
         foreach (var c in box) {
            if (c.R < minR) minR = c.R; if (c.R > maxR) maxR = c.R;
            if (c.G < minG) minG = c.G; if (c.G > maxG) maxG = c.G;
            if (c.B < minB) minB = c.B; if (c.B > maxB) maxB = c.B;
         }
         var (rangeR, rangeG, rangeB) = (maxR - minR, maxG - minG, maxB - minB);
         if (rangeR >= rangeG && rangeR >= rangeB) return (0, rangeR);
         if (rangeG >= rangeB) return (1, rangeG);
         return (2, rangeB);
      }

      private static int Channel(Rgb c, int channel) => channel == 0 ? c.R : channel == 1 ? c.G : c.B;

      private static Rgb Average(List<Rgb> box) {
         long r = 0, g = 0, b = 0;
         foreach (var c in box) { r += c.R; g += c.G; b += c.B; }
         int count = Math.Max(1, box.Count);
         return new Rgb((int)(r / count), (int)(g / count), (int)(b / count));
      }

      private static int NearestIndex(Rgb color, Rgb[] palette) {
         int best = 0, bestDist = int.MaxValue;
         for (int i = 0; i < palette.Length; i++) {
            var dist = color.DistanceSquaredTo(palette[i]);
            if (dist < bestDist) (bestDist, best) = (dist, i);
         }
         return best;
      }

      private static int Clamp(int v, int min, int max) => v < min ? min : v > max ? max : v;
   }
}
