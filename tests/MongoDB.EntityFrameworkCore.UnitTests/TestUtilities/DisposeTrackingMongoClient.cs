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
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.Driver.Core.Clusters;

namespace MongoDB.EntityFrameworkCore.UnitTests.TestUtilities;

// An IMongoClient test double that does nothing except count Dispose() calls.
// Every operational member throws; only lifecycle is observable.
internal sealed class DisposeTrackingMongoClient : IMongoClient
{
    public int DisposeCount { get; private set; }

    public void Dispose() => DisposeCount++;

    private static NotSupportedException Unsupported() => new("Operational members are not supported on the test double.");

    public ICluster Cluster => throw Unsupported();
    public MongoClientSettings Settings => throw Unsupported();

    public ClientBulkWriteResult BulkWrite(IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions? options = null, CancellationToken cancellationToken = default) => throw Unsupported();
    public ClientBulkWriteResult BulkWrite(IClientSessionHandle session, IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions? options = null, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<ClientBulkWriteResult> BulkWriteAsync(IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions? options = null, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<ClientBulkWriteResult> BulkWriteAsync(IClientSessionHandle session, IReadOnlyList<BulkWriteModel> models, ClientBulkWriteOptions? options = null, CancellationToken cancellationToken = default) => throw Unsupported();

    public void DropDatabase(string name, CancellationToken cancellationToken = default) => throw Unsupported();
    public void DropDatabase(IClientSessionHandle session, string name, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task DropDatabaseAsync(string name, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task DropDatabaseAsync(IClientSessionHandle session, string name, CancellationToken cancellationToken = default) => throw Unsupported();

    public IMongoDatabase GetDatabase(string name, MongoDatabaseSettings? settings = null) => throw Unsupported();

    public IAsyncCursor<string> ListDatabaseNames(CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncCursor<string> ListDatabaseNames(ListDatabaseNamesOptions options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncCursor<string> ListDatabaseNames(IClientSessionHandle session, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncCursor<string> ListDatabaseNames(IClientSessionHandle session, ListDatabaseNamesOptions options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IAsyncCursor<string>> ListDatabaseNamesAsync(CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IAsyncCursor<string>> ListDatabaseNamesAsync(ListDatabaseNamesOptions options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IAsyncCursor<string>> ListDatabaseNamesAsync(IClientSessionHandle session, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IAsyncCursor<string>> ListDatabaseNamesAsync(IClientSessionHandle session, ListDatabaseNamesOptions options, CancellationToken cancellationToken = default) => throw Unsupported();

    public IAsyncCursor<BsonDocument> ListDatabases(CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncCursor<BsonDocument> ListDatabases(ListDatabasesOptions options, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncCursor<BsonDocument> ListDatabases(IClientSessionHandle session, CancellationToken cancellationToken = default) => throw Unsupported();
    public IAsyncCursor<BsonDocument> ListDatabases(IClientSessionHandle session, ListDatabasesOptions options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(ListDatabasesOptions options, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(IClientSessionHandle session, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IAsyncCursor<BsonDocument>> ListDatabasesAsync(IClientSessionHandle session, ListDatabasesOptions options, CancellationToken cancellationToken = default) => throw Unsupported();

    public IClientSessionHandle StartSession(ClientSessionOptions? options = null, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IClientSessionHandle> StartSessionAsync(ClientSessionOptions? options = null, CancellationToken cancellationToken = default) => throw Unsupported();

    public IChangeStreamCursor<TResult> Watch<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw Unsupported();
    public IChangeStreamCursor<TResult> Watch<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw Unsupported();
    public Task<IChangeStreamCursor<TResult>> WatchAsync<TResult>(IClientSessionHandle session, PipelineDefinition<ChangeStreamDocument<BsonDocument>, TResult> pipeline, ChangeStreamOptions? options = null, CancellationToken cancellationToken = default) => throw Unsupported();

    public IMongoClient WithReadConcern(ReadConcern readConcern) => throw Unsupported();
    public IMongoClient WithReadPreference(ReadPreference readPreference) => throw Unsupported();
    public IMongoClient WithWriteConcern(WriteConcern writeConcern) => throw Unsupported();
}
