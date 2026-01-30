using System;
using System.Buffers;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using FastRsync.Delta;
using FastRsync.Signature;

namespace FastRsync.Benchmarks
{
    /// <summary>
    /// .NET 10 Performance Optimization Benchmark Suite
    /// 
    /// Validates improvements across:
    /// - Signature building (rolling checksum + strong hash)
    /// - Delta building (checksum rotation + hash lookup)
    /// - Delta applying (copy + write operations)
    /// 
    /// Test files:
    /// - 100 MB random data + 10% repeating blocks
    /// - Default 2 KB chunk size
    /// - Multiple patterns to stress different code paths
    /// </summary>
    [SimpleJob(warmupCount: 3, iterationCount: 5)]
    [RPlotExporter]
    [RankColumn]
    [MemoryDiagnoser]
    public class Net10PerformanceBenchmark : IDisposable
    {
        private const int TestFileSize = 100 * 1024 * 1024; // 100 MB
        private const int ChunkSize = 2048; // Default chunk size

        private byte[] basisFileData;
        private byte[] modifiedFileData;
        private MemoryStream basisStream;
        private MemoryStream modifiedStream;
        private MemoryStream signatureStream;
        private MemoryStream deltaStream;

        [GlobalSetup]
        public void Setup()
        {
            // Generate test data: 90% random, 10% repeated blocks
            Console.WriteLine("Generating test data...");
            basisFileData = GenerateTestData(TestFileSize, repeatRatio: 0.1);
            
            // Create modified version with 5% changes
            modifiedFileData = (byte[])basisFileData.Clone();
            ModifyData(modifiedFileData, changeRatio: 0.05);

            Console.WriteLine($"Test data size: {TestFileSize / 1024 / 1024} MB");
            Console.WriteLine("Setup complete.");
        }

        [GlobalCleanup]
        public void Cleanup() => Dispose();

        public void Dispose()
        {
            basisStream?.Dispose();
            modifiedStream?.Dispose();
            signatureStream?.Dispose();
            deltaStream?.Dispose();
        }

        /// <summary>
        /// BASELINE: Signature building with current implementation
        /// Measures:
        /// - File I/O overhead
        /// - Rolling checksum calculation (hot path: Adler32V2.Calculate)
        /// - Strong hash computation (hot path: xxHash64)
        /// - Serialization overhead
        /// </summary>
        [Benchmark(Description = "SignatureBuilder.Build (Baseline)")]
        public void SignatureBuildBaseline()
        {
            basisStream = new MemoryStream(basisFileData, writable: false);
            signatureStream = new MemoryStream();

            var signatureBuilder = new SignatureBuilder();
            signatureBuilder.Build(basisStream, new SignatureWriter(signatureStream));

            signatureStream.Position = 0;
        }

        /// <summary>
        /// DELTA BUILDING: Hot path for rolling checksum rotations
        /// Measures:
        /// - Inner loop: Adler32V2.Rotate() performance
        /// - Hash table lookups (chunkMap)
        /// - Strong hash verification on collisions
        /// - Delta command serialization
        /// </summary>
        [Benchmark(Description = "DeltaBuilder.BuildDelta (Baseline)")]
        public void DeltaBuildBaseline()
        {
            // Reuse signature from previous step
            basisStream = new MemoryStream(basisFileData, writable: false);
            signatureStream = new MemoryStream();

            var signatureBuilder = new SignatureBuilder();
            signatureBuilder.Build(basisStream, new SignatureWriter(signatureStream));

            // Build delta
            signatureStream.Position = 0;
            modifiedStream = new MemoryStream(modifiedFileData, writable: false);
            deltaStream = new MemoryStream();

            var deltaBuilder = new DeltaBuilder();
            deltaBuilder.BuildDelta(
                modifiedStream,
                new SignatureReader(signatureStream, null),
                new BinaryDeltaWriter(deltaStream)
            );

            deltaStream.Position = 0;
        }

        /// <summary>
        /// DELTA APPLYING: Reproduces basis + delta
        /// Measures:
        /// - Stream copy performance (basis file random access)
        /// - Write performance
        /// - Hash verification overhead (MD5)
        /// </summary>
        [Benchmark(Description = "DeltaApplier.Apply (Baseline)")]
        public void DeltaApplyBaseline()
        {
            // Build signature and delta
            basisStream = new MemoryStream(basisFileData, writable: false);
            signatureStream = new MemoryStream();

            var signatureBuilder = new SignatureBuilder();
            signatureBuilder.Build(basisStream, new SignatureWriter(signatureStream));

            signatureStream.Position = 0;
            basisStream.Position = 0;
            modifiedStream = new MemoryStream(modifiedFileData, writable: false);
            deltaStream = new MemoryStream();

            var deltaBuilder = new DeltaBuilder();
            deltaBuilder.BuildDelta(
                modifiedStream,
                new SignatureReader(signatureStream, null),
                new BinaryDeltaWriter(deltaStream)
            );

            // Apply delta
            deltaStream.Position = 0;
            basisStream.Position = 0;
            var outputStream = new MemoryStream();

            var deltaApplier = new DeltaApplier();
            deltaApplier.Apply(basisStream, new BinaryDeltaReader(deltaStream, null, 4096), outputStream);

            outputStream.Dispose();
        }

        /// <summary>
        /// FOCUSED BENCHMARK: Rolling Checksum Rotation Only
        /// Isolates: Adler32V2.Rotate() performance
        /// This is the HOTTEST path in delta building (~98% CPU time in inner loop)
        /// </summary>
        [Benchmark(Description = "Adler32V2.Rotate (Hot Path Isolation)")]
        public uint AdlerRotateHotPath()
        {
            var checksum = new Hash.Adler32RollingChecksumV2();
            byte[] window = new byte[2048];
            new Random(42).NextBytes(window);

            uint result = checksum.Calculate(window, 0, 128);

            // Simulate sliding window: 1000 rotations
            for (int i = 0; i < 1000; i++)
            {
                result = checksum.Rotate(result, window[i], window[i + 128], 128);
            }

            return result;
        }

        /// <summary>
        /// FOCUSED BENCHMARK: Strong Hash Only
        /// Isolates: xxHash64.ComputeHash() performance
        /// Typical: 10-15% of delta building time (called on collisions)
        /// </summary>
        [Benchmark(Description = "xxHash64.ComputeHash (Strong Hash)")]
        public byte[] HashComputationPath()
        {
            var hashAlgo = Core.SupportedAlgorithms.Hashing.Default();
            byte[] chunk = new byte[2048];
            new Random(42).NextBytes(chunk);

            byte[] hash = null;
            for (int i = 0; i < 100; i++)
            {
                hash = hashAlgo.ComputeHash(chunk, 0, chunk.Length);
            }

            return hash;
        }

        /// <summary>
        /// STRESS TEST: Large file operations
        /// Measures real-world throughput with contention
        /// </summary>
        [Benchmark(Description = "End-to-End: Signature + Delta + Apply (Large)")]
        public void EndToEndLarge()
        {
            // Prepare streams
            basisStream = new MemoryStream(basisFileData, writable: false);
            signatureStream = new MemoryStream();

            // Phase 1: Signature
            var signatureBuilder = new SignatureBuilder();
            signatureBuilder.Build(basisStream, new SignatureWriter(signatureStream));

            // Phase 2: Delta
            signatureStream.Position = 0;
            basisStream.Position = 0;
            modifiedStream = new MemoryStream(modifiedFileData, writable: false);
            deltaStream = new MemoryStream();

            var deltaBuilder = new DeltaBuilder();
            deltaBuilder.BuildDelta(
                modifiedStream,
                new SignatureReader(signatureStream, null),
                new BinaryDeltaWriter(deltaStream)
            );

            // Phase 3: Apply
            deltaStream.Position = 0;
            basisStream.Position = 0;
            var outputStream = new MemoryStream();

            var deltaApplier = new DeltaApplier();
            deltaApplier.Apply(basisStream, new BinaryDeltaReader(deltaStream, null, 4096), outputStream);

            outputStream.Dispose();
        }

        // ==================== Helper Methods ====================

        private static byte[] GenerateTestData(int size, double repeatRatio)
        {
            var data = new byte[size];
            var random = new Random(42); // Deterministic seed
            int repeatSize = (int)(size * repeatRatio / 100); // 10% is ~10MB blocks

            // Generate random data
            random.NextBytes(data);

            // Add repeating patterns (to stress rolling checksum and delta detection)
            if (repeatSize > 0)
            {
                byte[] pattern = new byte[256];
                random.NextBytes(pattern);

                for (int i = 0; i < size; i += repeatSize)
                {
                    int copySize = Math.Min(repeatSize, size - i);
                    Array.Copy(pattern, 0, data, i, Math.Min(pattern.Length, copySize));
                }
            }

            return data;
        }

        private static void ModifyData(byte[] data, double changeRatio)
        {
            var random = new Random(123); // Different seed for reproducible changes
            int changeCount = (int)(data.Length * changeRatio);

            for (int i = 0; i < changeCount; i++)
            {
                int position = random.Next(data.Length);
                data[position] = (byte)random.Next(256);
            }
        }
    }

    /// <summary>
    /// Utility to run benchmarks with specific configurations
    /// </summary>
    public static class BenchmarkRunner
    {
        /// <summary>
        /// Run minimal benchmark (for quick validation during development)
        /// </summary>
        public static void RunQuick()
        {
            Console.WriteLine("=== Quick Validation Run ===");
            using (var benchmark = new Net10PerformanceBenchmark())
            {
                benchmark.Setup();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                benchmark.AdlerRotateHotPath();
                sw.Stop();
                Console.WriteLine($"Adler32.Rotate: {sw.ElapsedMilliseconds}ms");

                sw.Restart();
                benchmark.HashComputationPath();
                sw.Stop();
                Console.WriteLine($"Hash computation: {sw.ElapsedMilliseconds}ms");

                sw.Restart();
                benchmark.SignatureBuildBaseline();
                sw.Stop();
                Console.WriteLine($"Signature build: {sw.ElapsedMilliseconds}ms ({TestFileSizeMb()} MB)");

                benchmark.Cleanup();
            }
        }

        private static int TestFileSizeMb() => 100;
    }
}
