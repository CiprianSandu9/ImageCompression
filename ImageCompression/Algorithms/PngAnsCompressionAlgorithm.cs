using ImageCompression.CustomCompressor;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>
/// A wrapper for the custom PNG compression algorithm (ANS).
/// </summary>
public class PngAnsCompressionAlgorithm : ICompressionAlgorithm
{
    public string Name => "Custom PNG (Ans)";
    public string FileExtension => ".png";

    public byte[] Compress(byte[] rawPixelData, int width, int height)
    {
        return PngAnsCompressor.CompressImage(rawPixelData, width, height);
    }

    public byte[] Decompress(byte[] compressedData, int width, int height)
    {
        return PngAnsCompressor.DecompressImage(compressedData);
    }
}
