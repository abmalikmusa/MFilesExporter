namespace MFilesExporter.Persistence.MFiles.Blobs;

/// <summary>
/// Hash algorithm used by the <see cref="BinaryObjectReader"/> to fingerprint
/// the payload as bytes flow through. <see cref="None"/> disables hashing
/// entirely — appropriate when the caller only wants the byte count.
/// </summary>
public enum BinaryChecksumAlgorithm
{
    /// <summary>No hash computed. Skips the incremental-hash overhead.</summary>
    None   = 0,

    /// <summary>SHA-256. Default — matches the sink's on-disk filename hashing.</summary>
    Sha256 = 1,

    /// <summary>SHA-1. Legacy; supported for interop only.</summary>
    Sha1   = 2,

    /// <summary>SHA-512. Stronger; ~25 % slower than SHA-256 on modern CPUs.</summary>
    Sha512 = 3,

    /// <summary>MD5. Non-cryptographic; supported only for legacy manifest formats.</summary>
    Md5    = 4,
}
