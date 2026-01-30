using System.Collections.Generic;
using FastRsync.Core;
using FastRsync.Hash;

namespace FastRsync.Signature
{
    public class SignatureMetadata
    {
        public required string ChunkHashAlgorithm { get; set; }
        public required string RollingChecksumAlgorithm { get; set; }
        public required string BaseFileHashAlgorithm { get; set; }
        public required string BaseFileHash { get; set; }
    }

    public enum RsyncFormatType
    {
        Octodiff,
        FastRsync
    }

    public class Signature
    {
        public Signature(SignatureMetadata metadata, RsyncFormatType type)
        {
            HashAlgorithm = SupportedAlgorithms.Hashing.Create(metadata.ChunkHashAlgorithm);
            RollingChecksumAlgorithm = SupportedAlgorithms.Checksum.Create(metadata.RollingChecksumAlgorithm);
            Chunks = new List<ChunkSignature>();
            Metadata = metadata;
            Type = type;
        }

        public IHashAlgorithm HashAlgorithm { get; private set; } = null!;
        public IRollingChecksum RollingChecksumAlgorithm { get; private set; } = null!;
        public List<ChunkSignature> Chunks { get; private set; } = null!;
        public SignatureMetadata Metadata { get; private set; } = null!;
        public RsyncFormatType Type { get; private set; }
    }
}