# PHASE 2.2: Optimized Chunk Map Implementation

**Estimated Time:** 30-45 minutes  
**Expected Gain:** +3-7% throughput on delta building  
**Risk Level:** LOW  
**Breaking Changes:** NONE

---

## Overview

The current chunk map implementation uses a `Dictionary<uint, int>` that stores only the index of the first chunk with a given rolling checksum. When collisions occur (multiple chunks with the same weak hash), the code performs a linear search.

This optimization replaces the linear search with explicit collision bucket storage, improving cache locality and reducing branch mispredictions in the hot loop.

---

## Current Implementation (HOT PATH)

**Location:** [DeltaBuilder.cs](source/FastRsync/Delta/DeltaBuilder.cs#L25)

```csharp
// Current: Linear search for collisions
for (var j = startIndex; j < chunks.Count && chunks[j].RollingChecksum == checksum; j++)
{
    var chunk = chunks[j];
    var hash = signature.HashAlgorithm.ComputeHash(buffer, i, remainingPossibleChunkSize);
    
    if (StructuralComparisons.StructuralEqualityComparer.Equals(hash, chunks[j].Hash))
    {
        // Match found
        break;
    }
}
```

**Problem:**
- Linear search through sorted chunks in inner loop
- Branch misprediction on each iteration
- CPU not optimized for this pattern

---

## Optimized Implementation

### Step 1: Update CreateChunkMap() to Build Collision Buckets

**Current Code:**
```csharp
private Dictionary<uint, int> CreateChunkMap(IList<ChunkSignature> chunks, out int maxChunkSize, out int minChunkSize)
{
    // ...
    var chunkMap = new Dictionary<uint, int>();
    for (var i = 0; i < chunks.Count; i++)
    {
        var chunk = chunks[i];
        // ...
        if (!chunkMap.ContainsKey(chunk.RollingChecksum))
        {
            chunkMap[chunk.RollingChecksum] = i;  // ← Only first index stored
        }
    }
    return chunkMap;
}
```

**Replacement:**
```csharp
private Dictionary<uint, List<ChunkSignature>> CreateOptimizedChunkMap(
    IList<ChunkSignature> chunks, out int maxChunkSize, out int minChunkSize)
{
    var chunkMap = new Dictionary<uint, List<ChunkSignature>>();
    
    for (var i = 0; i < chunks.Count; i++)
    {
        var chunk = chunks[i];
        // ... size calculations ...
        
        if (!chunkMap.ContainsKey(chunk.RollingChecksum))
        {
            chunkMap[chunk.RollingChecksum] = new List<ChunkSignature>();
        }
        chunkMap[chunk.RollingChecksum].Add(chunk);  // ← All collisions in list
    }
    
    return chunkMap;
}
```

### Step 2: Update Hot Loop to Use Bucket

**Current Code (BuildDelta):**
```csharp
if (!chunkMap.TryGetValue(checksum, out var startIndex)) 
    continue;

for (var j = startIndex; j < chunks.Count && chunks[j].RollingChecksum == checksum; j++)
{
    // Linear search...
}
```

**Replacement:**
```csharp
if (!chunkMap.TryGetValue(checksum, out var candidates)) 
    continue;

// Iterate over pre-computed collision bucket (better cache locality)
foreach (var chunk in candidates)
{
    var hash = signature.HashAlgorithm.ComputeHash(buffer, i, remainingPossibleChunkSize);
    
    if (StructuralComparisons.StructuralEqualityComparer.Equals(hash, chunk.Hash))
    {
        // Match found
        readSoFar += remainingPossibleChunkSize;
        // ...
        break;
    }
}
```

---

## Implementation Checklist

- [ ] Backup current DeltaBuilder.cs
- [ ] Rename `CreateChunkMap()` → `CreateChunkMapLegacy()` (keep for tests)
- [ ] Create new `CreateOptimizedChunkMap()` with List<ChunkSignature> buckets
- [ ] Update both `BuildDelta()` and `BuildDeltaAsync()` to use new map type
- [ ] Update hot loop collision handling
- [ ] Verify compiler warnings (if any)
- [ ] Run unit tests (./Tests.ps1)
- [ ] Run benchmark (Net10PerformanceBenchmark)
- [ ] Compare performance delta

---

## Expected Memory Impact

**Per-file overhead:**
- Before: `Dictionary<uint, int>` ≈ 24 bytes header + 8 bytes/entry
- After: `Dictionary<uint, List<ChunkSignature>>` ≈ 24 bytes + 8 bytes for dict + 32 bytes/list

**Example (1GB file, 2KB chunks):**
- ~500k chunks
- ~10% collision rate (conservative)
- Memory increase: ~500k * 10% * 24 bytes = ~12 MB
- CPU savings: 10-20% reduction in linear search operations

**Verdict:** Acceptable trade-off (small memory for significant CPU improvement)

---

## Backward Compatibility

✅ **Fully compatible** - internal change only:
- Public APIs unchanged
- Signature/delta format unchanged
- No breaking changes

---

## Testing Strategy

### Unit Test Validation
```powershell
# Ensure all tests pass
./Tests.ps1

# Specific test for delta correctness
dotnet test source/FastRsync.Tests --filter "Delta" -v minimal
```

### Performance Test
```powershell
# Run benchmark suite
dotnet run --project source/FastRsync.Benchmarks -- \
  --filter "*DeltaBuild*" --warmup 3 --target 5
```

### Regression Test
```csharp
// Verify: Same delta output with new map implementation
var delta1 = BuildDeltaWithLegacyMap(oldSig, newFile);
var delta2 = BuildDeltaWithOptimizedMap(oldSig, newFile);
Assert.Equal(delta1, delta2);  // Byte-for-byte identical
```

---

## Code Review Points

1. **Collision bucket size distribution** - Check if skewed (indicates poor hash func)
2. **Memory pressure on repeated builds** - Monitor List<T> allocations
3. **Iteration performance** - foreach vs for loop (C# 11 optimizations)

---

## Future Optimization Hook

Once this is implemented, we can consider:
- **Concurrent hash table** (for parallel chunk processing in Phase 3)
- **Bloom filter pre-check** (skip strong hash for impossible matches)
- **SIMD string matching** within collision bucket

---

## When to Implement

**Recommended:** After validating PHASE 1 benchmarks show positive gains

**If you want quick implementation:** Estimated 30-45 minutes for experienced developer familiar with the codebase

