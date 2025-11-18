using System.Diagnostics;

public class Program
{
    public static async Task Main(string[] args)
    {
        Console.WriteLine("Image Compression Algorithm Analyzer");
        Console.WriteLine("====================================");

        var algorithmsToTest = new List<ICompressionAlgorithm>
        {
            new PngCompressionAlgorithm(),
            new WebpCompressionAlgorithm(),
            new PngAnsCompressionAlgorithm()
        };

        string imagesPath = Path.Combine(AppContext.BaseDirectory, "images");

        string[] fileEntries = Directory.GetFiles(imagesPath);
        foreach (string fileName in fileEntries)
        {
            try
            {
                // Load image and get its raw pixel data. We use this same raw data for all algorithms
                // to ensure a fair comparison.
                (byte[] rawPixelData, int width, int height) = await ImageHelper.LoadImageAsRawBytesAsync(fileName);
                long originalSize = new FileInfo(fileName).Length;

                Console.WriteLine($"\nAnalyzing '{Path.GetFileName(fileName)}' ({width}x{height})");
                Console.WriteLine($"Original Size: {originalSize / 1024.0:F2} KB");
                Console.WriteLine("---------------------------------------------------------------------------------");

                var results = new List<CompressionResult>();

                // Run each algorithm and measure its performance
                foreach (var algorithm in algorithmsToTest)
                {
                    Console.WriteLine($"Running {algorithm.Name}...");
                    var result = await RunCompressionTestAsync(algorithm, rawPixelData, width, height);
                    results.Add(result);

                    // TODO: Rewrite saving logic
                    // Save the compressed file to disk
                    // string outputFileName = $"{Path.GetFileNameWithoutExtension(imagePath)}_{algorithm.Name.Replace(" ", "")}{algorithm.FileExtension}";
                    // await File.WriteAllBytesAsync($"images\\{outputFileName}", result.CompressedData);
                    // Console.WriteLine($" -> Saved compressed file as '{outputFileName}'");
                }

                // Display the final comparison results
                PrintResultsTable(results, originalSize);                
            }
            catch (Exception ex)
            {
                Console.WriteLine($"An error occurred: {ex.Message}");
            }
        }

        Console.ReadKey();
    }

    /// <summary>
    /// Executes a compression and decompression cycle for a given algorithm and measures performance.
    /// </summary>
    private static async Task<CompressionResult> RunCompressionTestAsync(ICompressionAlgorithm algorithm, byte[] rawPixelData, int width, int height)
    {
        var stopwatch = new Stopwatch();

        // --- Compression ---
        stopwatch.Start();
        byte[] compressedData = await Task.Run(() => algorithm.Compress(rawPixelData, width, height));
        stopwatch.Stop();
        var compressionTime = stopwatch.Elapsed;

        // --- Decompression & Verification ---
        stopwatch.Restart();
        byte[] decompressedData = await Task.Run(() => algorithm.Decompress(compressedData, width, height));
        stopwatch.Stop();
        var decompressionTime = stopwatch.Elapsed;

        // Verify that the compression was truly lossless
        bool isLossless = rawPixelData.SequenceEqual(decompressedData);
        if (!isLossless)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"  [VALIDATION FAILED] Decompressed data does not match original for {algorithm.Name}!");
            Console.ResetColor();
        }

        return new CompressionResult(
            algorithm.Name,
            compressedData,
            compressionTime,
            decompressionTime,
            isLossless
        );
    }

    /// <summary>
    /// Prints a formatted table with the compression results.
    /// </summary>
    private static void PrintResultsTable(List<CompressionResult> results, long originalSize)
    {
        Console.WriteLine("\n--- Final Comparison Results ---");
        Console.WriteLine("---------------------------------------------------------------------------------");
        Console.WriteLine("| Algorithm                  | Compressed Size (KB) | Ratio  | Comp. Time (ms) | Decomp. Time (ms) |");
        Console.WriteLine("|----------------------------|----------------------|--------|-----------------|-------------------|");

        foreach (var result in results.OrderBy(r => r.CompressedData.Length))
        {
            double compressedKb = result.CompressedData.Length / 1024.0;
            double ratio = (double)result.CompressedData.Length / originalSize;

            Console.WriteLine($"| {result.AlgorithmName,-26} | {compressedKb,20:F2} | {ratio,6:P1} | {result.CompressionTime.TotalMilliseconds,15:F2} | {result.DecompressionTime.TotalMilliseconds,17:F2} |");
        }
        Console.WriteLine("---------------------------------------------------------------------------------");
    }
}
