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
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Internal;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.SpecificationTests.Metadata;

public static class MongoBuilderExtensionTest
{
    [Fact]
    public static void Can_set_collection_name()
    {
        var typeBuilder = CreateBuilder().Entity(typeof(SampleEntity), ConfigurationSource.Convention)!;

        Assert.NotNull(typeBuilder.ToCollection("First"));
        Assert.Equal("First", typeBuilder.Metadata.GetCollectionName());

        Assert.NotNull(typeBuilder.ToCollection("Second", fromDataAnnotation: true));
        Assert.Equal("Second", typeBuilder.Metadata.GetCollectionName());

        Assert.Null(typeBuilder.ToCollection("Third"));
        Assert.Equal("Second", typeBuilder.Metadata.GetCollectionName());
    }

    [Fact]
    public static void Can_set_element_name()
    {
        var typeBuilder = CreateBuilder().Entity(typeof(SampleEntity), ConfigurationSource.Convention)!;
        var propertyBuilder = typeBuilder.Property(typeof(string), "SampleString", ConfigurationSource.Convention)!;

        Assert.NotNull(propertyBuilder.HasElementName("First"));
        Assert.Equal("First", propertyBuilder.Metadata.GetElementName());

        Assert.NotNull(propertyBuilder.HasElementName("Second", fromDataAnnotation: true));
        Assert.Equal("Second", propertyBuilder.Metadata.GetElementName());

        Assert.Null(propertyBuilder.HasElementName("Third"));
        Assert.Equal("Second", propertyBuilder.Metadata.GetElementName());
    }

    [Fact]
    public static void Can_set_binary_vector_data_type()
    {
        var typeBuilder = CreateBuilder().Entity(typeof(SampleEntity), ConfigurationSource.Convention)!;
        var propertyBuilder = typeBuilder.Property(typeof(byte[]), "SampleVector", ConfigurationSource.Convention)!;

        Assert.NotNull(propertyBuilder.HasBinaryVectorDataType(BinaryVectorDataType.Int8));
        Assert.Equal(BinaryVectorDataType.Int8, propertyBuilder.Metadata.GetBinaryVectorDataType());

        Assert.NotNull(propertyBuilder.HasBinaryVectorDataType(BinaryVectorDataType.Float32, fromDataAnnotation: true));
        Assert.Equal(BinaryVectorDataType.Float32, propertyBuilder.Metadata.GetBinaryVectorDataType());

        // A lower-priority convention cannot override the value set by a data annotation...
        Assert.Null(propertyBuilder.HasBinaryVectorDataType(BinaryVectorDataType.PackedBit));
        Assert.Equal(BinaryVectorDataType.Float32, propertyBuilder.Metadata.GetBinaryVectorDataType());

        // ...but it can re-assert the same value. This exercises the value-equality short-circuit
        // in CanSetAnnotation, which only works when the value is forwarded (the bug passed the
        // fromDataAnnotation bool in its place, so the data type value was lost).
        Assert.True(propertyBuilder.CanSetBinaryVectorDataType(BinaryVectorDataType.Float32));
        Assert.NotNull(propertyBuilder.HasBinaryVectorDataType(BinaryVectorDataType.Float32));
        Assert.Equal(BinaryVectorDataType.Float32, propertyBuilder.Metadata.GetBinaryVectorDataType());
    }

    private static ModelBuilder CreateConventionModelBuilder()
        => MongoTestHelpers.Instance.CreateConventionBuilder();

    private static InternalModelBuilder CreateBuilder()
        => (InternalModelBuilder)CreateConventionModelBuilder().GetInfrastructure();

    private class SampleEntity
    {
        public string SampleString { get; set; }

        public byte[] SampleVector { get; set; }
    }
}
