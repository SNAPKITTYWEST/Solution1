# Sovereign

A local-first model playground built from the existing Solution1 C# project. Cloudscape UI on GitHub Pages, an import-free WebAssembly core, a JWT-protected .NET 8 service, a CLI, and a Swift actor host.

This is an independent implementation of a bounded set of Bedrock-style workflows, **not a fork of AWS proprietary source or a full replacement for Amazon Bedrock**. No AWS account, API key, or cloud service is used by the active runtime. The original AWS adapter is preserved in `legacy/aws-adapter/` and excluded from the active solution.

[Open the GitHub Pages playground](https://snapkittywest.github.io/Solution1/)

The browser runs the shared WebAssembly core. Swift is supported through the native host below; this page does not compile or execute arbitrary Swift source.

## Run the playground

Requires Node 24, npm, and LLVM clang with wasm-ld.

```powershell
npm ci
npm run build
npm run dev
```

Open `http://127.0.0.1:5173`. The Pages build is entirely static, with relative asset paths. The browser loads `core.wasm` and instantiates it without imports. Default chat is **extractive document retrieval**, not simulated language-model inference. Documents and tokens remain in tab memory and disappear on reload.

```powershell
npm run cli -- hash "hello"
npm run cli -- embed "local knowledge retrieval"
npm run cli -- flow "Hello world" normalize,guard,hash
```

## C# / .NET service and CLI

Open `Solution1.sln` in Rider. Requires the .NET 8 SDK (not just the runtime). On the originating machine the SDK is `C:\Users\jessi\.dotnet\dotnet.exe`; add that directory to your terminal's PATH if necessary.

```powershell
dotnet build Solution1.sln -c Release
dotnet run --project Sovereign.Tests -c Release
dotnet run --project Sovereign.Host -- hash "hello"
```

Generate a fresh secret in your own shell. The secret signs short-lived tokens and must never go into a Pages build or Git commit.

```powershell
$env:SOVEREIGN_JWT_SECRET = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:SOVEREIGN_ALLOWED_ORIGIN = 'http://127.0.0.1:5173'
# For the published site use https://snapkittywest.github.io (origin has no path).
dotnet run --project Sovereign.Host -- token
# Copy the resulting JWT into Connection & JWT in the playground.
dotnet run --project Sovereign.Host -- serve
```

For real generation, run your own OpenAI-compatible model server and install model weights separately. Before starting the service, set:

```powershell
$env:SOVEREIGN_MODEL_ENDPOINT = 'http://127.0.0.1:11434/'
$env:SOVEREIGN_MODEL = 'your-installed-model-id'
```

The model endpoint must be loopback. Redirects are disabled. Generation is unavailable (HTTP 503) until a model is configured; no response is fabricated. The HTTP transport supports standard completions and SSE deltas; the current HTTP playground route returns a complete response. Pages cannot execute .NET or host a model. Browser policy may require permission for requests to loopback; a trusted HTTPS reverse proxy is another deployment option.

JWT profile: HS256 only, issuer `sovereign`, audience `sovereign-api`, scope `playground`, nonempty subject, `iat`/`nbf`/`exp`, lifetime at most 15 minutes, constant-time signature comparison. Tokens are issued only by the local CLI. Every service route requires authentication. CORS permits one configured origin. Documents and audit queries are isolated by token subject. Server storage is volatile and documents are capped at 100 per subject; the audit ring retains 1,000 requests. This is a local, single-operator service, not a multitenant internet service.

| Method | Route | Body / purpose |
|---|---|---|
| GET | `/api/status` | authenticated runtime status |
| GET | `/api/models` | configured local model ID |
| POST | `/api/invoke` | `{ "prompt": "..." }` |
| POST | `/api/tools` | `{ "operation": "embed", "text": "..." }` |
| POST | `/api/documents` | `{ "title": "...", "text": "..." }` |
| POST | `/api/retrieve` | `{ "prompt": "..." }` |
| POST | `/api/batch` | `{ "operation": "hash", "texts": ["..."] }` |
| POST | `/api/flows` | `{ "text": "...", "steps": ["normalize", "guard", "hash"] }` |
| GET | `/api/audit` | current subject's request outcomes |

## Swift host

See [the formal specification and Swift example](docs/ARCHITECTURE.md). The Swift package uses no third-party Swift packages. It embeds the **Wasmtime C runtime**; a native Wasm engine is an explicit runtime dependency, as requested by the host-embedding specification. Browser execution instead uses the browser's built-in engine. The freestanding core itself has no dependencies or imports.

On macOS with Xcode tools, or Linux with Swift and LLVM:

```sh
npm run build:core
bash scripts/test-swift.sh
```

The script downloads the pinned official Wasmtime v49.0.1 C API distribution into ignored `vendor/`, runs XCTest, and executes the Swift example. Native borrowed output is zero-copy inside a synchronous actor-isolated closure; requesting owned `Data` explicitly copies it. Input `Data` is copied once into linear memory. Pointers must never escape the closure or cross an `await`.

## Verification and publication

```powershell
dotnet build Solution1.sln -c Release
dotnet run --project Sovereign.Tests -c Release --no-build
npm test
npm run build
npx playwright install chromium
npm run test:ui
npm run bench
```

`npm test` includes Wasm properties plus an actual HTTP test against the built .NET host. The HTTP test issues temporary credentials in memory, exercises authentication and retrieval isolation, and compares all four .NET primitives against Wasm. Model transport tests use a controlled HTTP handler; they do not establish the quality or availability of any installed language model.

The Pages workflow gates deployment on Linux (.NET, Wasm, frontend, and browser tests). Native macOS Swift XCTest runs in a separate workflow and does not block the browser playground. Screenshots are uploaded as workflow artifacts. The repository does not contain credentials or model weights.

## Capability boundary

| Bedrock-style area | Implemented here | Boundary |
|---|---|---|
| Model playground | local retrieval + explicit model connector | no bundled trained model |
| Model invocation / streaming | .NET HTTP and SSE transport | local OpenAI-compatible server required |
| Embeddings | deterministic 64-bin hashed word histogram | lexical, not learned semantic embeddings |
| Knowledge bases / retrieval | local document indexing and cosine ranking | no managed storage or web crawler |
| Guardrails | explicit secret-label bitmask | not a safety classifier or PII guarantee |
| Flows / tools | bounded sequential primitive flows with deny short-circuit | no autonomous planning or arbitrary shell execution |
| Batch | up to 100 deterministic inputs | synchronous, no distributed scheduler |
| Audit | session events and deterministic FNV replay seals | not WORM, cryptographic, or tamper-proof |
| JWT access control | local issuer and strict service verifier | no managed IAM, SSO, billing, or cloud control plane |
| Fine-tuning, image/video/audio models, distillation, managed evaluation, provisioned throughput | not implemented | require separate models, training code, hardware, and service infrastructure |

Cloudscape + React are the requested UI dependencies. Vite, Playwright, and LLVM are build/test tools. .NET uses only its standard/shared frameworks; AWS SDK and NuGet runtime dependencies are absent from the active projects.

Independent project by SnapKitty. AWS, Amazon Bedrock, and Cloudscape names identify compatibility/design context, not affiliation. No project license has been invented; establish the desired license before external redistribution beyond this repository.
