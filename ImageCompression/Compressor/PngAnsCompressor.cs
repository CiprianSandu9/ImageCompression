using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using System.Threading.Tasks;
using ZstdSharp;

namespace ImageCompression.CustomCompressor
{
    public static class PngAnsCompressor
    {
        private const int Bpp = 4; // Rgba32
        private const int ChunkSize = 256; // lines per chunk

        public static byte[] CompressImage(byte[] rawPixelData, int width, int height)
        {
            // Calculate how many chunks we need
            int chunksCount = (height + ChunkSize - 1) / ChunkSize;
            
            // We'll store the compressed blobs for each chunk here
            var compressedChunks = new byte[chunksCount][];
            
            // Use Parallel.For to compress chunks independently
            Parallel.For(0, chunksCount, chunkIndex =>
            {
                int startY = chunkIndex * ChunkSize;
                int endY = Math.Min(startY + ChunkSize, height);
                int chunkHeight = endY - startY;
                int stride = width * Bpp;
                int chunkByteSize = chunkHeight * stride;
                int sourceOffset = startY * stride;

                // 1. Rent buffers
                byte[] chunkRaw = ArrayPool<byte>.Shared.Rent(chunkByteSize);
                byte[] filteredBuffer = ArrayPool<byte>.Shared.Rent(chunkByteSize + chunkHeight); // +1 byte per row for filterType
                
                try
                {
                    // Copy raw data for this chunk
                    Array.Copy(rawPixelData, sourceOffset, chunkRaw, 0, chunkByteSize);

                    // 2. Color Transform (SIMD)
                    ApplyColorTransformAvx2(chunkRaw, chunkHeight * width);

                    // 3. Filter (SIMD Selection)
                    int filteredLength = ApplyAdaptiveFiltering(chunkRaw, filteredBuffer, width, chunkHeight, stride);

                    // 4. Compress with ZSTD
                    // Note: Compressor is NOT thread-safe, so we create one per task
                    using var compressor = new Compressor(15);
                    compressedChunks[chunkIndex] = compressor.Wrap(new ReadOnlySpan<byte>(filteredBuffer, 0, filteredLength)).ToArray();
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(chunkRaw);
                    ArrayPool<byte>.Shared.Return(filteredBuffer);
                }
            });

            // Write final file structure
            // [Width:4][Height:4][ChunkCount:4]
            // [ChunkSize0:4][ChunkSize1:4]...
            // [ChunkData0][ChunkData1]...
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(width);
            bw.Write(height);
            bw.Write(chunksCount);
            
            foreach (var chunk in compressedChunks)
            {
                bw.Write(chunk.Length);
            }

            foreach (var chunk in compressedChunks)
            {
                bw.Write(chunk);
            }

            return ms.ToArray();
        }

        public static byte[] DecompressImage(byte[] compressedDataFull)
        {
            using var ms = new MemoryStream(compressedDataFull);
            using var br = new BinaryReader(ms);

            int width = br.ReadInt32();
            int height = br.ReadInt32();
            int chunksCount = br.ReadInt32();

            // Read offsets/sizes
            int[] chunkSizes = new int[chunksCount];
            for (int i = 0; i < chunksCount; i++)
            {
                chunkSizes[i] = br.ReadInt32();
            }

            long dataStartPos = ms.Position;
            int currentOffset = (int)dataStartPos;

            // Pre-allocate result buffer
            byte[] resultImage = new byte[width * height * Bpp];

            Parallel.For(0, chunksCount, chunkIndex =>
            {
                // Determine where this chunk starts in the compressed data
                int chunkOffset = currentOffset;
                for (int j = 0; j < chunkIndex; j++)
                {
                    chunkOffset += chunkSizes[j];
                }

                ReadOnlySpan<byte> chunkCompressedData = new ReadOnlySpan<byte>(compressedDataFull, chunkOffset, chunkSizes[chunkIndex]);

                int startY = chunkIndex * ChunkSize;
                int endY = Math.Min(startY + ChunkSize, height);
                int chunkHeight = endY - startY;
                int stride = width * Bpp;

                // Decompress
                using var decompressor = new Decompressor();
                byte[] decompressedChunk = decompressor.Unwrap(chunkCompressedData).ToArray();

                UnfilterAndReconstruct(decompressedChunk, resultImage, startY, width, chunkHeight, stride);

                // Inverse Color Transform
                // We can do this right here on the chunk part of resultImage
                int pixelStart = startY * width;
                int pixelCount = chunkHeight * width;
                InverseColorTransformAvx2(resultImage, pixelStart, pixelCount);
            });

            return resultImage;
        }

        // ------------------------------------------------------------------------
        // SIMD Color Transform
        // ------------------------------------------------------------------------

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void ApplyColorTransformAvx2(byte[] data, int pixelCount)
        {
            // R = R - G
            // B = B - G
            // G = G (unchanged)
            // A = A (unchanged)
            
            // We process 32 bytes (8 pixels) at a time if using AVX2
            if (Avx2.IsSupported)
            {
                int i = 0;
                int vectorSize = Vector256<byte>.Count;
                int limit = (pixelCount * 4) - vectorSize;

                // Mask to shuffle G to R and B positions
                // Pixel: R G B A
                // We want to subtract G from R and B.
                // Vector input: R G B A  R G B A ...
                // Subtractor:   G 0 G 0  G 0 G 0 ... (No, we subtract G from R and B)
                // Actually:     G 0 G 0 is not quite right because we act on bytes.
                // Let's load 8 pixels.
                // We want to create a vector of G values duplicated? 
                
                // Efficient math: 
                // data - (G_in_R_pos | G_in_B_pos) ?
                // 
                // Shuffle mask to replicate G into R and B slots?
                // Input:   R1 G1 B1 A1 R2 G2 B2 A2 ...
                // Desired: G1  0 G1  0 G2  0 G2  0 ...
                // Then: Input - Desired = (R1-G1) G1 (B1-G1) A1 ...
                
                // Shuffle indices:
                // 0 -> 1 (put G1 in byte 0)
                // 1 -> -1 (keep G1 as is? No subtract 0 from it) -> Actually for subtraction we want 0 operand.
                // But generally 0 operand in subtraction means no change. Input - 0 = Input.
                // So if we put 0 in G and A slots, and G in R and B slots.
                
                // 0x80 is 'zero' in shuffle
                // Pattern for one pixel (4 bytes): 1, 0x80, 1, 0x80
                // Repeat for 8 pixels.
                
                ReadOnlySpan<byte> shuffleArray = new byte[] 
                {
                    1, 0x80, 1, 0x80, 5, 0x80, 5, 0x80, 
                    9, 0x80, 9, 0x80, 13, 0x80, 13, 0x80,
                    17, 0x80, 17, 0x80, 21, 0x80, 21, 0x80,
                    25, 0x80, 25, 0x80, 29, 0x80, 29, 0x80
                };
                
                // Need to pin or load this mask
                Vector256<byte> shuffleMask = Vector256.Create<byte>(shuffleArray);

                fixed (byte* ptr = data)
                {
                    for (; i <= limit; i += vectorSize)
                    {
                        var vec = Avx.LoadVector256(ptr + i);
                        var gProps = Avx2.Shuffle(vec, shuffleMask);
                        var result = Avx2.Subtract(vec, gProps);
                        Avx.Store(ptr + i, result);
                    }
                }
                
                // Handle remaining
                for (; i < pixelCount * 4; i += 4)
                {
                    byte g = data[i + 1];
                    data[i] = (byte)(data[i] - g);
                    data[i + 2] = (byte)(data[i + 2] - g);
                }
            }
            else
            {
                 // Fallback
                 for (int i = 0; i < pixelCount; i++)
                 {
                     int offset = i * 4;
                     byte g = data[offset + 1];
                     data[offset] = (byte)(data[offset] - g);
                     data[offset + 2] = (byte)(data[offset + 2] - g);
                 }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe void InverseColorTransformAvx2(byte[] data, int pixelStart, int pixelCount)
        {
            int offsetStart = pixelStart * 4;
            int totalBytes = pixelCount * 4;
            
            if (Avx2.IsSupported)
            {
                 ReadOnlySpan<byte> shuffleArray = new byte[] 
                {
                    1, 0x80, 1, 0x80, 5, 0x80, 5, 0x80, 
                    9, 0x80, 9, 0x80, 13, 0x80, 13, 0x80,
                    17, 0x80, 17, 0x80, 21, 0x80, 21, 0x80,
                    25, 0x80, 25, 0x80, 29, 0x80, 29, 0x80
                };
                Vector256<byte> shuffleMask = Vector256.Create<byte>(shuffleArray);

                int i = 0;
                int vectorSize = Vector256<byte>.Count;
                int limit = totalBytes - vectorSize;

                fixed (byte* ptr = &data[offsetStart])
                {
                    for (; i <= limit; i += vectorSize)
                    {
                        var vec = Avx.LoadVector256(ptr + i);
                        var gProps = Avx2.Shuffle(vec, shuffleMask);
                        var result = Avx2.Add(vec, gProps);
                        Avx.Store(ptr + i, result);
                    }
                }
                
                for (; i < totalBytes; i += 4)
                {
                    int off = offsetStart + i;
                    byte g = data[off + 1];
                    data[off] = (byte)(data[off] + g);
                    data[off + 2] = (byte)(data[off + 2] + g);
                }
            }
            else
            {
                 for (int i = 0; i < pixelCount; i++)
                 {
                     int off = offsetStart + i * 4;
                     byte g = data[off + 1];
                     data[off] = (byte)(data[off] + g);
                     data[off + 2] = (byte)(data[off + 2] + g);
                 }
            }
        }

        // ------------------------------------------------------------------------
        // Filtering
        // ------------------------------------------------------------------------

        private static unsafe int ApplyAdaptiveFiltering(byte[] raw, byte[] outputBuffer, int width, int height, int stride)
        {
            int bytesPerRow = 1 + stride;
            byte[] prevRowBuf = new byte[stride];            
            Span<byte> prevRow = prevRowBuf;            
            int outPos = 0;
            
            for (int y = 0; y < height; y++)
            {
                int rowOffset = y * stride;
                ReadOnlySpan<byte> currRow = new ReadOnlySpan<byte>(raw, rowOffset, stride);

                // Find best filter
                int bestEncoding = 0;
                long startSum = CalculateSad(currRow, prevRow, 0); 
                long bestSum = startSum; // Default to None (0)

                // Check Sub (1)
                long subSum = CalculateSad(currRow, prevRow, 1);
                if (subSum < bestSum) { bestSum = subSum; bestEncoding = 1; }

                // Check Up (2)
                long upSum = CalculateSad(currRow, prevRow, 2);
                if (upSum < bestSum) { bestSum = upSum; bestEncoding = 2; }

                // Check Average (3)
                long avgSum = CalculateSad(currRow, prevRow, 3);
                if (avgSum < bestSum) { bestSum = avgSum; bestEncoding = 3; }

                // Check Paeth (4)
                long paethSum = CalculateSad(currRow, prevRow, 4);
                if (paethSum < bestSum) { bestSum = paethSum; bestEncoding = 4; }

                // Write output
                outputBuffer[outPos++] = (byte)bestEncoding;
                WriteFilteredRow((byte)bestEncoding, currRow, prevRow, outputBuffer, outPos);
                outPos += stride;

                // Update prevRow = currRow (raw, not filtered)
                currRow.CopyTo(prevRow);
            }
            return outPos;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe long CalculateSad(ReadOnlySpan<byte> current, ReadOnlySpan<byte> prev, byte filterType)
        {
            // SIMD-ing Paeth/Avg is complex because of byte dependencies.
            // SAD of (x - predicted) is basically sum of absolute encoded values.
            // SIMD for standard filters is tricky because of the horizontal 'a' dependency.
            // 'Up' filter (2) is easy to SIMD (current - prev).
            // 'Sub' (1) has dependency.
            
            long sum = 0;
            int length = current.Length;

            fixed (byte* cPtr = current)
            fixed (byte* pPtr = prev)
            {
                 for (int i = 0; i < length; i++)
                 {
                    byte a = (i < Bpp) ? (byte)0 : cPtr[i - Bpp];
                    byte b = pPtr[i];
                    byte c = (i < Bpp) ? (byte)0 : pPtr[i - Bpp];

                    byte predicted = filterType switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (byte)((a + b) / 2),
                        4 => PaethPredictor(a, b, c),
                        _ => 0
                    };

                    int diff = (sbyte)(cPtr[i] - predicted);
                    sum += diff < 0 ? -diff : diff;
                }
            }
            return sum;
        }

        private static unsafe void WriteFilteredRow(byte filterType, ReadOnlySpan<byte> current, ReadOnlySpan<byte> prev, byte[] outBuf, int outOffset)
        {
             int length = current.Length;
             fixed (byte* cPtr = current)
             fixed (byte* pPtr = prev)
             fixed (byte* oPtr = &outBuf[outOffset])
             {
                 for (int i = 0; i < length; i++)
                 {
                    byte a = (i < Bpp) ? (byte)0 : cPtr[i - Bpp];
                    byte b = pPtr[i];
                    byte c = (i < Bpp) ? (byte)0 : pPtr[i - Bpp];

                    byte predicted = filterType switch
                    {
                        0 => 0,
                        1 => a,
                        2 => b,
                        3 => (byte)((a + b) / 2),
                        4 => PaethPredictor(a, b, c),
                        _ => 0
                    };

                    oPtr[i] = (byte)(cPtr[i] - predicted);
                }
             }
        }

        private static unsafe void UnfilterAndReconstruct(byte[] decompressedChunk, byte[] resultImage, int startY, int width, int height, int stride)
        {
            int readPos = 0;
            
            // Buffer for the "virtual" zero row for the first line
            byte[] zeroRow = new byte[stride]; 

            fixed (byte* rPtr = resultImage)
            fixed (byte* dPtr = decompressedChunk)
            fixed (byte* zPtr = zeroRow)
            {
                for (int y = 0; y < height; y++)
                {
                    int absoluteRow = startY + y;
                    int rowStartOffset = absoluteRow * stride;
                    
                    byte filterType = dPtr[readPos++];
                    
                    byte* prevRowPtr;
                    if (y == 0) prevRowPtr = zPtr;
                    else prevRowPtr = rPtr + rowStartOffset - stride;
                    
                    byte* currRowPtr = rPtr + rowStartOffset; // Destination
                    byte* filteredRowPtr = dPtr + readPos;    // Source
                    
                    // Unfilter
                    for (int i = 0; i < stride; i++)
                    {
                         byte a = (i < Bpp) ? (byte)0 : currRowPtr[i - Bpp]; // Left (already written to Dest)
                         byte b = prevRowPtr[i];                             // Up (from Prev)
                         byte c = (i < Bpp) ? (byte)0 : prevRowPtr[i - Bpp]; // UpLeft (from Prev)

                        byte predicted = filterType switch
                        {
                            0 => 0,
                            1 => a,
                            2 => b,
                            3 => (byte)((a + b) / 2),
                            4 => PaethPredictor(a, b, c),
                            _ => 0
                        };
                        
                        currRowPtr[i] = (byte)(filteredRowPtr[i] + predicted);
                    }
                    
                    readPos += stride;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
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
