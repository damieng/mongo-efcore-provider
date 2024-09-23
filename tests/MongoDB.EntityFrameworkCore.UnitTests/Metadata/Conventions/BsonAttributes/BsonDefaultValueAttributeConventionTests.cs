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
using Microsoft.EntityFrameworkCore.Diagnostics;
using MongoDB.Bson.Serialization.Attributes;

namespace MongoDB.EntityFrameworkCore.UnitTests.Metadata.Conventions.BsonAttributes;

public static class BsonDefaultValueAttributeConventionTests
{
    [Fact]
    public static void BsonDefaultValue_specified_properties_are_of_a_specified_kind()
    {
        using var db = new BaseDbContext();

        var location = db.GetProperty((DefaultsEntity d) => d.Location)!;
        var count = db.GetProperty((DefaultsEntity d) => d.Count)!;
        var ratings = db.GetProperty((DefaultsEntity d) => d.Ratings)!;
        var summary = db.GetProperty((DefaultsEntity d) => d.Summary)!;

        Assert.Equal("TBD", location.GetDefaultValueWhenMissing());
        Assert.Equal(1, count.GetDefaultValueWhenMissing());
        Assert.Equal(new long[] {1, 2, 3}, ratings.GetDefaultValueWhenMissing());
        Assert.Null(summary.GetDefaultValueWhenMissing());
    }

    [Fact]
    public static void ModelBuilder_specified_kind_override_BsonDefaultValue_attribute()
    {
        using var db = new ModelBuilderSpecifiedDbContext();

        var location = db.GetProperty((DefaultsEntity d) => d.Location)!;
        var count = db.GetProperty((DefaultsEntity d) => d.Count)!;
        var ratings = db.GetProperty((DefaultsEntity d) => d.Ratings)!;
        var summary = db.GetProperty((DefaultsEntity d) => d.Summary)!;

        Assert.Equal("N/A", location.GetDefaultValueWhenMissing());
        Assert.Equal(100, count.GetDefaultValueWhenMissing());
        Assert.Equal(new long[] {5, 4, 3}, ratings.GetDefaultValueWhenMissing());
        Assert.Equal("To be completed", summary.GetDefaultValueWhenMissing());
    }

    class DefaultsEntity
    {
        public int Id { get; set; }

        [BsonDefaultValue("TBD")]
        public string Location { get; set; }

        [BsonDefaultValue(defaultValue: 1)]
        public int Count { get; set; }

        [BsonDefaultValue(new long[] {1, 2, 3})]
        public long[]? Ratings { get; set; }

        [BsonDefaultValue(null)]
        public string Summary { get; set; }
    }

    class BaseDbContext : DbContext
    {
        public DbSet<DefaultsEntity> Entities { get; set; }

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseMongoDB("mongodb://localhost:27017", "UnitTests")
                .ConfigureWarnings(x => x.Ignore(CoreEventId.ManyServiceProvidersCreatedWarning));
    }

    class ModelBuilderSpecifiedDbContext : BaseDbContext
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<DefaultsEntity>(e =>
            {
                e.Property(p => p.Location).HasDefaultValueWhenMissing("N/A");
                e.Property(p => p.Count).HasDefaultValueWhenMissing(100);
                e.Property(p => p.Ratings).HasDefaultValueWhenMissing(new long[] {5, 4, 3});
                e.Property(p => p.Summary).HasDefaultValueWhenMissing("To be completed");
            });
        }
    }
}
