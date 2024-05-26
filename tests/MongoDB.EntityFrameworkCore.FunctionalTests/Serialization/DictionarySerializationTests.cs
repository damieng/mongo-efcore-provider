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

using MongoDB.Bson;

public class DictionarySerializationTests(TemporaryDatabaseFixture tempDatabase)
    : IClassFixture<TemporaryDatabaseFixture>
{
    class DictionaryStringValuesEntity
    {
        public ObjectId _id { get; set; }
        public Dictionary<string, string> aDictionary { get; set; }
    }

    class DictionaryIntValuesEntity
    {
        public ObjectId _id { get; set; }
        public Dictionary<string, int> aDictionary { get; set; }
    }

    class IDictionaryObjectValuesEntity
    {
        public ObjectId _id { get; set; }
        public IDictionary<string, object> aDictionary { get; set; }
    }

    [Fact]
    public void Dictionary_string_values_read_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryStringValuesEntity>();
        collection.InsertOne(new DictionaryStringValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = []});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Empty(actual.aDictionary);
    }

    [Fact]
    public void Dictionary_string_values_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryStringValuesEntity>();
        var expected = new Dictionary<string, string>
        {
            {"Season", "Summer"}, {"Temperature", "35'"}, {"Clouds", "None"}, {"Wind", "Breeze"}
        };
        var item = new DictionaryStringValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = expected};

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDictionary);
        }
    }

    [Fact]
    public void Dictionary_int_values_read_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryIntValuesEntity>();
        collection.InsertOne(new DictionaryIntValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = []});

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Empty(actual.aDictionary);
    }

    [Fact]
    public void Dictionary_int_values_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<DictionaryIntValuesEntity>();
        var expected = new Dictionary<string, int> {{"Season", 2}, {"Temperature", 35}, {"Clouds", 0}, {"Wind", 11}};
        var item = new DictionaryIntValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = expected};

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDictionary);
        }
    }

    [Fact]
    public void IDictionary_int_values_read_empty()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IDictionaryObjectValuesEntity>();
        collection.InsertOne(new IDictionaryObjectValuesEntity
        {
            _id = ObjectId.GenerateNewId(), aDictionary = new Dictionary<string, object>()
        });

        using var db = SingleEntityDbContext.Create(collection);
        var actual = db.Entities.FirstOrDefault();

        Assert.NotNull(actual);
        Assert.Empty(actual.aDictionary);
    }

    [Fact]
    public void IDictionary_int_values_write_read_with_items()
    {
        var collection = tempDatabase.CreateTemporaryCollection<IDictionaryObjectValuesEntity>();
        var expected = new Dictionary<string, object> {{"Season", 2}, {"Temperature", 35}, {"Clouds", 0}, {"Wind", 11}};
        var item = new IDictionaryObjectValuesEntity {_id = ObjectId.GenerateNewId(), aDictionary = expected};

        {
            using var db = SingleEntityDbContext.Create(collection);
            db.Entities.Add(item);
            db.SaveChanges();
        }

        {
            using var db = SingleEntityDbContext.Create(collection);
            var actual = db.Entities.FirstOrDefault();
            Assert.NotNull(actual);
            Assert.Equal(expected, actual.aDictionary);
        }
    }
}
