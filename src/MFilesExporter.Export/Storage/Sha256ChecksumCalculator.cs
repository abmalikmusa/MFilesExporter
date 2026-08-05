using System.Security.Cryptography;
using MFilesExporter.Application.Abstractions;

namespace MFilesExporter.Export.Storage;

internal sealed class Sha256ChecksumCalculator : IChecksumCalculator
{
    private IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private bool _finalized;

    public void Append(ReadOnlySpan<byte> data)
    {
        if (_finalized) throw new InvalidOperationException("Cannot append after FinalizeHex.");
        _hash.AppendData(data);
    }

    public string FinalizeHex()
    {
        _finalized = true;
        return Convert.ToHexString(_hash.GetHashAndReset()).ToLowerInvariant();
    }

    public void Dispose() => _hash.Dispose();
}

internal sealed class Sha256ChecksumCalculatorFactory : IChecksumCalculatorFactory
{
    public IChecksumCalculator Create() => new Sha256ChecksumCalculator();
}
