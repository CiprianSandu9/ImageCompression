using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Tiff;
using SixLabors.ImageSharp.Formats.Tiff.Constants;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>
/// A wrapper for the standard TIFF compression algorithm using LZW.
/// Uses the ImageSharp library to handle the encoding and decoding.
/// </summary>
public class TiffLzwCompressionAlgorithm : ICompressionAlgorithm
{
    public string Name => "Standard TIFF (LZW)";
    public string FileExtension => ".tiff";

    public byte[] Compress(byte[] rawPixelData, int width, int height)
    {
        using var image = Image.LoadPixelData<Rgba32>(rawPixelData, width, height);
        using var memoryStream = new MemoryStream();

        var encoder = new TiffEncoder
        {
            // Specify the LZW compression scheme for TIFF
            Compression = TiffCompression.Lzw
        };

        image.SaveAsTiff(memoryStream, encoder);
        return memoryStream.ToArray();
    }

    public byte[] Decompress(byte[] compressedData)
    {
        using var memoryStream = new MemoryStream(compressedData);
        using var image = Image.Load<Rgba32>(memoryStream);

        // ImageSharp decodes the TIFF, we just need to get the raw pixel data back out
        return ImageHelper.GetPixelData(image);
    }
}
