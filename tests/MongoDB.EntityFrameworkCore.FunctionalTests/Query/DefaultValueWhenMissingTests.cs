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
using MongoDB.EntityFrameworkCore.FunctionalTests.Entities.Guides;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection(nameof(ReadOnlySampleGuidesFixture))]
public sealed class DefaultValueWhenMissingTests(ReadOnlySampleGuidesFixture database)
    : IDisposable, IAsyncDisposable
{
    private readonly IMongoDatabase _mongoDatabase = database.MongoDatabase;
    private readonly GuidesDbContext _db = GuidesDbContext.Create(database.MongoDatabase);

    class ExtendedPlanet<T> : Planet
    {
        public T value { get; set; }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void Missing_int_defaults_when_missing(int defaultValue)
        => Missing_defaults_when_missing(defaultValue);

    [Theory]
    [InlineData(0)]
    [InlineData(long.MaxValue)]
    [InlineData(long.MinValue)]
    public void Missing_long_defaults_when_missing(long defaultValue)
        => Missing_defaults_when_missing(defaultValue);

    [Theory]
    [InlineData(0)]
    [InlineData(float.MaxValue)]
    [InlineData(float.MinValue)]
    public void Missing_float_defaults_when_missing(float defaultValue)
        => Missing_defaults_when_missing(defaultValue);

    [Theory]
    [InlineData(0)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public void Missing_double_defaults_when_missing(double defaultValue)
        => Missing_defaults_when_missing(defaultValue);

    [Theory]
    [InlineData(null)]
    [InlineData(0d)]
    [InlineData(double.MaxValue)]
    [InlineData(double.MinValue)]
    public void Missing_nullable_double_defaults_when_missing(double? defaultValue)
        => Missing_defaults_when_missing(defaultValue);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("N/A")]
    public void Missing_string_defaults_when_missing(string? defaultValue)
        => Missing_defaults_when_missing(defaultValue);

    [Theory]
    [InlineData(new int[0])]
    [InlineData(new[] {1})]
    [InlineData(new[] {1, 10, 1000, -10000})]
    public void Missing_int_array_defaults_when_missing(int[] defaultValue)
        => Missing_defaults_when_missing(defaultValue);

    [Fact]
    public void Missing_nullable_string_array_defaults_when_missing_to_null()
        => Missing_defaults_when_missing<string[]?>(null);

    [Fact]
    public void Missing_nullable_string_array_defaults_when_missing_to_empty_array()
        => Missing_defaults_when_missing<string[]?>([]);

    [Fact]
    public void Missing_nullable_string_array_defaults_when_missing_to_string_array()
        => Missing_defaults_when_missing<string[]?>(["a", "b", "c"]);

    private void Missing_defaults_when_missing<T>(T defaultValue)
    {
        var collection = _mongoDatabase.GetCollection<ExtendedPlanet<T>>("planets");
        using var db = SingleEntityDbContext.Create(collection,
            mb => mb.Entity<ExtendedPlanet<T>>().Property(e => e.value).HasDefaultValueWhenMissing(defaultValue));

        var actual = db.Entities.First();
        Assert.Equal(defaultValue, actual.value);
    }

    public void Dispose()
    {
        _db.Dispose();
        database.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        await _db.DisposeAsync();
        await database.DisposeAsync();
    }
}
