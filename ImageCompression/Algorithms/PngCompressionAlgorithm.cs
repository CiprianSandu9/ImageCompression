using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>
/// A wrapper for the standard PNG compression algorithm (DEFLATE).
/// Uses the ImageSharp library to handle the encoding and decoding.
/// </summary>
public class PngCompressionAlgorithm : ICompressionAlgorithm
{
    public string Name => "Standard PNG (Deflate)";
    public string FileExtension => ".png";

    public byte[] Compress(byte[] rawPixelData, int width, int height)
    {
        using var image = Image.LoadPixelData<Rgba32>(rawPixelData, width, height);
        using var memoryStream = new MemoryStream();

        // The PngEncoder provides options to control compression level, etc.
        var encoder = new PngEncoder
        {
            CompressionLevel = PngCompressionLevel.BestCompression,
            FilterMethod = PngFilterMethod.Paeth // Paeth can be good for photographic content
        };

        image.SaveAsPng(memoryStream, encoder);
        return memoryStream.ToArray();
    }

    public byte[] Decompress(byte[] compressedData, int width, int height)
    {
        using var memoryStream = new MemoryStream(compressedData);
        using var image = Image.Load<Rgba32>(memoryStream);

        // ImageSharp decodes the PNG, we just need to get the raw pixel data back out
        return ImageHelper.GetPixelData(image);
    }
}
