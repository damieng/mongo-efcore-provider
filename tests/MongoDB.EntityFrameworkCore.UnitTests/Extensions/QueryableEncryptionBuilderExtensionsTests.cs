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
using MongoDB.EntityFrameworkCore;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.UnitTests.Extensions;

public class QueryableEncryptionBuilderExtensionsTests
{
    [Fact]
    public void IsEncryptedForRange_with_precision_invokes_rangeBuilder_after_configuration()
    {
        var model = new ModelBuilder();
        var entity = model.Entity<TestEntity>();
        var dataKeyId = Guid.NewGuid();
        var propertyBuilder = entity.Property(e => e.IntProperty);

        QueryableEncryptionType? typeDuringCallback = null;
        Guid? dataKeyDuringCallback = null;
        object? minDuringCallback = null;
        object? maxDuringCallback = null;

        propertyBuilder.IsEncryptedForRange(1, 100, precision: 2, dataKeyId, _ =>
        {
            var property = propertyBuilder.Metadata;
            typeDuringCallback = property.GetQueryableEncryptionType();
            dataKeyDuringCallback = property.GetEncryptionDataKeyId();
            minDuringCallback = property.GetQueryableEncryptionRangeMin();
            maxDuringCallback = property.GetQueryableEncryptionRangeMax();
        });

        // The callback must observe the fully-configured property, matching the equality overload's ordering.
        Assert.Equal(QueryableEncryptionType.Range, typeDuringCallback);
        Assert.Equal(dataKeyId, dataKeyDuringCallback);
        Assert.Equal(1, minDuringCallback);
        Assert.Equal(100, maxDuringCallback);
    }

    private class TestEntity
    {
        public int IntProperty { get; set; }
    }
}
