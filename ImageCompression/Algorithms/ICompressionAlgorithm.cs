/// <summary>
/// Defines the contract for a compression algorithm.
/// Any new algorithm must implement this interface to be included in the comparison.
/// </summary>
public interface ICompressionAlgorithm
{
    /// <summary>
    /// The display name of the algorithm.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// The file extension for the compressed output.
    /// </summary>
    string FileExtension { get; }

    /// <summary>
    /// Compresses the raw image pixel data.
    /// </summary>
    /// <param name="rawPixelData">A byte array of the raw pixel data (e.g., in RGB24 format).</param>
    /// <param name="width">The width of the image.</param>
    /// <param name="height">The height of the image.</param>
    /// <returns>A byte array containing the compressed data.</returns>
    byte[] Compress(byte[] rawPixelData, int width, int height);

    /// <summary>
    /// Decompresses data back into raw image pixel data.
    /// </summary>
    /// <param name="compressedData">The byte array of compressed data.</param>
    /// <returns>A byte array of the raw pixel data.</returns>
    byte[] Decompress(byte[] compressedData, int width, int height);
}
