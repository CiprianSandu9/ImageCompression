using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>
/// A wrapper for the standard WEBP compression algorithm.
/// Uses the ImageSharp library to handle the encoding and decoding.
/// </summary>
public class WebpCompressionAlgorithm : ICompressionAlgorithm
{
    public string Name => "Standard WebP";
    public string FileExtension => ".webp";

    public byte[] Compress(byte[] rawPixelData, int width, int height)
    {
        using var image = Image.LoadPixelData<Rgba32>(rawPixelData, width, height);
        using var memoryStream = new MemoryStream();

        var encoder = new WebpEncoder
        {
            FileFormat = WebpFileFormatType.Lossless
        };

        image.SaveAsWebp(memoryStream, encoder);
        return memoryStream.ToArray();
    }

    public byte[] Decompress(byte[] compressedData, int width, int height)
    {
        using var memoryStream = new MemoryStream(compressedData);
        using var image = Image.Load<Rgba32>(memoryStream);

        return ImageHelper.GetPixelData(image);
    }
}
