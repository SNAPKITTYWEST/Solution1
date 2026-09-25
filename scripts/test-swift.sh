#!/usr/bin/env bash
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION=v49.0.1
case "$(uname -s)" in Darwin) PLATFORM=macos ;; Linux) PLATFORM=linux ;; *) echo 'Swift host test supports macOS/Linux'; exit 1 ;; esac
case "$(uname -m)" in arm64|aarch64) ARCH=aarch64 ;; x86_64) ARCH=x86_64 ;; *) exit 1 ;; esac
NAME="wasmtime-${VERSION}-${ARCH}-${PLATFORM}-c-api"
mkdir -p "$ROOT/vendor"
if [ ! -d "$ROOT/vendor/$NAME" ]; then
  curl --fail --location --retry 3 "https://github.com/bytecodealliance/wasmtime/releases/download/$VERSION/$NAME.tar.xz" -o "$ROOT/vendor/wasmtime.tar.xz"
  tar -xJf "$ROOT/vendor/wasmtime.tar.xz" -C "$ROOT/vendor"
fi
ENGINE="$ROOT/vendor/$NAME"
export SOVEREIGN_WASM="$ROOT/public/core.wasm"
cd "$ROOT/swift"
swift test -Xcc "-I$ENGINE/include" -Xlinker "-L$ENGINE/lib" -Xlinker -rpath -Xlinker "$ENGINE/lib"
swift run -Xcc "-I$ENGINE/include" -Xlinker "-L$ENGINE/lib" -Xlinker -rpath -Xlinker "$ENGINE/lib" sovereign-swift "$SOVEREIGN_WASM"
