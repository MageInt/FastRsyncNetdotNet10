namespace FastRsync.Delta
{
    public class DeltaMetadata
    {
        public required string HashAlgorithm { get; set; }
        public required string ExpectedFileHashAlgorithm { get; set; }
        public required string ExpectedFileHash { get; set; }
        public required string BaseFileHashAlgorithm { get; set; }
        public required string BaseFileHash { get; set; }
    }
}
