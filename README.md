# Sovereign

A local-first model playground built from the existing Solution1 C# project. Cloudscape UI on GitHub Pages, an import-free WebAssembly core, a JWT-protected .NET 8 service, a CLI, and a Swift actor host.

This is an independent implementation of a bounded set of Bedrock-style workflows, **not a fork of AWS proprietary source or a full replacement for Amazon Bedrock**. No AWS account, API key, or cloud service is used by the active runtime. The original AWS adapter is preserved in `legacy/aws-adapter/` and excluded from the active solution.

The playground is **not** published on GitHub Pages. It requires a signed-in session, so it is served by the service below rather than by a public static site; see [Serving the playground behind a login](#serving-the-playground-behind-a-login).

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

JWT profile: HS256 only, issuer `sovereign`, audience `sovereign-api`, scope `playground`, nonempty subject, `iat`/`nbf`/`exp`, lifetime at most 15 minutes, constant-time signature comparison. Tokens are issued by the local CLI, or by the service itself after a successful sign-in. Every service route requires authentication. CORS permits one configured origin. Documents and audit queries are isolated by token subject. Server storage is volatile and documents are capped at 100 per subject; the audit ring retains 1,000 requests. This is a local, single-operator service, not a multitenant internet service.

## Serving the playground behind a login

Point the service at the built site and it will serve the playground itself, only to a signed-in session:

```powershell
$env:SOVEREIGN_WEB_ROOT = (Resolve-Path .\dist).Path
dotnet run --project Sovereign.Host -- serve
```

With `SOVEREIGN_WEB_ROOT` set, an unauthenticated request for `/` is redirected to `/login` instead of receiving the application, `/api/*` still answers 401, and `/login` stays reachable so a session can be established. A successful sign-in sets an `HttpOnly`, `SameSite=Strict` session cookie holding the same 15-minute JWT; the browser sends it on navigation, which is why the gate works without JavaScript.

**GitHub Pages does not publish this application.** The Pages workflow deploys only a static notice, because Pages serves static files with no ability to reject a request: an unauthenticated copy on the public internet could be bypassed in one line of JavaScript, so a client-side gate there would be theatre rather than a control. Authentication is enforced by the service, which can actually refuse to respond.

To deploy this yourself, run the service on a host the browser can reach, put a trusted HTTPS reverse proxy in front of it, and point a domain at it. A tunnel works for a private instance. Keep the service on loopback when you do not need it reachable, and set `SOVEREIGN_ALLOWED_ORIGIN` to the exact origin that serves the page.

## Single sign-on (SAML 2.0)

The playground has a sign-in page at `/login`. It performs SP-initiated SAML against the identity provider you configure, and exchanges the verified assertion for the same short-lived HS256 token the CLI issues, so nothing downstream changes.

SAML is **off until configured**. With any of the variables below missing, `/saml/login` and `/saml/metadata` return 503 and `/saml/status` names the missing setting. There are no defaults and no fallback identity provider.

| Variable | Meaning |
|---|---|
| `SOVEREIGN_SAML_SP_ENTITYID` | Entity ID this service provider publishes; register it with your IdP |
| `SOVEREIGN_SAML_ACS` | Public **https** URL the IdP posts assertions back to |
| `SOVEREIGN_SAML_IDP_ENTITYID` | IdP entity ID, matched against the Response `Issuer` |
| `SOVEREIGN_SAML_IDP_SSO_POST` | IdP HTTP-POST single-sign-on endpoint |
| `SOVEREIGN_SAML_IDP_CERT` | Path to a PEM signing certificate, or several separated by `;` |
| `SOVEREIGN_SAML_SUBJECT_ATTRIBUTE` | Attribute to use as the token subject; defaults to the NameID |
| `SOVEREIGN_SAML_RETURN_ORIGIN` | Origin the browser is returned to after login |
| `SOVEREIGN_BIND` | Listener address; loopback by default |

```powershell
$env:SOVEREIGN_SAML_SP_ENTITYID  = 'https://sovereign.example/saml'
$env:SOVEREIGN_SAML_ACS          = 'https://sovereign.example/saml/acs'
$env:SOVEREIGN_SAML_IDP_ENTITYID = 'https://idp.example/entity'
$env:SOVEREIGN_SAML_IDP_SSO_POST = 'https://idp.example/sso'
$env:SOVEREIGN_SAML_IDP_CERT     = 'C:\certs\idp-signing.pem'
# The IdP must be able to reach the ACS, so the host needs a routable listener.
$env:SOVEREIGN_BIND               = 'http://0.0.0.0:5080'
dotnet run --project Sovereign.Host -- serve
```

Register the service provider with your IdP using `GET /saml/metadata` (ACS binding is HTTP-POST). Put a trusted HTTPS reverse proxy in front of the host; the ACS must be reachable over the public internet for the IdP to post to it.

What the validator refuses: unsigned responses, responses not signed by a configured certificate, a Reference that does not cover the Response element, digests that do not match the signed content, SHA-1 digests, an Issuer other than the configured IdP, an audience other than this service provider, expired or not-yet-valid assertions, a missing `InResponseTo`, a replayed `RelayState` (one-time nonce, 10-minute window), and any Assertion that is not a direct child of the signed Response. XML parsing disables DTDs and external entities.

Boundary: this is a SAML 2.0 SP for a single-operator service. There is no session store, no single logout, no IdP-initiated flow, and no user provisioning. Token lifetime stays capped at 15 minutes, so an active user must sign in again when it expires. The Pages site holds no secret; only the host verifies signatures.

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
| POST | `/auth/login` | `{ "username": "...", "password": "..." }` returns a token and sets the session cookie |
| GET | `/auth/status` | which sign-in methods are configured |
| POST | `/auth/logout` | clears the session cookie |
| GET | `/saml/metadata` | service-provider metadata for IdP registration |
| GET | `/saml/status` | whether single sign-on is configured, and what is missing |
| GET | `/saml/login` | begins SP-initiated login, redirects to the IdP |
| POST | `/saml/acs` | assertion consumer; returns the token on the URL fragment |

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

The Pages workflow installs dependencies, builds the static frontend and Wasm core, and publishes dist. Runtime/browser tests and native Swift tests run in separate workflows; neither blocks Pages deployment. Screenshots are uploaded as workflow artifacts. The repository does not contain credentials or model weights.

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
| Session enforcement | service serves the app only to a signed-in session | no refresh or revocation; the 15-minute token simply expires |
| SAML 2.0 single sign-on | SP-initiated login, signed-assertion verification, token exchange | no session store, SLO, IdP-initiated flow, or provisioning; requires a public ACS |
| Fine-tuning, image/video/audio models, distillation, managed evaluation, provisioned throughput | not implemented | require separate models, training code, hardware, and service infrastructure |

Cloudscape + React are the requested UI dependencies. Vite, Playwright, and LLVM are build/test tools. .NET uses only its standard/shared frameworks; AWS SDK and NuGet runtime dependencies are absent from the active projects.

Independent project by SnapKitty. AWS, Amazon Bedrock, and Cloudscape names identify compatibility/design context, not affiliation. No project license has been invented; establish the desired license before external redistribution beyond this repository.
