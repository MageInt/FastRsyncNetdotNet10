# .NET 10 Optimization Implementation Guide

## Executive Summary

This document provides step-by-step instructions to implement the 12 optimizations identified in `OPTIMIZATION_ANALYSIS_NET10.md`.

**Expected Total Time:** 4-6 hours  
**Expected Throughput Gain:** 50-80%  
**Risk Level:** Low-Medium (all phases include backward compatibility fallbacks)

---

## PHASE 1: Quick Wins (60 minutes target)

### PHASE 1.1: ArrayPool<byte> for Reused Buffers
**File:** `source/FastRsync/Delta/DeltaBuilder.cs`  
**File:** `source/FastRsync/Delta/BinaryDeltaWriter.cs`  
**Time:** 15 min  
**Gain:** +3-8% throughput

**Changes:**
1. Add `using System.Buffers;` to both files
2. Wrap `new byte[readBufferSize]` in ArrayPool.Shared.Rent/Return pattern
3. Replace bare `new byte[]` allocations with ArrayPool pattern

**See:** `IMPLEMENTATION_PHASE1_ARRAYPOOL.md`

---

### PHASE 1.2: xxHash3 Integration
**Files:**
- `source/FastRsync/Hash/IHashAlgorithm.cs` (new overload)
- New file: `source/FastRsync/Hash/XxHash3Wrapper.cs`
- `source/FastRsync/Core/SupportedAlgorithms.cs` (add factory)
- `source/FastRsync/Core/BinaryFormat.cs` (version bump for signatures)

**Time:** 30 min  
**Gain:** +15-25% throughput

**Changes:**
1. Create new `XxHash3Wrapper : IHashAlgorithm` using `System.IO.Hashing.XxHash3`
2. Add factory method in `SupportedAlgorithms.Hashing.XxHash3()`
3. Set as new default (with backward compatibility for xxHash64 reading)
4. Update signature format version to 3.0
5. Update SignatureReader to support both v2 (xxHash64) and v3 (xxHash3)

**See:** `IMPLEMENTATION_PHASE1_XXHASH3.md`

---

### PHASE 1.3: Stream Span<T> Overloads
**Files:**
- `source/FastRsync/Signature/SignatureBuilder.cs`
- `source/FastRsync/Delta/DeltaBuilder.cs`
- `source/FastRsync/Delta/DeltaApplier.cs`
- `source/FastRsync/Delta/BinaryDeltaWriter.cs`

**Time:** 10 min  
**Gain:** +2-4% throughput

**Changes:**
1. Replace `stream.Read(byte[], int, int)` with `stream.Read(Span<byte>)`
2. Replace `stream.Write(byte[], int, int)` with `stream.Write(ReadOnlySpan<byte>)`
3. Use `buffer.AsSpan(offset, length)` to create spans from existing arrays

---

### PHASE 1.4: stackalloc for Small Temporary Buffers
**Files:**
- `source/FastRsync/Signature/SignatureBuilder.cs` (metadata hashes)
- `source/FastRsync/Hash/CryptographyHashAlgorithmWrapper.cs` (if applicable)

**Time:** 5 min  
**Gain:** +1-3% throughput

**Changes:**
1. Identify small temporary allocations (< 256 bytes typically)
2. Replace with `Span<byte> buffer = stackalloc byte[N];`
3. Pass spans to hash functions

---

## PHASE 2: Core Optimizations (2-3 hours target)

### PHASE 2.1: Span-based Rolling Checksum API
**Files:**
- `source/FastRsync/Hash/IRollingChecksum.cs` (new overload)
- `source/FastRsync/Hash/Adler32RollingChecksum.cs`
- `source/FastRsync/Hash/Adler32RollingChecksumV2.cs`
- `source/FastRsync/Delta/DeltaBuilder.cs` (usage)

**Time:** 30 min  
**Gain:** +8-12% throughput

**Changes:**
1. Add `uint Calculate(ReadOnlySpan<byte> block)` to `IRollingChecksum`
2. Implement in both `Adler32RollingChecksum` and `Adler32RollingChecksumV2`
3. Update `DeltaBuilder` to use `new ReadOnlySpan<byte>(buffer, offset, length)` pattern
4. Keep old `byte[], int, int` overload for backward compatibility

---

### PHASE 2.2: Optimized Chunk Map (Dictionary → SortedDictionary)
**Files:**
- `source/FastRsync/Delta/DeltaBuilder.cs`

**Time:** 30 min  
**Gain:** +3-7% throughput

**Changes:**
1. Change `Dictionary<uint, int>` → `SortedDictionary<uint, List<ChunkSignature>>`
2. Update `CreateChunkMap()` to store collision buckets explicitly
3. Update hot loop to iterate `chunkMap[checksum]` list instead of linear search

---

### PHASE 2.3: Span-based Metadata Hashing
**Files:**
- `source/FastRsync/Signature/SignatureBuilder.cs`
- Update to use `System.Security.Cryptography.IncrementalHash`

**Time:** 20 min  
**Gain:** +1-2% throughput

**Changes:**
1. Replace `hashAlgorithm.ComputeHash(stream)` with incremental hash reading chunks
2. Use `stackalloc` for intermediate buffers
3. Keep compatibility wrapper for existing API

---

### PHASE 2.4: JSON Serialization Optimization
**Files:**
- `source/FastRsync/Delta/BinaryDeltaWriter.cs`
- `source/FastRsync/Signature/SignatureWriter.cs` (if applicable)

**Time:** 20 min  
**Gain:** +0.5-1% throughput

**Changes:**
1. Use `Utf8JsonWriter` directly instead of string intermediate
2. Write directly to stream with UTF-8 encoding
3. Leverage JsonSerializerContext for source generation

---

## PHASE 3: Advanced Optimizations (4+ hours)

### PHASE 3.1: SIMD Rolling Checksum Vectorization
**Files:**
- New file: `source/FastRsync/Hash/SimdAdler32RollingChecksum.cs`
- `source/FastRsync/Hash/Adler32RollingChecksumV2.cs` (conditional dispatch)

**Time:** 2+ hours  
**Gain:** +15-30% on checksum operations  
**Risk:** Medium (SIMD correctness, CPU dispatch)

**Changes:**
1. Implement `SimdAdler32RollingChecksum` with SIMD batch processing
2. Add `Vector256.IsHardwareAccelerated` check for runtime dispatch
3. Vectorize Rotate() for 32-64 parallel rotations
4. Extensive validation against scalar reference implementation
5. Only use on batches where beneficial (e.g., chunk size > 64 bytes)

**See:** `IMPLEMENTATION_PHASE3_SIMD.md` (comprehensive guide)

---

### PHASE 3.2: ref struct DataRange (if feasible)
**Files:**
- `source/FastRsync/Core/DataRange.cs`
- Impact analysis needed: check all usages

**Time:** 1-2 hours  
**Risk:** HIGH (breaking change if exposed in public API)

**Action:** Run `list_code_usages` for `DataRange` first to assess risk

---

### PHASE 3.3: Parallel Chunk Map Creation (conditional)
**Files:**
- `source/FastRsync/Delta/DeltaBuilder.cs` (new overload)

**Time:** 30 min  
**Gain:** +10-20% on multi-core systems (only if chunks.Count > 100k)

**Changes:**
1. Detect CPU core count
2. If cores >= 4 AND chunks.Count > 100k: use Parallel.For
3. Otherwise: keep sequential path
4. Use `ConcurrentDictionary` for thread-safe aggregation

---

### PHASE 3.4: Hardware-Accelerated MD5 (minimal benefit)
**Files:**
- `source/FastRsync/Hash/CryptographyHashAlgorithmWrapper.cs`

**Time:** 10 min  
**Gain:** +5-15% on verification phase (minor path)

---

## Validation & Testing

### Before Each Phase
1. Run existing unit tests: `Tests.ps1`
2. Ensure all tests pass
3. Record baseline benchmark (from `Net10PerformanceBenchmark.cs`)

### After Each Optimization
1. Run unit tests again
2. Run benchmark for that optimization
3. Verify no regression on other metrics (memory, GC)
4. Document throughput delta

### Full Validation Checklist
- [ ] All unit tests pass
- [ ] Signature format backward compatible (xxHash64 still readable)
- [ ] No memory leaks (check ArrayPool returns)
- [ ] No unbounded allocations in hot paths
- [ ] Benchmark shows expected gains
- [ ] Code reviews completed
- [ ] Profiling validation (dotnet-trace on 1 GB file)

---

## Rollout Strategy

### Development
1. Branch: `feature/net10-optimization`
2. Implement Phase 1 first (low risk, quick wins)
3. Validate each optimization independently
4. Create separate commit per optimization for bisect capability

### Testing
1. Run `Tests.ps1` in debug and release
2. Run benchmark on different CPU architectures (if possible)
3. Test with real-world workloads (>1GB files)

### Release
1. Version bump (e.g., 2.5.0 → 3.0.0 due to signature format change)
2. Release notes: Document xxHash3 as new default, xxHash64 still supported
3. CLI flag: `--hash-algorithm {xxHash64,xxHash3}` for compatibility

---

## Performance Monitoring

### Metrics to Track
- **Throughput:** MB/s for signature building, delta building, delta applying
- **Memory:** Peak working set, Gen0 allocations, ArrayPool hit rate
- **Latency:** Wall-clock time for 100MB, 1GB operations
- **CPU:** Instruction count, branch mispredictions (via performance profiler)

### Baseline (Before)
```
Signature Build: ??? MB/s
Delta Build:    ??? MB/s
Delta Apply:    ??? MB/s
Gen0 Allocs:    ???
```

### Target (After Phase 1+2)
```
Signature Build: +20% throughput
Delta Build:    +35% throughput
Delta Apply:    +15% throughput
Gen0 Allocs:    -40%
```

---

## FAQ

### Q: Should I implement all 12 optimizations?
**A:** Start with Phase 1 + Phase 2 (safe, +25-40%). Phase 3 is optional and higher risk.

### Q: Will xxHash3 break backward compatibility?
**A:** No. Old signatures (xxHash64) will still be readable. New signatures default to xxHash3 but can be configured.

### Q: What's the risk of SIMD vectorization?
**A:** Medium. Modular arithmetic (Adler32) must be exact. Requires property-based testing. CPU dispatch (.IsSupported) makes it safe on old hardware.

### Q: Should I use Parallel for chunk maps?
**A:** Only if profiling shows chunk map creation is bottleneck AND you have 4+ cores. Usually not worth it.

### Q: What about Native AOT compatibility?
**A:** Most optimizations are compatible. SIMD intrinsics may require reflection trimming directives. ArrayPool is fully trimming-safe.

---

## Next Steps

1. Read `OPTIMIZATION_ANALYSIS_NET10.md` (full technical analysis)
2. Start with PHASE 1.1 (ArrayPool)
3. Validate each change with benchmarks
4. Update this document as you progress

