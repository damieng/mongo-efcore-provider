/* Copyright 2023-present MongoDB Inc.
 *
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Infrastructure;

namespace MongoDB.EntityFrameworkCore.Storage;

/// <summary>
/// Holds a single <see cref="IMongoClient"/> per EF Core configuration so it can be shared across every
/// <see cref="MongoClientWrapper"/> (and therefore every <see cref="Microsoft.EntityFrameworkCore.DbContext"/>)
/// that resolves from the same internal service provider, aligning with the MongoDB driver's guidance to give
/// <see cref="MongoClient"/> a singleton lifetime.
/// </summary>
/// <remarks>
/// Registered as a singleton in the provider's service collection. EF Core caches its internal service provider
/// per configuration (see <c>MongoOptionsExtension.ExtensionInfo.GetServiceProviderHashCode</c>), so a single
/// instance here is automatically one client per configuration. This wrapper only serves the plain
/// connection-string / <see cref="MongoClientSettings"/> path; pre-configured clients and encryption clients are
/// handled per-scope by <see cref="MongoClientWrapper"/> and never flow through here.
/// </remarks>
internal class SingletonMongoClientWrapper : ISingletonOptions, IDisposable
{
    private readonly object _lock = new();
    private MongoOptionsExtension? _options;
    private IMongoClient? _client;

    /// <summary>
    /// The shared <see cref="IMongoClient"/>, created on first access from the resolved options.
    /// </summary>
    public virtual IMongoClient Client
    {
        get
        {
            if (_client == null)
            {
                lock (_lock)
                {
                    _client ??= CreateMongoClient(_options);
                }
            }

            return _client;
        }
    }

    /// <summary>
    /// Creates the underlying <see cref="IMongoClient"/> from the resolved options. Overridable so tests can
    /// substitute a client to observe lifecycle behavior.
    /// </summary>
    internal virtual IMongoClient CreateMongoClient(MongoOptionsExtension? options)
        => new MongoClient(MongoClientSettingsHelper.CreateSettings(options, queryableEncryptionSchema: null));

    /// <inheritdoc />
    public void Initialize(IDbContextOptions options)
        => _options = options.FindExtension<MongoOptionsExtension>();

    /// <inheritdoc />
    public void Validate(IDbContextOptions options)
    {
    }

    /// <summary>
    /// Disposes the <see cref="IMongoClient"/> this wrapper created, if any. A client supplied by the user via
    /// <c>UseMongoDB(IMongoClient, ...)</c> never flows through here, so this only ever disposes a client the
    /// provider owns. Invoked by EF Core when the internal service provider is disposed.
    /// </summary>
    public void Dispose()
    {
        lock (_lock)
        {
            (_client as IDisposable)?.Dispose();
            _client = null;
        }
    }
}
