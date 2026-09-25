# 1. Formal System Architecture & Memory Specification

## Target

The supplied target placeholder is resolved to a sovereign local tool runtime for the Bedrock-style playground. The kernel implements bounded text normalization, FNV checksums, lexical embeddings and a literal secret-label guard. Hosting language models or reproducing AWS infrastructure is outside this kernel's implemented scope.

Primitive graph:

```text
UTF-8 byte buffer -> bounds checks -> operation dispatch
  hash:      xor -> wrapping u32 multiply
  normalize: byte range test -> ASCII case offset
  embed:     normalize -> ASCII word boundaries -> FNV -> 64 counters
  guard:     normalize -> bounded byte comparisons -> bitmask
  success -> increment tick -> deterministic replay seal
  error   -> error flag; no tick advance and no output write
```

The hash recurrence is `h[0]=2166136261; h[i+1]=((h[i] XOR byte[i])*16777619) mod 2^32`.
For each maximal `[A-Za-z0-9]+` word w, increment `v[FNV(lower(w)) mod 64]`. The embedding vector is 64 unsigned 32-bit counters, not a learned embedding. Similarity is `(a·b)/(sqrt(a·a)*sqrt(b·b))`, with zero returned if either norm is zero. Cosine scoring occurs in the host, not Wasm; integer kernel state remains deterministic. Rank ties use ascending document ID.

Guard mask: bit 0 = literal `password`, bit 1 = `secret`, bit 2 = `api_key`, case-insensitive ASCII substring match. A flow stops after any nonzero guard result. Maximum 16 steps. No recursive dispatch, dynamic code execution, network, random numbers or clocks in the core.

## State machine

`UNINITIALIZED --reset_core--> READY --alloc--> READY --execute(valid)--> READY`.
Every successful operation increments tick; invalid operations preserve tick, seal and output and set the error flag. `dealloc` releases only the newest live allocation. A second free, non-LIFO free, or mismatched size fails. `reset_core` wipes metadata and all used arena bytes, then restores version, tick zero and the initial seal. Call reset before use; all shipped hosts do so.

The host never suspends while holding a memory pointer. `memory.grow` can relocate native storage; reacquire the base after every exported call. JS recreates DataView/Uint8Array views after allocation. Swift confines the store to an actor.

## Exact byte map (wasm32, little endian)

| Half-open range | Purpose |
|---|---|
| `[0, 65536)` | compiler execution stack, downward from 65536; fixed 64 KiB |
| `[65536, 131072)` | compiler static strings and allocator cursor; reserved static region |
| `[131072, 131096)` | six-u32 metadata header |
| `[131096, 196608)` | reserved, zeroed on reset |
| `[196608, memory_size)` | explicit LIFO arena |
| `[memory_size, 16777216)` | potential future arena pages |

Initial memory: 262144 bytes (4 × 64 KiB pages). Maximum: 16777216 bytes (256 pages). `alloc` grows by `ceil((required-current)/65536)` pages and handles the Wasm grow failure sentinel. Allocation size and addition are validated before arithmetic. Inputs are limited to 65536 bytes. Compiler flags fix stack placement, initial/maximum memory, and omit a start entrypoint and libc.

Metadata offsets relative to 131072: `0 magic=0x534f5631`, `4 ABI version=1`, `8 tick`, `12 error`, `16 replay seal`, `20 last output byte count`. The header is 24 bytes, 4-byte aligned.

Each allocation is `[u32 requested_size, u32 header_offset, payload, zero padding]`; total bytes = `8+align_up(size,8)`. Returned payload offsets are 8-byte aligned. Freed blocks including their headers are zeroed. An allocation scan validates that each requested span is inside a live allocation. Hosts are trusted owners of linear memory; this is not protection against a host deliberately corrupting allocator headers.

## ABI

```c
uint32_t reset_core(void);
uint32_t metadata_ptr(void);
uint32_t alloc(uint32_t size);             // 0 on failure
uint32_t dealloc(uint32_t ptr, uint32_t size); // status, exact LIFO release
uint32_t execute_core_step(uint32_t op, uint32_t input_ptr,
                           uint32_t input_len, uint32_t output_ptr,
                           uint32_t output_capacity);
```

Offsets are 32-bit values, never native host pointers. `memory` is exported; no imports are accepted by the native bridge. Ops: 1 hash (4 bytes), 2 embed (256 bytes), 3 guard (4 bytes), 4 normalize (input length). Output capacity may exceed result length; the entire capacity is zeroed before writing. Input and output ranges must not overlap. Tick overflow fails before writing.

Return codes: 0 OK, 1 invalid pointer/overlap, 2 size/capacity, 3 unsupported operation, 4 allocation failure, 5 invalid free, 6 tick overflow. `alloc` returns zero and writes a metadata error. Core output pointers must reference allocations created by this instance. There is no C struct padding across the ABI; words are explicitly little endian.

Seal update: `s = mix(mix(previous_seal, op), input_hash)`, then `s=mix(s,output_byte)` for each result byte. **FNV is not cryptographic**. These seals detect replay divergence, not malicious tampering. Do not call this a signature, proof of authorship, or WORM chain.

# 2. Core Wasm Code (minimal low-level C)

Full source: [`core/core.c`](../core/core.c). Build command is explicit in [`scripts/build-core.mjs`](../scripts/build-core.mjs). It uses `--target=wasm32-unknown-unknown -nostdlib -fno-builtin`, exports the ABI above, and has no WASI or standard-library calls. `public/core.wasm` is checked in so the exact artifact is available to all hosts; CI recompiles it before tests and deployment.

JS/CLI host: [`shared/engine.mjs`](../shared/engine.mjs). Allocation lifetimes use `try/finally`. All calculations in the kernel are bounded loops; no recursion is used, and stack use does not scale with input size. Browser and native engine traps still contain unexpected stack or memory faults. Swift enables 20 million Wasmtime fuel units per call and a 64 KiB native Wasm stack cap.

# 3. Swift Host Bridge & Memory Pointer Extensions

The native engine is Wasmtime's C API, pinned by the test script. [`bridge.c`](../swift/Sources/CSovereign/bridge.c) owns the engine, module, store and instance. It validates zero imports, looks up typed exports, checks returned value kinds, converts traps into status codes, and destroys every owned engine object.

[`Runtime.swift`](../swift/Sources/SovereignRuntime/Runtime.swift) confines the store to an actor. Range checks use `length <= total - start` after validating start, avoiding pointer-addition overflow. Pointer extensions encode/decode little-endian words without alignment assumptions. A new native memory base is read for each borrow.

There are two deliberate ownership boundaries:

- `withOutput` exposes the Wasm result directly as a borrowed `UnsafeRawBufferPointer`, with no output-array allocation. Its closure is synchronous, cannot suspend, and must not retain the pointer.
- `execute` returns `Result<Data, CoreError>` and copies the result to owned storage. Input `Data` is copied into the Wasm arena once. This is not advertised as end-to-end zero-copy for arbitrary existing input buffers.

Wasmtime is an explicit native runtime dependency, not a hidden high-level abstraction. The core remains freestanding. Cloudscape requires React and is confined to the frontend; JWT uses standard .NET cryptographic primitives rather than invented cryptography.

# 4. End-to-End Swift Execution Example & Test Harness

```swift
let runtime = try Runtime(module: Data(contentsOf: URL(fileURLWithPath: "core.wasm")))
let hash = try await runtime.withOutput(.hash, input: Data("hello".utf8)) {
    try $0.readU32(at: 0)
}
assert(hash == 0x4f9f2cab)
let output = try await runtime.execute(.normalize, input: Data("HELLO".utf8)).get()
assert(String(decoding: output, as: UTF8.self) == "hello")
```

[`RuntimeTests.swift`](../swift/Tests/SovereignRuntimeTests/RuntimeTests.swift) tests initialization, malformed modules, known vectors, entire-memory determinism, byte encoding, offset overflow, input limits, maximum-size inputs and memory growth. JS properties additionally test invalid opcodes, short output buffers, overlapping spans, stale pointers, incorrect frees, allocation exhaustion and tick overflow. HTTP integration compares the server's independent .NET implementation against Wasm and checks JWT rejection, CORS and document isolation.

Benchmark [`scripts/bench.mjs`](../scripts/bench.mjs) warms the engine then measures 10,000 embedding calls, including host encode/copy/free overhead. Output includes runtime version, input size and wall-clock duration. It is a local measurement, not an AWS performance comparison.

## Sources for runtime and frontend integration

- [Wasmtime C API](https://docs.wasmtime.dev/c-api/wasmtime_8h.html)
- [Wasmtime linear memory](https://docs.wasmtime.dev/examples-memory.html)
- [Cloudscape components and required React integration](https://cloudscape.design/get-started/for-developers/using-cloudscape-components/)
