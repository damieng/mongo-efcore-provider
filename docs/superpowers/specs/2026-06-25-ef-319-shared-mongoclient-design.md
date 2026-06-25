# EF-319 — Share a single MongoClient per configuration

**Status:** Approved (design)
**Ticket:** [EF-319](https://jira.mongodb.org/browse/EF-319) — *Consider caching a single MongoClient per configuration (align with driver singleton-lifetime guidance)*
**Type:** Improvement

## Problem

The MongoDB C# driver documents that `MongoClient` should be given a **singleton lifetime** — a single instance reused across the application. The EF provider violates this: `IMongoClientWrapper` is registered **scoped** (`MongoServiceCollectionExtensions.AddEntityFrameworkMongoDB`), so every `DbContext` instance gets its own `MongoClientWrapper`, and for the plain connection-string / `MongoClientSettings` path that wrapper calls `new MongoClient(settings)` (`MongoClientWrapper.GetOrCreateMongoClient`). Result: one `MongoClient` **object per `DbContext` instance**.

The driver's `ClusterRegistry` pools the underlying connections regardless, so this is not a connection-pool explosion — but client object identity is fragmented, contradicting documented guidance and diverging from the EF Cosmos provider, which already solves this with `SingletonCosmosClientWrapper`.

### Current behavior (proven by test)

`MongoClientSharingTests` (already in the branch) demonstrates the three paths:

- connection string → **different** `MongoClient` per context (`NotSame`)
- `MongoClientSettings` → **different** `MongoClient` per context (`NotSame`)
- pre-configured `IMongoClient` → **same** instance (`Same`)

## Goal

Reuse a single `MongoClient` per configuration for the provider-created (plain connection-string / `MongoClientSettings`) path, while preserving the two paths that must not change:

- **Pre-configured `IMongoClient`** — user owns the lifetime; the provider only borrows it.
- **Encryption** (CSFLE / Queryable Encryption) — the client carries model-derived `AutoEncryptionOptions` and stays per-scope.

## Why a plain singleton is sufficient

EF already caches its **internal service provider per configuration**:

- `MongoOptionsExtension.ExtensionInfo.GetServiceProviderHashCode()` = `HashCode.Combine(ConnectionString, DatabaseName)`.
- `ShouldUseSameServiceProvider(...)` additionally compares `ConnectionString`, `MongoClient`, `ClientSettings`, and `DatabaseName`.

Two contexts with the same configuration therefore share the same internal service provider; two with different configurations get different providers. A service registered as a **singleton within that provider is automatically "one instance per configuration"** — no manual config-keyed dictionary is required. This is exactly the mechanism EF Cosmos relies on for `SingletonCosmosClientWrapper`.

Consequence worth noting: configurations differing only by `DatabaseName` (same connection string) get separate internal providers and therefore separate singleton clients. This is acceptable — the driver's `ClusterRegistry` still pools the underlying cluster — and keeping the singleton keyed by the existing provider granularity avoids inventing a parallel keying scheme.

## Design

### New service: `SingletonMongoClientWrapper` (internal)

- Registered via `TryAddSingleton` inside `AddEntityFrameworkMongoDB`'s provider-specific block.
- Constructor dependency: `IDbContextOptions` (to read the `MongoOptionsExtension`). It does **not** depend on `IQueryableEncryptionSchemaProvider` — that is scoped, and the encryption path does not flow through this singleton.
- Exposes a lazily-created, cached `IMongoClient Client`, built under a lock:
  - settings via `MongoClientSettingsHelper.CreateSettings(options, queryableEncryptionSchema: null)`,
  - client via `new MongoClient(settings)`.
- Implements `IDisposable`: disposes the `MongoClient` it created when EF's internal service provider is torn down (EF disposes singleton services that implement `IDisposable`). `IMongoClient`/`MongoClient` implement `IDisposable` as of driver 3.x (confirmed against 3.9.0). The wrapper only ever disposes a client **it** constructed.

### Changed: `MongoClientWrapper` (stays scoped)

`SingletonMongoClientWrapper` is injected. `GetOrCreateMongoClient` decision tree becomes:

1. **Pre-configured `IMongoClient`** (`serviceProvider.GetService<IMongoClient>()` or `options.MongoClient`) → return it as today, including the existing "cannot activate encryption with a pre-configured MongoClient" guard. Never disposed by the provider.
2. **`createOwnMongoClient`** (queryable-encryption schema applies, or `MongoClientSettingsHelper.HasMongoClientOptions(options)`) → build a per-scope client exactly as today (with the model-derived schema). Not shared, not disposed by the singleton.
3. **Plain connection-string / `ClientSettings`** → return `_singletonClientWrapper.Client` (the shared instance).

Database-name resolution (`_databaseName`, parsed from the connection string when not set explicitly) stays in the scoped `MongoClientWrapper` — it is per-configuration and inexpensive, and the singleton has no need for it.

### Multi-EF compatibility

Pure DI + runtime change using APIs common to EF8/EF9/EF10. No version-conditional (`#if`) code required; the change compiles identically across all three configurations.

## Breaking-change assessment

- `IMongoClientWrapper` interface, signatures, and visibility are unchanged.
- **Observable behavior change:** `IMongoClientWrapper.Client` now returns a shared instance across `DbContext`s that share a configuration (object identity changes for the plain path). This is the intended improvement, not a document-shape or signature break, but it is observable — add a note to `BREAKING-CHANGES.md`.
- New `SingletonMongoClientWrapper` is `internal` → not part of the public surface.

## Testing

Update `MongoClientSharingTests` and add coverage:

- **Plain connection string** → two contexts now return the **same** client (`Same`) — was `NotSame`.
- **`MongoClientSettings`** → two contexts now return the **same** client (`Same`) — was `NotSame`.
- **Pre-configured `IMongoClient`** → still the same borrowed instance (`Same`, unchanged).
- **Different configurations** (different connection strings) → different clients (`NotSame`).
- **Encryption path** → client is built per-scope and is **not** the singleton instance (guards against accidentally routing encryption through the shared client). Gated on encryption test prerequisites where required.
- **Disposal** → disposing the internal service provider disposes a provider-created client but does **not** dispose a pre-configured (user-supplied) client.

Unit tests live in `tests/MongoDB.EntityFrameworkCore.UnitTests/Storage/`. Client construction does not open a connection, so the sharing/identity tests run without a live server.

## Out of scope

- Sharing or pooling the **encryption** client (kept per-scope by design; possible future follow-up).
- Any change to the pre-configured `IMongoClient` lifecycle (still user-owned).
- A configuration-keyed global registry independent of EF's service-provider cache (rejected — adds lifetime/eviction complexity for no benefit over the per-provider singleton).
