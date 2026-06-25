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

using System.Collections.Generic;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.UnitTests.Storage;

// These tests resolve IMongoClientWrapper.Client, which lazily creates a MongoClient.
// Creating a MongoClient does not open a connection, so these run without a live server.
public static class MongoClientSharingTests
{
    private class TestContext(DbContextOptions options) : DbContext(options);

    [Fact]
    public static void Connection_string_shares_a_single_MongoClient_across_DbContexts()
    {
        // Same options instance => the two contexts share EF's internal service provider.
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseMongoDB("mongodb://localhost:27017", "UnitTests")
            .Options;

        using var context1 = new TestContext(options);
        using var context2 = new TestContext(options);

        var client1 = context1.GetService<IMongoClientWrapper>().Client;
        var client2 = context2.GetService<IMongoClientWrapper>().Client;

        // A single provider-created MongoClient is shared per configuration (EF-319).
        Assert.Same(client1, client2);
    }

    [Fact]
    public static void Client_settings_share_a_single_MongoClient_across_DbContexts()
    {
        var settings = MongoClientSettings.FromConnectionString("mongodb://localhost:27017");
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseMongoDB(settings, "UnitTests")
            .Options;

        using var context1 = new TestContext(options);
        using var context2 = new TestContext(options);

        var client1 = context1.GetService<IMongoClientWrapper>().Client;
        var client2 = context2.GetService<IMongoClientWrapper>().Client;

        Assert.Same(client1, client2);
    }

    [Fact]
    public static void Preconfigured_MongoClient_is_shared_across_DbContexts()
    {
        var mongoClient = new MongoClient("mongodb://localhost:27017");
        var options = new DbContextOptionsBuilder<TestContext>()
            .UseMongoDB(mongoClient, "UnitTests")
            .Options;

        using var context1 = new TestContext(options);
        using var context2 = new TestContext(options);

        var client1 = context1.GetService<IMongoClientWrapper>().Client;
        var client2 = context2.GetService<IMongoClientWrapper>().Client;

        // A user-supplied IMongoClient is a single borrowed instance returned to every context.
        Assert.Same(mongoClient, client1);
        Assert.Same(client1, client2);
    }

    [Fact]
    public static void Different_connection_strings_get_different_MongoClients()
    {
        var optionsA = new DbContextOptionsBuilder<TestContext>()
            .UseMongoDB("mongodb://localhost:27017", "UnitTests")
            .Options;
        var optionsB = new DbContextOptionsBuilder<TestContext>()
            .UseMongoDB("mongodb://localhost:27018", "UnitTests")
            .Options;

        using var contextA = new TestContext(optionsA);
        using var contextB = new TestContext(optionsB);

        var clientA = contextA.GetService<IMongoClientWrapper>().Client;
        var clientB = contextB.GetService<IMongoClientWrapper>().Client;

        // Different configurations => different EF service providers => different clients.
        Assert.NotSame(clientA, clientB);
    }

    [Fact]
    public static void Encryption_options_create_a_separate_MongoClient_per_DbContext()
    {
        // KmsProviders set => HasMongoClientOptions is true => the wrapper must build a
        // per-scope client (the encryption path), never the shared singleton.
        var extension = new MongoOptionsExtension()
            .WithConnectionString("mongodb://localhost:27017")
            .WithDatabaseName("UnitTests")
            .WithKmsProviders(new Dictionary<string, IReadOnlyDictionary<string, object>>
            {
                ["local"] = new Dictionary<string, object> { ["key"] = new byte[96] }
            });

        var options = new DbContextOptionsBuilder<TestContext>()
            .UseMongoDB(extension)
            .Options;

        using var context1 = new TestContext(options);
        using var context2 = new TestContext(options);

        var client1 = context1.GetService<IMongoClientWrapper>().Client;
        var client2 = context2.GetService<IMongoClientWrapper>().Client;

        Assert.NotSame(client1, client2);
    }
}
