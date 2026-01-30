using System;

namespace FastRsync.Hash
{
    public interface IRollingChecksum
    {
        string Name { get; }
        UInt32 Calculate(byte[] block, int offset, int count);
        
        /// <summary>
        /// Span-based overload for .NET 10 optimization.
        /// Enables better JIT codegen and bounds check elimination.
        /// </summary>
        UInt32 Calculate(ReadOnlySpan<byte> block);
        
        UInt32 Rotate(UInt32 checksum, byte remove, byte add, int chunkSize);
    }
}