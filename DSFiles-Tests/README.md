# dsfiles-tests

To install dependencies:

```bash
bun install
```

To run:

```bash
bun run index.ts
```

This project was created using `bun init` in bun v1.3.1. [Bun](https://bun.com) is a fast all-in-one JavaScript runtime.

## Web regression tests (Node.js)

From the repository root:

```powershell
node --test DSFiles-Tests/web.test.mjs DSFiles-Tests/chunk-upload.test.mjs DSFiles-Tests/upload-timing.test.mjs
```

These tests use isolated in-memory storage and mocked HTTP responses. They cover
legacy preference/session migration, encryption write failures, encrypted local
OAuth authorization, exports without authorization, lock/disconnect, Drive snapshot
migration, recovery errors, filename extensions and proof difficulty. They do not
upload files or contact Google, Discord or JSPaste.

Upload tests cover signed fragment receipts, resumable uploads, safe finalization
retries, cancellation, backoff and historical ETA fitting with slow outliers.
For manual browser checks using dummy files and a local simulated API:

```powershell
node DSFiles-Tests/upload-fixture.mjs
```

Open `http://127.0.0.1:49273`. A `.pdf` retries its second fragment once after a 429;
a name containing `attention` requires one manual retry; a `.zip` loses its first
final response and recovers without sending its fragments again. The fixture uses
1 MiB fragments and never contacts external storage. Stop it with Ctrl+C.

## Native compatibility tests

```powershell
dotnet run --project DSFiles-Tests/Native/DSFiles.NativeTests.csproj
```

Checks QUIC support, HTTP JSON, expiry queue persistence, encrypted JWT round trips
and SkiaSharp image encoding. Requires the platform's QUIC and Skia libraries.
On Linux, publish with `-r linux-x64 -p:NativeAot=true` to verify the AOT executable.
The tests use a temporary queue and dummy JWT keys in their own process.
