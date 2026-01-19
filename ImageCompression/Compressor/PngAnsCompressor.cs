using System;
using System.Buffers;
using System.IO;
using System.Threading.Tasks;
using ZstdSharp;

namespace ImageCompression.CustomCompressor
{
    public static class PngAnsCompressor
    {
        private const int Bpp = 4; // Rgba32

        public static byte[] CompressImage(byte[] rawPixelData, int width, int height)
        {
            int stride = width * Bpp;
            int totalSize = stride * height;
            int maxBufferSize = 16 + height + totalSize;

            byte[] filteredBuffer = ArrayPool<byte>.Shared.Rent(maxBufferSize);
            byte[] colorTransformedBuffer = ArrayPool<byte>.Shared.Rent(totalSize);

            try
            {
                ApplyColorTransform(rawPixelData, colorTransformedBuffer, width, height);

                int filteredLength = ApplyAdaptiveFiltering(colorTransformedBuffer, filteredBuffer, width, height, stride);
                var filteredSpan = new ReadOnlySpan<byte>(filteredBuffer, 0, filteredLength);

                using var compressor = new Compressor(15); // Higher level for better ratio
                byte[] compressedFiltered = compressor.Wrap(filteredSpan).ToArray();
                byte[] compressedTransformed = compressor.Wrap(new ReadOnlySpan<byte>(colorTransformedBuffer, 0, totalSize)).ToArray();

                bool useFiltered = compressedFiltered.Length < compressedTransformed.Length;
                byte[] bestData = useFiltered ? compressedFiltered : compressedTransformed;

                using var ms = new MemoryStream();
                // 'leaveOpen: true' allows us to safely access 'ms' after 'bw' is disposed
                using (var bw = new BinaryWriter(ms, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    bw.Write(width);
                    bw.Write(height);
                    bw.Write(useFiltered);
                    bw.Write(bestData);
                } // bw.Dispose() is called here, forcing a FLUSH to 'ms'

                return ms.ToArray();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(filteredBuffer);
                ArrayPool<byte>.Shared.Return(colorTransformedBuffer);
            }
        }

        public static byte[] DecompressImage(byte[] compressedDataFull)
        {
            using var ms = new MemoryStream(compressedDataFull);
            using var br = new BinaryReader(ms);

            int width = br.ReadInt32();
            int height = br.ReadInt32();
            bool wasFiltered = br.ReadBoolean();

            int stride = width * Bpp;
            int totalPixelsLen = stride * height;

            byte[] compressedPayload = br.ReadBytes((int)(ms.Length - ms.Position));

            using var decompressor = new Decompressor();
            byte[] decompressedData = decompressor.Unwrap(compressedPayload).ToArray();

            byte[] resultImage;

            if (wasFiltered)
            {
                byte[] rawImage = new byte[totalPixelsLen];
                byte[] prevRowArr = ArrayPool<byte>.Shared.Rent(stride);

                try
                {
                    // Initialize "previous row" to zeros for the first pass
                    Array.Clear(prevRowArr, 0, stride);
                    Span<byte> prevRow = prevRowArr.AsSpan(0, stride);

                    var filterReader = new ReadOnlySpan<byte>(decompressedData);
                    int readPos = 0;

                    for (int y = 0; y < height; y++)
                    {
                        byte filterType = filterReader[readPos++];
                        var filteredRow = filterReader.Slice(readPos, stride);
                        readPos += stride;

                        int rowStart = y * stride;

                        for (int i = 0; i < stride; i++)
                        {
                            // A = Left (Safe: looks at rawImage which we are currently filling)
                            byte a = (i < Bpp) ? (byte)0 : rawImage[rowStart + i - Bpp];

                            // B = Up (Safe: looks at prevRow which hasn't changed yet)
                            byte b = prevRow[i];

                            // C = Up-Left (Safe: looks at prevRow which hasn't changed yet)
                            byte c = (i < Bpp) ? (byte)0 : prevRow[i - Bpp];

                            byte predicted = filterType switch
                            {
                                0 => 0,
                                1 => a,
                                2 => b,
                                3 => (byte)((a + b) / 2),
                                4 => PaethPredictor(a, b, c),
                                _ => 0
                            };

                            // Reconstructed = FilteredDelta + Predicted
                            rawImage[rowStart + i] = (byte)(filteredRow[i] + predicted);
                        }

                        // CRITICAL: Update prevRow only AFTER the entire row is reconstructed
                        // We copy the data we just wrote (rawImage) into the prevRow buffer
                        var completedRow = new ReadOnlySpan<byte>(rawImage, rowStart, stride);
                        completedRow.CopyTo(prevRow);
                    }
                    resultImage = rawImage;
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(prevRowArr);
                }
            }
            else
            {
                resultImage = decompressedData;
            }

            InverseColorTransform(resultImage, width, height);
            return resultImage;
        }

        private static void ApplyColorTransform(byte[] input, byte[] output, int width, int height)
        {
            // Subtract Green Transform
            int totalPixels = width * height;
            Parallel.For(0, totalPixels, i =>
            {
                int offset = i * 4;
                // R G B A
                byte g = input[offset + 1];
                output[offset] = (byte)(input[offset] - g);     // R - G
                output[offset + 1] = g;                             // G
                output[offset + 2] = (byte)(input[offset + 2] - g); // B - G
                output[offset + 3] = input[offset + 3];             // A
            });
        }

        private static void InverseColorTransform(byte[] data, int width, int height)
        {
            // Inverse Subtract Green
            int totalPixels = width * height;
            Parallel.For(0, totalPixels, i =>
            {
                int offset = i * 4;
                byte g = data[offset + 1];
                data[offset] = (byte)(data[offset] + g);     // R + G
                data[offset + 2] = (byte)(data[offset + 2] + g); // B + G
            });
        }

        private static int ApplyAdaptiveFiltering(byte[] raw, byte[] outputBuffer, int width, int height, int stride)
        {
            // Each row in output: [FilterByte (1)] + [FilteredPixels (stride)]
            int bytesPerRow = 1 + stride;

            Parallel.For(0, height, y =>
            {
                int inputOffset = y * stride;
                var currRow = new ReadOnlySpan<byte>(raw, inputOffset, stride);

                ReadOnlySpan<byte> prevRow;
                byte[]? tempPrevRow = null;

                if (y == 0)
                {
                    // For the first row, the "previous row" is virtual and strictly zero.
                    tempPrevRow = new byte[stride];
                    prevRow = tempPrevRow;
                }
                else
                {
                    prevRow = new ReadOnlySpan<byte>(raw, inputOffset - stride, stride);
                }

                long bestSum = long.MaxValue;
                byte bestFilter = 0;

                // Try all 5 filters
                for (byte f = 0; f < 5; f++)
                {
                    long sum = CalculateFilterSum(f, currRow, prevRow);
                    if (sum < bestSum)
                    {
                        bestSum = sum;
                        bestFilter = f;
                    }
                }

                int outputRowOffset = y * bytesPerRow;
                outputBuffer[outputRowOffset] = bestFilter;
                WriteFilterOutput(bestFilter, currRow, prevRow, outputBuffer, outputRowOffset + 1);
            });

            return height * bytesPerRow;
        }

        private static long CalculateFilterSum(byte type, ReadOnlySpan<byte> current, ReadOnlySpan<byte> prev)
        {
            long sum = 0;
            for (int i = 0; i < current.Length; i++)
            {
                byte a = (i < Bpp) ? (byte)0 : current[i - Bpp];
                byte b = prev[i];
                byte c = (i < Bpp) ? (byte)0 : prev[i - Bpp];

                byte predicted = type switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (byte)((a + b) / 2),
                    4 => PaethPredictor(a, b, c),
                    _ => 0
                };

                // FIXED: Cast to sbyte, then int to handle negative wrapping correctly
                int diff = (sbyte)(current[i] - predicted);
                sum += diff < 0 ? -diff : diff;
            }
            return sum;
        }

        private static void WriteFilterOutput(byte type, ReadOnlySpan<byte> current, ReadOnlySpan<byte> prev, byte[] output, int outOffset)
        {
            for (int i = 0; i < current.Length; i++)
            {
                byte a = (i < Bpp) ? (byte)0 : current[i - Bpp];
                byte b = prev[i];
                byte c = (i < Bpp) ? (byte)0 : prev[i - Bpp];

                byte predicted = type switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (byte)((a + b) / 2),
                    4 => PaethPredictor(a, b, c),
                    _ => 0
                };

                output[outOffset + i] = (byte)(current[i] - predicted);
            }
        }

        private static byte PaethPredictor(byte a, byte b, byte c)
        {
            int p = a + b - c;
            int pa = Math.Abs(p - a);
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }
    }
}