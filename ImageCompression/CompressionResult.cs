/// <summary>
/// A simple data class to hold the results of a single compression test run.
/// </summary>
public class CompressionResult
{
    public string AlgorithmName { get; }
    public byte[] CompressedData { get; }
    public TimeSpan CompressionTime { get; }
    public TimeSpan DecompressionTime { get; }
    public bool IsLossless { get; }

    public CompressionResult(string algorithmName, byte[] compressedData, TimeSpan compressionTime, TimeSpan decompressionTime, bool isLossless)
    {
        AlgorithmName = algorithmName;
        CompressedData = compressedData;
        CompressionTime = compressionTime;
        DecompressionTime = decompressionTime;
        IsLossless = isLossless;
    }
}
