using System;
using System.Collections.Generic;
using System.Text;

namespace CodexUsageMeter
{
    // QR Code Model 2 (ISO/IEC 18004): byte mode, error correction level M, versions 1-6.
    // That covers the phone remote link; the matrix is [y, x] with true = dark, without the quiet zone.
    internal static class QrCode
    {
        private static readonly int[] EccPerBlock = { 0, 10, 16, 26, 18, 24, 16 };
        private static readonly int[] BlockCount = { 0, 1, 1, 1, 2, 2, 4 };
        private const int FormatBitsM = 0;

        internal static bool[,] Encode(string text) { return Encode(text, -1); }

        // mask -1 picks the mask with the lowest standard penalty.
        internal static bool[,] Encode(string text, int mask)
        {
            byte[] payload = Encoding.UTF8.GetBytes(text);
            int version = 1;
            while (version <= 6 && DataCodewords(version) * 8 < 4 + 8 + payload.Length * 8) version++;
            if (version > 6) throw new ArgumentException("QR 코드에 넣기에 너무 긴 링크입니다.");

            List<bool> bits = new List<bool>();
            Append(bits, 4, 4);
            Append(bits, payload.Length, 8);
            foreach (byte b in payload) Append(bits, b, 8);
            int capacity = DataCodewords(version) * 8;
            Append(bits, 0, Math.Min(4, capacity - bits.Count));
            Append(bits, 0, (8 - bits.Count % 8) % 8);
            for (int pad = 0xEC; bits.Count < capacity; pad ^= 0xEC ^ 0x11) Append(bits, pad, 8);
            byte[] data = new byte[capacity / 8];
            for (int i = 0; i < bits.Count; i++) if (bits[i]) data[i >> 3] |= (byte)(0x80 >> (i & 7));

            Symbol symbol = new Symbol(version);
            symbol.DrawFunctionPatterns();
            symbol.DrawCodewords(Interleave(version, data));
            if (mask < 0)
            {
                int best = 0, bestPenalty = Int32.MaxValue;
                for (int candidate = 0; candidate < 8; candidate++)
                {
                    symbol.ApplyMask(candidate); symbol.DrawFormatBits(candidate);
                    int penalty = symbol.Penalty();
                    if (penalty < bestPenalty) { best = candidate; bestPenalty = penalty; }
                    symbol.ApplyMask(candidate);
                }
                mask = best;
            }
            symbol.ApplyMask(mask);
            symbol.DrawFormatBits(mask);
            return symbol.Modules;
        }

        private static void Append(List<bool> bits, int value, int length)
        {
            for (int i = length - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
        }

        private static int RawModules(int version)
        {
            int result = (16 * version + 128) * version + 64;
            if (version >= 2)
            {
                int align = version / 7 + 2;
                result -= (25 * align - 10) * align - 55;
            }
            return result;
        }

        private static int DataCodewords(int version) { return RawModules(version) / 8 - EccPerBlock[version] * BlockCount[version]; }

        private static byte[] Interleave(int version, byte[] data)
        {
            int blocks = BlockCount[version], ecc = EccPerBlock[version], raw = RawModules(version) / 8;
            int shortBlocks = blocks - raw % blocks, shortLength = raw / blocks;
            byte[] divisor = Divisor(ecc);
            List<byte[]> list = new List<byte[]>();
            for (int i = 0, offset = 0; i < blocks; i++)
            {
                int length = shortLength - ecc + (i < shortBlocks ? 0 : 1);
                byte[] part = new byte[length];
                Array.Copy(data, offset, part, 0, length); offset += length;
                byte[] block = new byte[shortLength + 1];
                Array.Copy(part, block, length);
                Array.Copy(Remainder(part, divisor), 0, block, block.Length - ecc, ecc);
                list.Add(block);
            }
            List<byte> result = new List<byte>();
            for (int i = 0; i <= shortLength; i++)
                for (int j = 0; j < blocks; j++)
                    if (i != shortLength - ecc || j >= shortBlocks) result.Add(list[j][i]);
            return result.ToArray();
        }

        private static byte[] Divisor(int degree)
        {
            byte[] result = new byte[degree];
            result[degree - 1] = 1;
            int root = 1;
            for (int i = 0; i < degree; i++)
            {
                for (int j = 0; j < degree; j++)
                {
                    result[j] = Multiply(result[j], root);
                    if (j + 1 < degree) result[j] ^= result[j + 1];
                }
                root = Multiply(root, 0x02);
            }
            return result;
        }

        private static byte[] Remainder(byte[] data, byte[] divisor)
        {
            byte[] result = new byte[divisor.Length];
            foreach (byte b in data)
            {
                int factor = b ^ result[0];
                Array.Copy(result, 1, result, 0, result.Length - 1);
                result[result.Length - 1] = 0;
                for (int i = 0; i < result.Length; i++) result[i] ^= Multiply(divisor[i], factor);
            }
            return result;
        }

        private static byte Multiply(int x, int y)
        {
            int z = 0;
            for (int i = 7; i >= 0; i--)
            {
                z = (z << 1) ^ ((z >> 7) * 0x11D);
                z ^= ((y >> i) & 1) * x;
            }
            return (byte)z;
        }

        private sealed class Symbol
        {
            internal readonly bool[,] Modules;
            private readonly bool[,] _function;
            private readonly int _version, _size;

            internal Symbol(int version)
            {
                _version = version; _size = version * 4 + 17;
                Modules = new bool[_size, _size]; _function = new bool[_size, _size];
            }

            private void Set(int x, int y, bool dark) { Modules[y, x] = dark; _function[y, x] = true; }

            internal void DrawFunctionPatterns()
            {
                for (int i = 0; i < _size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }
                DrawFinder(3, 3); DrawFinder(_size - 4, 3); DrawFinder(3, _size - 4);
                int[] positions = AlignmentPositions();
                int last = positions.Length - 1;
                for (int i = 0; i <= last; i++)
                    for (int j = 0; j <= last; j++)
                        if (!(i == 0 && j == 0) && !(i == 0 && j == last) && !(i == last && j == 0))
                            DrawAlignment(positions[i], positions[j]);
                DrawFormatBits(0);
            }

            private int[] AlignmentPositions()
            {
                if (_version == 1) return new int[0];
                int count = _version / 7 + 2;
                int step = (_version * 8 + count * 3 + 5) / (count * 4 - 4) * 2;
                int[] result = new int[count];
                result[0] = 6;
                for (int i = count - 1, position = _size - 7; i >= 1; i--, position -= step) result[i] = position;
                return result;
            }

            private void DrawFinder(int x, int y)
            {
                for (int dy = -4; dy <= 4; dy++)
                    for (int dx = -4; dx <= 4; dx++)
                    {
                        int distance = Math.Max(Math.Abs(dx), Math.Abs(dy)), xx = x + dx, yy = y + dy;
                        if (xx >= 0 && xx < _size && yy >= 0 && yy < _size) Set(xx, yy, distance != 2 && distance != 4);
                    }
            }

            private void DrawAlignment(int x, int y)
            {
                for (int dy = -2; dy <= 2; dy++)
                    for (int dx = -2; dx <= 2; dx++)
                        Set(x + dx, y + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
            }

            internal void DrawFormatBits(int mask)
            {
                int data = FormatBitsM << 3 | mask, remainder = data;
                for (int i = 0; i < 10; i++) remainder = (remainder << 1) ^ ((remainder >> 9) * 0x537);
                int bits = (data << 10 | remainder) ^ 0x5412;
                for (int i = 0; i <= 5; i++) Set(8, i, Bit(bits, i));
                Set(8, 7, Bit(bits, 6)); Set(8, 8, Bit(bits, 7)); Set(7, 8, Bit(bits, 8));
                for (int i = 9; i < 15; i++) Set(14 - i, 8, Bit(bits, i));
                for (int i = 0; i < 8; i++) Set(_size - 1 - i, 8, Bit(bits, i));
                for (int i = 8; i < 15; i++) Set(8, _size - 15 + i, Bit(bits, i));
                Set(8, _size - 8, true);
            }

            private static bool Bit(int value, int index) { return ((value >> index) & 1) != 0; }

            internal void DrawCodewords(byte[] data)
            {
                int i = 0;
                for (int right = _size - 1; right >= 1; right -= 2)
                {
                    if (right == 6) right = 5;
                    for (int vertical = 0; vertical < _size; vertical++)
                        for (int j = 0; j < 2; j++)
                        {
                            int x = right - j;
                            bool upward = ((right + 1) & 2) == 0;
                            int y = upward ? _size - 1 - vertical : vertical;
                            if (!_function[y, x] && i < data.Length * 8)
                            {
                                Modules[y, x] = Bit(data[i >> 3], 7 - (i & 7));
                                i++;
                            }
                        }
                }
            }

            internal void ApplyMask(int mask)
            {
                for (int y = 0; y < _size; y++)
                    for (int x = 0; x < _size; x++)
                    {
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
                        if (invert && !_function[y, x]) Modules[y, x] = !Modules[y, x];
                    }
            }

            // ISO/IEC 18004 penalty rules N1-N4; any mask decodes, this only favours easier scanning.
            internal int Penalty()
            {
                int result = 0, dark = 0;
                for (int pass = 0; pass < 2; pass++)
                    for (int a = 0; a < _size; a++)
                    {
                        int run = 0; bool previous = false;
                        for (int b = 0; b < _size; b++)
                        {
                            bool value = pass == 0 ? Modules[a, b] : Modules[b, a];
                            if (b > 0 && value == previous) run++;
                            else { if (run >= 5) result += run - 2; run = 1; previous = value; }
                            if (b + 10 < _size && Finder(pass, a, b)) result += 40;
                        }
                        if (run >= 5) result += run - 2;
                    }
                for (int y = 0; y < _size; y++)
                    for (int x = 0; x < _size; x++)
                    {
                        if (Modules[y, x]) dark++;
                        if (x + 1 < _size && y + 1 < _size && Modules[y, x] == Modules[y, x + 1] &&
                            Modules[y, x] == Modules[y + 1, x] && Modules[y, x] == Modules[y + 1, x + 1]) result += 3;
                    }
                int total = _size * _size;
                result += Math.Abs(dark * 20 - total * 10) / total * 10;
                return result;
            }

            private static readonly bool[] FinderLike = { true, false, true, true, true, false, true, false, false, false, false };

            private bool Finder(int pass, int a, int b)
            {
                bool forward = true, backward = true;
                for (int k = 0; k < 11; k++)
                {
                    bool value = pass == 0 ? Modules[a, b + k] : Modules[b + k, a];
                    if (value != FinderLike[k]) forward = false;
                    if (value != FinderLike[10 - k]) backward = false;
                }
                return forward || backward;
            }
        }
    }
}
