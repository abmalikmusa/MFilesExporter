namespace MFilesExporter.Application.Abstractions;

public interface IChecksumCalculator : IDisposable
{
    void Append(ReadOnlySpan<byte> data);
    string FinalizeHex();
}

public interface IChecksumCalculatorFactory
{
    IChecksumCalculator Create();
}
