using ZstdSharp;

namespace ImageCompression.CustomCompressor
{
    /// <summary>
    /// PNG-like compression and decompression pipeline using ANS.
    /// </summary>
    public static class PngAnsCompressor
    {
        /// <summary>
        /// Compresses an image using PNG-style adaptive filtering and Zstandard (ANS) compression.
        /// </summary>
        public static byte[] CompressImage(byte[] rawPixelData, int width, int height)
        {

            byte[] filteredData = ApplyAdaptiveFiltering(rawPixelData, width, height, 4);

            int compressionLevel = 14;
            using var compressor = new Compressor(compressionLevel);

            // Compress the FILTERED data
            byte[] compressedFilteredData = compressor.Wrap(filteredData).ToArray();

            // Compress the RAW data (no filter)
            // On photos, this will almost always be SMALLER
            byte[] compressedRawData = compressor.Wrap(rawPixelData).ToArray();

            byte[] bestCompressedData;
            bool wasFiltered;

            if (compressedFilteredData.Length < compressedRawData.Length)
            {
                bestCompressedData = compressedFilteredData;
                wasFiltered = true;
            }
            else
            {
                bestCompressedData = compressedRawData;
                wasFiltered = false;
            }

            // Prepare final output: [width (4 bytes)][height (4 bytes)][wasFiltered (1 byte)][compressed data]
            using var ms = new MemoryStream();
            using var bw = new BinaryWriter(ms);

            bw.Write(width);
            bw.Write(height);
            bw.Write(wasFiltered); // Write 1 byte (true/false)
            bw.Write(bestCompressedData);

            return ms.ToArray();
        }

        /// <summary>
        /// Decompresses an image, un-filters it, and saves it.
        /// </summary>
        public static byte[] DecompressImage(byte[] compressedDataFull)
        {
            using var ms = new MemoryStream(compressedDataFull);
            using var br = new BinaryReader(ms);

            int width = br.ReadInt32();
            int height = br.ReadInt32();
            bool wasFiltered = br.ReadBoolean(); // Read filtered flag
            const int bpp = 4; // Rgba32

            byte[] compressedData = br.ReadBytes((int)(ms.Length - ms.Position));

            // Decompress with ANS (Zstd)
            using var decompressor = new Decompressor();
            byte[] decompressedData = decompressor.Unwrap(compressedData).ToArray();

            // Un-apply filters (or don't)
            byte[] rawPixelData;
            if (wasFiltered)
            {
                // If we filtered it, we must un-filter it
                rawPixelData = UnapplyFiltering(decompressedData, width, height, bpp);
            }
            else
            {
                // The data was raw, so it's already perfect
                rawPixelData = decompressedData;
            }

            return rawPixelData;
        }

        #region Filtering Logic

        private enum FilterType : byte { None = 0, Sub = 1, Up = 2, Average = 3, Paeth = 4 }

        private static byte[] ApplyAdaptiveFiltering(byte[] raw, int width, int height, int bpp)
        {
            int stride = width * bpp;
            // Output size = (1 byte filter type + stride) * height
            var filtered = new MemoryStream(height + stride * height);
            var prevRow = new byte[stride];
            var currentRow = new byte[stride];
            var filterBuffer = new byte[stride];

            for (int y = 0; y < height; y++)
            {
                int rowStart = y * stride;
                Buffer.BlockCopy(raw, rowStart, currentRow, 0, stride);

                long bestFilterSum = long.MaxValue;
                FilterType bestFilter = FilterType.None;
                byte[] bestFilterBuffer = null;

                // Try all 5 filters and pick the best one (lowest sum of absolute differences)
                foreach (FilterType filterType in Enum.GetValues(typeof(FilterType)))
                {
                    long currentSum = 0;
                    Filter(filterType, currentRow, prevRow, filterBuffer, bpp);

                    foreach (byte b in filterBuffer)
                    {
                        // Use signed byte for sum calculation
                        currentSum += Math.Abs((int)(sbyte)b);
                    }

                    if (currentSum < bestFilterSum)
                    {
                        bestFilterSum = currentSum;
                        bestFilter = filterType;
                        // Swap buffers
                        (bestFilterBuffer, filterBuffer) = (filterBuffer, new byte[stride]);
                    }
                }

                // Write the chosen filter type (1 byte) + filtered data
                filtered.WriteByte((byte)bestFilter);
                filtered.Write(bestFilterBuffer, 0, stride);

                // Current row becomes previous row for next iteration
                Buffer.BlockCopy(currentRow, 0, prevRow, 0, stride);
            }
            return filtered.ToArray();
        }

        private static void Filter(FilterType type, byte[] current, byte[] prev, byte[] output, int bpp)
        {
            for (int i = 0; i < current.Length; i++)
            {
                byte a = (i < bpp) ? (byte)0 : current[i - bpp]; // Left
                byte b = prev[i];                                // Above
                byte c = (i < bpp) ? (byte)0 : prev[i - bpp];    // Above-left

                output[i] = (byte)(current[i] - type switch
                {
                    FilterType.None => 0,
                    FilterType.Sub => a,
                    FilterType.Up => b,
                    FilterType.Average => (byte)((a + b) / 2),
                    FilterType.Paeth => PaethPredictor(a, b, c),
                    _ => 0
                });
            }
        }

        private static byte PaethPredictor(byte a, byte b, byte c)
        {
            int p = a + b - c;     // Initial estimate
            int pa = Math.Abs(p - a);  // Distances to a, b, c
            int pb = Math.Abs(p - b);
            int pc = Math.Abs(p - c);

            // Return nearest of a, b, c
            if (pa <= pb && pa <= pc) return a;
            if (pb <= pc) return b;
            return c;
        }

        #endregion

        #region Un-filtering Logic

        private static byte[] UnapplyFiltering(byte[] filtered, int width, int height, int bpp)
        {
            int stride = width * bpp;
            byte[] raw = new byte[stride * height];
            byte[] prevRow = new byte[stride]; // Starts as all zeros

            var ms = new MemoryStream(filtered);

            for (int y = 0; y < height; y++)
            {
                var filterType = (FilterType)ms.ReadByte(); // Read the 1-byte filter type

                byte[] filteredRow = new byte[stride];
                ms.Read(filteredRow, 0, stride);

                int rowStart = y * stride;

                for (int i = 0; i < stride; i++)
                {
                    byte a = (i < bpp) ? (byte)0 : raw[rowStart + i - bpp]; // Left
                    byte b = prevRow[i];                                  // Above
                    byte c = (i < bpp) ? (byte)0 : prevRow[i - bpp];        // Above-left

                    byte recon = (byte)(filteredRow[i] + filterType switch
                    {
                        FilterType.None => 0,
                        FilterType.Sub => a,
                        FilterType.Up => b,
                        FilterType.Average => (byte)((a + b) / 2),
                        FilterType.Paeth => PaethPredictor(a, b, c),
                        _ => 0
                    });

                    raw[rowStart + i] = recon;
                }

                // Current reconstructed row becomes previous row
                Buffer.BlockCopy(raw, rowStart, prevRow, 0, stride);
            }
            return raw;
        }

        #endregion
    }
}
