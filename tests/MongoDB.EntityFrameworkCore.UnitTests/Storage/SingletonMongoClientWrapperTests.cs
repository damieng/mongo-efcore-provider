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

using Microsoft.EntityFrameworkCore;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.UnitTests.Storage;

public static class SingletonMongoClientWrapperTests
{
    // Lets the test substitute the created client so disposal can be observed.
    private sealed class TestSingletonMongoClientWrapper(IMongoClient client) : SingletonMongoClientWrapper
    {
        internal override IMongoClient CreateMongoClient(MongoOptionsExtension? options) => client;
    }

    [Fact]
    public static void Dispose_disposes_the_client_it_created()
    {
        var spy = new DisposeTrackingMongoClient();
        var wrapper = new TestSingletonMongoClientWrapper(spy);

        _ = wrapper.Client; // force creation

        wrapper.Dispose();

        Assert.Equal(1, spy.DisposeCount);
    }

    [Fact]
    public static void Dispose_is_idempotent()
    {
        var spy = new DisposeTrackingMongoClient();
        var wrapper = new TestSingletonMongoClientWrapper(spy);

        _ = wrapper.Client;

        wrapper.Dispose();
        wrapper.Dispose();

        Assert.Equal(1, spy.DisposeCount);
    }

    [Fact]
    public static void Dispose_without_creating_a_client_does_nothing()
    {
        var spy = new DisposeTrackingMongoClient();
        var wrapper = new TestSingletonMongoClientWrapper(spy);

        // Client was never accessed, so nothing was created and nothing should be disposed.
        wrapper.Dispose();

        Assert.Equal(0, spy.DisposeCount);
    }
}
