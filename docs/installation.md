# Installation

## Prerequisites

| Side | Required | Notes |
|---|---|---|
| .NET | SDK 8 or 10. The 4 libraries multi-target **net8.0 + net10.0** (108/108 unit tests pass on both TFMs; a fresh net8.0 consumer was built and run against the packed nupkgs). SampleApi + integration tests are net10.0 | No workloads |
| Node | 18+ | Validated on Node 18 + npm 10 |
| Angular consumer | 17 / 18 / 19 (declared peer range; build + strictTemplates compile verified on 18 only) | Standalone components, no NgModule |

## .NET — install packages (or project-reference the source)

Core library needs **zero third-party NuGet** (shared framework only):

| Package | Contains | Required? |
|---|---|---|
| `Pixous.AI.Abstractions` | Interfaces, DTOs, `IConversationStore` | Always (transitively) |
| `Pixous.AI.Core` | Services, options, selection, in-memory store | Always |
| `Pixous.AI.Providers` | All built-in providers + retry policy | Always (or subset later) |
| `Pixous.AI.AspNetCore` | `AddAiSuite`, `/api/ai` endpoints | For API hosts |

```bash
# While evolving: project references (what SampleApi does)
dotnet add <YourApi>.csproj reference <path>/src/Pixous.AI.AspNetCore/Pixous.AI.AspNetCore.csproj

# When stable: pack once, push to your private feed
dotnet pack dotnet/Pixous.AI.slnx -c Release
dotnet nuget push "dotnet/src/*/bin/Release/*.nupkg" --source <your-feed>
```

## Verify the build

```bash
cd dotnet
dotnet build Pixous.AI.slnx
dotnet test Pixous.AI.slnx        # 76 unit + 24 integration (WebApplicationFactory, no keys)
dotnet run --project samples/SampleApi --urls http://localhost:5199
# open http://localhost:5199/demo.html
```

## Angular — install the library

Required NPM: `@ngx-translate/core` (runtime UI translation; a dependency of
the library). Peers (your app provides): `@angular/common,core,forms` ^17–^19, `rxjs` ^7.8.
Streaming uses native `fetch` — no polyfills.

```bash
npm i @pixous/ai-suite @ngx-translate/core @ngx-translate/http-loader
```

Typecheck the library itself: `cd angular/pixous-ai-suite && npm install && npm run typecheck`.
