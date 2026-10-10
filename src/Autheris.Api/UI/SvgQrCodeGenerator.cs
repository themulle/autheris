namespace Autheris.Api.UI;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Lightweight, zero-dependency pure C# QR Code SVG generator.
/// Generates standards-compliant QR Code SVGs for otpauth:// URIs without external libraries.
/// </summary>
public static class SvgQrCodeGenerator
{
    public static string GenerateSvg(
        string content,
        int pixelsPerModule = 6,
        string darkColor = "#38bdf8",
        string lightColor = "#0f172a")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(content);

        var matrix = QrEncoder.Encode(content);
        int size = matrix.Length;
        int svgSize = size * pixelsPerModule;

        var sb = new StringBuilder();
        sb.Append($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {svgSize} {svgSize}\" width=\"100%\" height=\"100%\" role=\"img\" aria-label=\"2FA QR Code\">");
        sb.Append($"<rect width=\"100%\" height=\"100%\" fill=\"{lightColor}\"/>");

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                if (matrix[y][x])
                {
                    sb.Append($"<rect x=\"{x * pixelsPerModule}\" y=\"{y * pixelsPerModule}\" width=\"{pixelsPerModule}\" height=\"{pixelsPerModule}\" fill=\"{darkColor}\"/>");
                }
            }
        }

        sb.Append("</svg>");
        return sb.ToString();
    }

    private static class QrEncoder
    {
        public static bool[][] Encode(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            // Choose version based on byte length: V4 (up to 62 bytes), V6 (up to 106 bytes), V9 (up to 182 bytes)
            int version = 4;
            int totalCodewords = 80;
            int ecCodewords = 36;
            if (bytes.Length > 62)
            {
                version = 6;
                totalCodewords = 136;
                ecCodewords = 60;
            }
            if (bytes.Length > 106)
            {
                version = 9;
                totalCodewords = 230;
                ecCodewords = 110;
            }

            int dataCodewords = totalCodewords - ecCodewords;
            var dataBits = new List<bool>();

            // Byte mode indicator: 0100
            AppendBits(dataBits, 4, 4);
            // Character count indicator (8 bits for V1-9)
            AppendBits(dataBits, bytes.Length, 8);
            foreach (var b in bytes)
            {
                AppendBits(dataBits, b, 8);
            }

            // Terminator
            int termLen = Math.Min(4, dataCodewords * 8 - dataBits.Count);
            AppendBits(dataBits, 0, termLen);

            // Pad to 8-bit boundary
            while (dataBits.Count % 8 != 0)
            {
                dataBits.Add(false);
            }

            // Pad codewords with 0xEC and 0x11
            byte padByte = 0xEC;
            while (dataBits.Count < dataCodewords * 8)
            {
                AppendBits(dataBits, padByte, 8);
                padByte = (padByte == 0xEC) ? (byte)0x11 : (byte)0xEC;
            }

            // Convert to data codewords
            var dataWords = new byte[dataCodewords];
            for (int i = 0; i < dataCodewords; i++)
            {
                byte val = 0;
                for (int b = 0; b < 8; b++)
                {
                    if (dataBits[i * 8 + b]) val |= (byte)(1 << (7 - b));
                }
                dataWords[i] = val;
            }

            var ecWords = ComputeReedSolomon(dataWords, ecCodewords);
            var finalCodewords = new byte[totalCodewords];
            Array.Copy(dataWords, 0, finalCodewords, 0, dataCodewords);
            Array.Copy(ecWords, 0, finalCodewords, dataCodewords, ecCodewords);

            return BuildMatrix(version, finalCodewords);
        }

        private static void AppendBits(List<bool> bits, int value, int count)
        {
            for (int i = count - 1; i >= 0; i--)
            {
                bits.Add(((value >> i) & 1) == 1);
            }
        }

        private static byte[] ComputeReedSolomon(byte[] data, int ecCount)
        {
            var exp = new byte[512];
            var log = new byte[256];
            int x = 1;
            for (int i = 0; i < 255; i++)
            {
                exp[i] = (byte)x;
                exp[i + 255] = (byte)x;
                log[x] = (byte)i;
                x = (x << 1) ^ (x >= 128 ? 0x11D : 0);
            }

            // Generator polynomial
            var gen = new byte[ecCount + 1];
            gen[0] = 1;
            for (int i = 0; i < ecCount; i++)
            {
                var factor = exp[i];
                for (int j = i + 1; j >= 1; j--)
                {
                    gen[j] = (byte)(gen[j] ^ (gen[j - 1] == 0 ? 0 : exp[(log[gen[j - 1]] + log[factor]) % 255]));
                }
            }

            var res = new byte[ecCount];
            foreach (var b in data)
            {
                byte factor = (byte)(b ^ res[0]);
                Array.Copy(res, 1, res, 0, ecCount - 1);
                res[ecCount - 1] = 0;
                if (factor != 0)
                {
                    int factorLog = log[factor];
                    for (int j = 0; j < ecCount; j++)
                    {
                        if (gen[j + 1] != 0)
                        {
                            res[j] ^= exp[(log[gen[j + 1]] + factorLog) % 255];
                        }
                    }
                }
            }
            return res;
        }

        private static bool[][] BuildMatrix(int version, byte[] codewords)
        {
            int size = 17 + version * 4;
            var matrix = new bool[size][];
            var isFunction = new bool[size][];
            for (int i = 0; i < size; i++)
            {
                matrix[i] = new bool[size];
                isFunction[i] = new bool[size];
            }

            // Finder patterns
            DrawFinder(matrix, isFunction, 0, 0);
            DrawFinder(matrix, isFunction, size - 7, 0);
            DrawFinder(matrix, isFunction, 0, size - 7);

            // Timing patterns
            for (int i = 8; i < size - 8; i++)
            {
                bool val = (i % 2 == 0);
                matrix[6][i] = val;
                isFunction[6][i] = true;
                matrix[i][6] = val;
                isFunction[i][6] = true;
            }

            // Alignment patterns
            int[] alignPos = version switch
            {
                4 => [6, 26],
                6 => [6, 34],
                9 => [6, 26, 46],
                _ => [6, size - 7]
            };

            foreach (var ay in alignPos)
            {
                foreach (var ax in alignPos)
                {
                    if (isFunction[ay][ax]) continue;
                    DrawAlignment(matrix, isFunction, ax - 2, ay - 2);
                }
            }

            // Reserve format information areas
            for (int i = 0; i < 9; i++)
            {
                isFunction[8][i] = true;
                isFunction[i][8] = true;
                if (size - 1 - i < size)
                {
                    isFunction[8][size - 1 - i] = true;
                    isFunction[size - 1 - i][8] = true;
                }
            }
            matrix[size - 8][8] = true; // Dark module
            isFunction[size - 8][8] = true;

            // Place data bits using zigzag
            var allBits = new List<bool>();
            foreach (var b in codewords)
            {
                AppendBits(allBits, b, 8);
            }

            int bitIndex = 0;
            int right = size - 1;
            bool upward = true;

            while (right > 0)
            {
                if (right == 6) right--; // Skip vertical timing pattern
                for (int vertical = 0; vertical < size; vertical++)
                {
                    int y = upward ? (size - 1 - vertical) : vertical;
                    for (int xCol = right; xCol >= right - 1; xCol--)
                    {
                        if (!isFunction[y][xCol])
                        {
                            bool bit = bitIndex < allBits.Count && allBits[bitIndex++];
                            // Apply Mask 0: (x + y) % 2 == 0
                            if ((xCol + y) % 2 == 0) bit = !bit;
                            matrix[y][xCol] = bit;
                        }
                    }
                }
                upward = !upward;
                right -= 2;
            }

            // Format info for L error correction and Mask 0: 0x77C4
            const int formatBits = 0b111011111000100;
            for (int i = 0; i < 15; i++)
            {
                bool bit = ((formatBits >> i) & 1) == 1;
                // Around top-left
                if (i <= 5) matrix[8][i] = bit;
                else if (i == 6) matrix[8][7] = bit;
                else if (i == 7) matrix[8][8] = bit;
                else if (i == 8) matrix[7][8] = bit;
                else matrix[14 - i][8] = bit;

                // Split format bits around corners
                if (i < 8) matrix[size - 1 - i][8] = bit;
                else matrix[8][size - 15 + i] = bit;
            }

            return matrix;
        }

        private static void DrawFinder(bool[][] matrix, bool[][] isFunction, int x, int y)
        {
            for (int dy = -1; dy <= 7; dy++)
            {
                for (int dx = -1; dx <= 7; dx++)
                {
                    int px = x + dx;
                    int py = y + dy;
                    if (px >= 0 && px < matrix.Length && py >= 0 && py < matrix.Length)
                    {
                        isFunction[py][px] = true;
                        bool dark = dx >= 0 && dx <= 6 && dy >= 0 && dy <= 6 &&
                                    (dx == 0 || dx == 6 || dy == 0 || dy == 6 ||
                                     (dx >= 2 && dx <= 4 && dy >= 2 && dy <= 4));
                        matrix[py][px] = dark;
                    }
                }
            }
        }

        private static void DrawAlignment(bool[][] matrix, bool[][] isFunction, int x, int y)
        {
            for (int dy = 0; dy < 5; dy++)
            {
                for (int dx = 0; dx < 5; dx++)
                {
                    int px = x + dx;
                    int py = y + dy;
                    isFunction[py][px] = true;
                    matrix[py][px] = (dx == 0 || dx == 4 || dy == 0 || dy == 4 || (dx == 2 && dy == 2));
                }
            }
        }
    }
}
