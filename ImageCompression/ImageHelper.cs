using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

/// <summary>
/// Provides helper methods for loading images and manipulating pixel data.
/// </summary>
public static class ImageHelper
{
    /// <summary>
    /// Loads an image from a file path and returns its raw pixel data as a byte array,
    /// along with its dimensions.
    /// </summary>
    public static async Task<(byte[] pixelData, int width, int height)> LoadImageAsRawBytesAsync(string path)
    {
        using Image<Rgba32> image = await Image.LoadAsync<Rgba32>(path);
        return (GetPixelData(image), image.Width, image.Height);
    }

    /// <summary>
    /// Extracts the raw RGBA32 pixel data from an ImageSharp Image object.
    /// </summary>
    public static byte[] GetPixelData(Image<Rgba32> image)
    {
        // We create a byte array to hold the pixel data.
        // For Rgba32, each pixel is 4 bytes (R, G, B, A).
        var pixelData = new byte[image.Width * image.Height * 4];
        image.CopyPixelDataTo(pixelData);
        return pixelData;
    }
}
