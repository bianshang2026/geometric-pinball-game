#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using UnityEngine;

namespace TJGenerators.AssetSearch
{
    /// <summary>
    /// GIF 动画数据：全部帧及每帧的显示时长（秒）。
    /// </summary>
    internal sealed class GifAnimation
    {
        public readonly Texture2D[] Frames;
        public readonly float[]     Delays; // seconds per frame

        public GifAnimation(Texture2D[] frames, float[] delays)
        {
            Frames = frames;
            Delays = delays;
        }
    }

    /// <summary>
    /// 纯 C# GIF 解码器，支持多帧、透明、隔行扫描、GCE disposal method。
    /// </summary>
    internal static class GifDecoder
    {
        // ===== Public API =====

        /// <summary>解码所有帧，返回 GifAnimation；失败返回 null。</summary>
        public static GifAnimation DecodeAll(byte[] data)
        {
            if (data == null || data.Length < 13) return null;
            if (data[0] != 'G' || data[1] != 'I' || data[2] != 'F') return null;

            int pos = 6; // skip "GIFxx"

            // ── Logical Screen Descriptor ──────────────────────────────────────
            int  screenW   = ReadUInt16(data, pos); pos += 2;
            int  screenH   = ReadUInt16(data, pos); pos += 2;
            byte lsdPacked = data[pos++];
            int  bgIdx     = data[pos++];
            pos++; // pixel aspect ratio

            bool     hasGct = (lsdPacked & 0x80) != 0;
            int      gctSize = 1 << ((lsdPacked & 0x07) + 1);
            Color32[] gct    = null;
            if (hasGct)
            {
                gct = ReadColorTable(data, pos, gctSize);
                pos += gctSize * 3;
            }

            Color32 bgColor = hasGct && bgIdx < gct.Length
                ? gct[bgIdx]
                : new Color32(0, 0, 0, 0);

            // ── Compositing canvas (GIF coord: y=0 at top) ────────────────────
            var canvas = new Color32[screenW * screenH];
            for (int i = 0; i < canvas.Length; i++) canvas[i] = bgColor;

            var frames = new List<Texture2D>();
            var delays = new List<float>();

            // Per-frame state reset after each Image Descriptor
            int   transparentIndex = -1;
            int   disposalMethod   = 0;
            float frameDelay       = 0.1f;

            // ── Block loop ────────────────────────────────────────────────────
            while (pos < data.Length)
            {
                byte blockId = data[pos++];

                if (blockId == 0x3B) break; // Trailer

                // ── Extension ─────────────────────────────────────────────────
                if (blockId == 0x21)
                {
                    if (pos >= data.Length) break;
                    byte label = data[pos++];

                    if (label == 0xF9 && pos < data.Length) // Graphic Control Extension
                    {
                        int gceSize = data[pos++];
                        if (gceSize >= 4 && pos + gceSize <= data.Length)
                        {
                            byte flags     = data[pos];
                            disposalMethod   = (flags >> 2) & 0x07;
                            bool hasTransp   = (flags & 0x01) != 0;
                            int  delayCentis = data[pos + 1] | (data[pos + 2] << 8);
                            frameDelay       = delayCentis <= 0 ? 0.1f : delayCentis / 100f;
                            transparentIndex = hasTransp ? data[pos + 3] : -1;
                        }
                        pos = Math.Min(pos + gceSize, data.Length);
                        if (pos < data.Length && data[pos] == 0x00) pos++; // block terminator
                    }
                    else
                    {
                        SkipSubBlocks(data, ref pos);
                    }
                    continue;
                }

                // ── Image Descriptor ──────────────────────────────────────────
                if (blockId == 0x2C)
                {
                    if (pos + 9 > data.Length) break;

                    int  imgLeft   = ReadUInt16(data, pos); pos += 2;
                    int  imgTop    = ReadUInt16(data, pos); pos += 2;
                    int  imgW      = ReadUInt16(data, pos); pos += 2;
                    int  imgH      = ReadUInt16(data, pos); pos += 2;
                    byte imgPacked = data[pos++];

                    bool     hasLct    = (imgPacked & 0x80) != 0;
                    bool     interlaced = (imgPacked & 0x40) != 0;
                    int      lctSize   = 1 << ((imgPacked & 0x07) + 1);
                    Color32[] colorTable = gct;

                    if (hasLct)
                    {
                        colorTable = ReadColorTable(data, pos, lctSize);
                        pos += lctSize * 3;
                    }

                    if (pos >= data.Length) break;
                    byte   lzwMin  = data[pos++];
                    byte[] lzwData = ReadSubBlocks(data, ref pos);

                    if (imgW > 0 && imgH > 0 && colorTable != null)
                    {
                        int[] pixels = LzwDecode(lzwData, lzwMin, imgW * imgH);
                        if (pixels != null)
                        {
                            // Save canvas before draw (disposal method 3)
                            Color32[] prevCanvas = disposalMethod == 3
                                ? (Color32[])canvas.Clone()
                                : null;

                            // Blit this frame's pixels onto the canvas
                            BlitFrame(canvas, screenW, screenH, imgLeft, imgTop,
                                      imgW, imgH, pixels, colorTable, transparentIndex, interlaced);

                            // Capture the composited canvas as a Texture2D (flip Y for Unity)
                            frames.Add(CanvasToTexture(canvas, screenW, screenH));
                            delays.Add(frameDelay);

                            // Apply disposal to prepare canvas for next frame
                            switch (disposalMethod)
                            {
                                case 2: // Restore region to background color
                                    for (int y = imgTop; y < imgTop + imgH && y < screenH; y++)
                                        for (int x = imgLeft; x < imgLeft + imgW && x < screenW; x++)
                                            canvas[y * screenW + x] = bgColor;
                                    break;
                                case 3: // Restore to pre-draw canvas
                                    if (prevCanvas != null) canvas = prevCanvas;
                                    break;
                                // 0, 1: keep canvas as-is
                            }
                        }
                    }

                    // Reset per-frame state
                    transparentIndex = -1;
                    disposalMethod   = 0;
                    frameDelay       = 0.1f;
                }
            }

            return frames.Count > 0
                ? new GifAnimation(frames.ToArray(), delays.ToArray())
                : null;
        }

        // ===== Canvas helpers =====

        private static void BlitFrame(
            Color32[] canvas, int screenW, int screenH,
            int imgLeft, int imgTop, int imgW, int imgH,
            int[] pixels, Color32[] colorTable, int transparentIndex, bool interlaced)
        {
            if (interlaced)
            {
                int[] offsets = { 0, 4, 2, 1 };
                int[] jumps   = { 8, 8, 4, 2 };
                int px = 0;
                for (int pass = 0; pass < 4; pass++)
                    for (int y = offsets[pass]; y < imgH; y += jumps[pass])
                        for (int x = 0; x < imgW && px < pixels.Length; x++, px++)
                            BlitPixel(canvas, screenW, screenH, imgLeft + x, imgTop + y,
                                      pixels[px], colorTable, transparentIndex);
            }
            else
            {
                for (int y = 0; y < imgH; y++)
                    for (int x = 0; x < imgW; x++)
                    {
                        int src = y * imgW + x;
                        BlitPixel(canvas, screenW, screenH, imgLeft + x, imgTop + y,
                                  src < pixels.Length ? pixels[src] : 0, colorTable, transparentIndex);
                    }
            }
        }

        private static void BlitPixel(
            Color32[] canvas, int screenW, int screenH,
            int cx, int cy, int colorIdx, Color32[] colorTable, int transparentIndex)
        {
            if (colorIdx == transparentIndex) return;
            if ((uint)cx >= (uint)screenW || (uint)cy >= (uint)screenH) return;
            canvas[cy * screenW + cx] = colorIdx < colorTable.Length
                ? colorTable[colorIdx]
                : new Color32(0, 0, 0, 255);
        }

        private static Texture2D CanvasToTexture(Color32[] canvas, int w, int h)
        {
            var tex    = new Texture2D(w, h, TextureFormat.RGBA32, false);
            var colors = new Color32[w * h];
            for (int y = 0; y < h; y++)
            {
                int unityY = h - 1 - y;
                for (int x = 0; x < w; x++)
                    colors[unityY * w + x] = canvas[y * w + x];
            }
            tex.SetPixels32(colors);
            tex.Apply();
            return tex;
        }

        // ===== Low-level GIF parsers =====

        private static int ReadUInt16(byte[] data, int pos)
            => data[pos] | (data[pos + 1] << 8);

        private static Color32[] ReadColorTable(byte[] data, int pos, int count)
        {
            var table = new Color32[count];
            for (int i = 0; i < count && pos + 2 < data.Length; i++, pos += 3)
                table[i] = new Color32(data[pos], data[pos + 1], data[pos + 2], 255);
            return table;
        }

        private static byte[] ReadSubBlocks(byte[] data, ref int pos)
        {
            var result = new List<byte>(1024);
            while (pos < data.Length)
            {
                int size = data[pos++];
                if (size == 0) break;
                int end = Math.Min(pos + size, data.Length);
                while (pos < end) result.Add(data[pos++]);
            }
            return result.ToArray();
        }

        private static void SkipSubBlocks(byte[] data, ref int pos)
        {
            while (pos < data.Length)
            {
                int size = data[pos++];
                if (size == 0) break;
                pos = Math.Min(pos + size, data.Length);
            }
        }

        // ===== LZW Decoder =====

        private static int[] LzwDecode(byte[] data, int minCodeSize, int pixelCount)
        {
            if (minCodeSize < 2 || minCodeSize > 8) return null;

            int clearCode = 1 << minCodeSize;
            int eoiCode   = clearCode + 1;
            int codeSize  = minCodeSize + 1;
            int codeMask  = (1 << codeSize) - 1;

            var table = new List<int[]>(4096);
            for (int i = 0; i < clearCode; i++) table.Add(new[] { i });
            table.Add(null); // clearCode slot
            table.Add(null); // eoiCode slot

            var output = new List<int>(pixelCount + 64);
            int bitBuf = 0, bitCnt = 0, dataPos = 0;
            int[] prev = null;

            while (true)
            {
                while (bitCnt < codeSize && dataPos < data.Length)
                {
                    bitBuf |= data[dataPos++] << bitCnt;
                    bitCnt += 8;
                }
                if (bitCnt < codeSize) break;

                int code = bitBuf & codeMask;
                bitBuf >>= codeSize;
                bitCnt  -= codeSize;

                if (code == clearCode)
                {
                    table.Clear();
                    for (int i = 0; i < clearCode; i++) table.Add(new[] { i });
                    table.Add(null);
                    table.Add(null);
                    codeSize = minCodeSize + 1;
                    codeMask = (1 << codeSize) - 1;
                    prev = null;
                    continue;
                }

                if (code == eoiCode) break;

                int[] entry;
                if (code < table.Count && table[code] != null)
                    entry = table[code];
                else if (code == table.Count && prev != null)
                {
                    // K+1 special case: sequence not yet in table
                    entry = new int[prev.Length + 1];
                    Array.Copy(prev, entry, prev.Length);
                    entry[prev.Length] = prev[0];
                }
                else
                    break; // corrupt stream

                foreach (int p in entry) output.Add(p);
                if (output.Count >= pixelCount) break;

                if (prev != null && table.Count < 4096)
                {
                    var ne = new int[prev.Length + 1];
                    Array.Copy(prev, ne, prev.Length);
                    ne[prev.Length] = entry[0];
                    table.Add(ne);
                    if (table.Count >= (1 << codeSize) && codeSize < 12)
                    {
                        codeSize++;
                        codeMask = (1 << codeSize) - 1;
                    }
                }
                prev = entry;
            }

            return output.ToArray();
        }
    }
}
#endif
