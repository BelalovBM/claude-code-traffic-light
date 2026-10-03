using System;
using System.Collections.Generic;
using System.Drawing;
using System.Text;

namespace Semaphore
{
    // Minimal QR Code encoder: byte mode, error correction level M, versions 1-6
    // (up to 106 bytes), which is plenty for a subscription link.
    static class Qr
    {
        // Level M tables for versions 1..6: EC codewords per block, block count, data codewords per block.
        static readonly int[] EcPerBlock = { 0, 10, 16, 26, 18, 24, 16 };
        static readonly int[] Blocks = { 0, 1, 1, 1, 2, 2, 4 };
        static readonly int[] DataPerBlock = { 0, 16, 28, 44, 32, 43, 27 };
        static readonly int[][] Align =
        {
            null, new int[0], new[] { 6, 18 }, new[] { 6, 22 }, new[] { 6, 26 }, new[] { 6, 30 }, new[] { 6, 34 },
        };

        public static Bitmap Render(string text, int scale)
        {
            bool[,] m = Encode(text);
            if (m == null) return null;
            int n = m.GetLength(0);
            int quiet = 4;
            var bmp = new Bitmap((n + quiet * 2) * scale, (n + quiet * 2) * scale);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.White);
                for (int y = 0; y < n; y++)
                    for (int x = 0; x < n; x++)
                        if (m[y, x])
                            g.FillRectangle(Brushes.Black, (x + quiet) * scale, (y + quiet) * scale, scale, scale);
            }
            return bmp;
        }

        public static bool[,] Encode(string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            int version = 0;
            for (int v = 1; v <= 6; v++)
                if (data.Length <= (Blocks[v] * DataPerBlock[v] * 8 - 12) / 8) { version = v; break; }
            if (version == 0) return null;

            int totalData = Blocks[version] * DataPerBlock[version];
            byte[] codewords = BuildCodewords(data, version, totalData);
            int size = version * 4 + 17;

            var modules = new bool[size, size];
            var isFunc = new bool[size, size];
            DrawFunctionPatterns(modules, isFunc, version);
            PlaceData(modules, isFunc, codewords);

            int bestMask = 0;
            int bestScore = int.MaxValue;
            for (int mask = 0; mask < 8; mask++)
            {
                ApplyMask(modules, isFunc, mask);
                DrawFormat(modules, isFunc, mask);
                int score = Penalty(modules);
                if (score < bestScore) { bestScore = score; bestMask = mask; }
                ApplyMask(modules, isFunc, mask); // masks are self-inverse
            }
            ApplyMask(modules, isFunc, bestMask);
            DrawFormat(modules, isFunc, bestMask);
            return modules;
        }

        static byte[] BuildCodewords(byte[] data, int version, int totalData)
        {
            var bits = new List<bool>();
            Append(bits, 4, 4);
            Append(bits, data.Length, 8);
            foreach (byte b in data) Append(bits, b, 8);

            int capacity = totalData * 8;
            Append(bits, 0, Math.Min(4, capacity - bits.Count));
            while (bits.Count % 8 != 0) bits.Add(false);
            for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11)
                Append(bits, pad, 8);

            var dataBytes = new byte[totalData];
            for (int i = 0; i < bits.Count; i++)
                if (bits[i]) dataBytes[i >> 3] |= (byte)(0x80 >> (i & 7));

            int blocks = Blocks[version], per = DataPerBlock[version], ec = EcPerBlock[version];
            byte[] divisor = Divisor(ec);
            var dataBlocks = new byte[blocks][];
            var ecBlocks = new byte[blocks][];
            for (int b = 0; b < blocks; b++)
            {
                dataBlocks[b] = new byte[per];
                Array.Copy(dataBytes, b * per, dataBlocks[b], 0, per);
                ecBlocks[b] = Remainder(dataBlocks[b], divisor);
            }

            var result = new List<byte>();
            for (int i = 0; i < per; i++)
                for (int b = 0; b < blocks; b++) result.Add(dataBlocks[b][i]);
            for (int i = 0; i < ec; i++)
                for (int b = 0; b < blocks; b++) result.Add(ecBlocks[b][i]);
            return result.ToArray();
        }

        static void Append(List<bool> bits, int value, int count)
        {
            for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        // Reed-Solomon over GF(256) with the QR polynomial 0x11D.
        static int Mul(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return z & 0xFF;
        }

        static byte[] Divisor(int degree)
        {
            var result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = (byte)Mul(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = Mul(root, 2);
            }
            return result;
        }

        static byte[] Remainder(byte[] data, byte[] divisor)
        {
            var result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                int factor = (b ^ result[0]) & 0xFF;
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++)
                    result[i] ^= (byte)Mul(divisor[i], factor);
            }
            return result;
        }

        static void Set(bool[,] m, bool[,] f, int x, int y, bool dark)
        {
            m[y, x] = dark;
            f[y, x] = true;
        }

        static void DrawFunctionPatterns(bool[,] m, bool[,] f, int version)
        {
            int size = m.GetLength(0);

            for (int i = 0; i < size; i++)
            {
                Set(m, f, 6, i, i % 2 == 0);
                Set(m, f, i, 6, i % 2 == 0);
            }

            DrawFinder(m, f, 3, 3);
            DrawFinder(m, f, size - 4, 3);
            DrawFinder(m, f, 3, size - 4);

            int[] pos = Align[version];
            for (int a = 0; a < pos.Length; a++)
                for (int b = 0; b < pos.Length; b++)
                {
                    bool cornerFinder = (a == 0 && b == 0) || (a == 0 && b == pos.Length - 1) || (a == pos.Length - 1 && b == 0);
                    if (cornerFinder) continue;
                    DrawAlignment(m, f, pos[a], pos[b]);
                }

            DrawFormat(m, f, 0); // reserves the area; real bits are written once the mask is known
        }

        static void DrawFinder(bool[,] m, bool[,] f, int cx, int cy)
        {
            int size = m.GetLength(0);
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || y < 0 || x >= size || y >= size) continue;
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Set(m, f, x, y, dist != 2 && dist != 4);
                }
        }

        static void DrawAlignment(bool[,] m, bool[,] f, int cx, int cy)
        {
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    Set(m, f, cx + dx, cy + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        static void DrawFormat(bool[,] m, bool[,] f, int mask)
        {
            int size = m.GetLength(0);
            int data = mask; // level M is 00 in the two high bits
            int rem = data;
            for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
            int bits = ((data << 10) | rem) ^ 0x5412;

            for (int i = 0; i <= 5; i++) Set(m, f, 8, i, Bit(bits, i));
            Set(m, f, 8, 7, Bit(bits, 6));
            Set(m, f, 8, 8, Bit(bits, 7));
            Set(m, f, 7, 8, Bit(bits, 8));
            for (int i = 9; i < 15; i++) Set(m, f, 14 - i, 8, Bit(bits, i));

            for (int i = 0; i < 8; i++) Set(m, f, size - 1 - i, 8, Bit(bits, i));
            for (int i = 8; i < 15; i++) Set(m, f, 8, size - 15 + i, Bit(bits, i));
            Set(m, f, 8, size - 8, true);
        }

        static bool Bit(int value, int index)
        {
            return ((value >> index) & 1) != 0;
        }

        static void PlaceData(bool[,] m, bool[,] f, byte[] data)
        {
            int size = m.GetLength(0);
            int i = 0;
            for (int right = size - 1; right >= 1; right -= 2)
            {
                if (right == 6) right = 5;
                for (int vert = 0; vert < size; vert++)
                    for (int j = 0; j < 2; j++)
                    {
                        int x = right - j;
                        bool upward = ((right + 1) & 2) == 0;
                        int y = upward ? size - 1 - vert : vert;
                        if (!f[y, x] && i < data.Length * 8)
                        {
                            m[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                            i++;
                        }
                    }
            }
        }

        static void ApplyMask(bool[,] m, bool[,] f, int mask)
        {
            int size = m.GetLength(0);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    if (f[y, x]) continue;
                    bool invert;
                    switch (mask)
                    {
                        case 0: invert = (x + y) % 2 == 0; break;
                        case 1: invert = y % 2 == 0; break;
                        case 2: invert = x % 3 == 0; break;
                        case 3: invert = (x + y) % 3 == 0; break;
                        case 4: invert = (x / 3 + y / 2) % 2 == 0; break;
                        case 5: invert = x * y % 2 + x * y % 3 == 0; break;
                        case 6: invert = (x * y % 2 + x * y % 3) % 2 == 0; break;
                        default: invert = ((x + y) % 2 + x * y % 3) % 2 == 0; break;
                    }
                    if (invert) m[y, x] = !m[y, x];
                }
        }

        static int Penalty(bool[,] m)
        {
            int n = m.GetLength(0);
            int score = 0;

            for (int pass = 0; pass < 2; pass++)
                for (int a = 0; a < n; a++)
                {
                    int run = 1;
                    for (int b = 1; b < n; b++)
                    {
                        bool cur = pass == 0 ? m[a, b] : m[b, a];
                        bool prev = pass == 0 ? m[a, b - 1] : m[b - 1, a];
                        if (cur == prev) { run++; if (run == 5) score += 3; else if (run > 5) score++; }
                        else run = 1;
                    }
                }

            for (int y = 0; y < n - 1; y++)
                for (int x = 0; x < n - 1; x++)
                {
                    bool c = m[y, x];
                    if (c == m[y, x + 1] && c == m[y + 1, x] && c == m[y + 1, x + 1]) score += 3;
                }

            int dark = 0;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                    if (m[y, x]) dark++;
            int total = n * n;
            int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
            score += Math.Max(0, k) * 10;
            return score;
        }
    }
}
