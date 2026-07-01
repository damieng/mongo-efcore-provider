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
using System.Linq;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Microsoft.EntityFrameworkCore.Metadata.Conventions.Infrastructure;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Metadata.Conventions;

/// <summary>
/// Marks scalar array/<see cref="List{T}"/> properties (e.g. <c>string[]</c>, <c>List&lt;int&gt;</c>) as
/// EF Core "primitive collections" by setting their <see cref="IReadOnlyProperty.GetElementType"/>. EF
/// Core's query pipeline (e.g. <c>Queryable.SelectMany</c> nav-expansion) only recognizes a collection
/// property as expandable when this metadata is present; without it, EF throws before the query ever
/// reaches the provider's translators.
/// </summary>
/// <remarks>
/// Anything that survives to this point as an <see cref="IConventionProperty"/> (rather than an
/// <see cref="IConventionNavigation"/>) has already been classified as scalar by
/// <see cref="MongoRelationshipDiscoveryConvention"/>, so no further navigation/owned-type check is
/// needed here. Deliberately scoped to arrays and closed <see cref="List{T}"/> only — NOT the broader
/// <c>IEnumerable&lt;T&gt;</c>/<c>IReadOnlyList&lt;T&gt;</c>/<c>IList&lt;T&gt;</c> interface-typed
/// properties, which have their own existing serialization/converter handling elsewhere in the provider
/// (see <c>ClrTypeMappingTests</c>, <c>CollectionSerializationTests</c>) that primitive-collection
/// treatment conflicts with. Vector-index key properties are also excluded: a vector embedding (e.g.
/// <c>float[]</c>) is serialized as a packed binary vector, not an element-addressable array.
/// </remarks>
internal sealed class PrimitiveCollectionDiscoveryConvention : IModelFinalizingConvention
{
    /// <summary>
    /// Creates a <see cref="PrimitiveCollectionDiscoveryConvention" /> with required dependencies.
    /// </summary>
    /// <param name="dependencies">The <see cref="ProviderConventionSetBuilderDependencies"/> this convention depends upon.</param>
    public PrimitiveCollectionDiscoveryConvention(ProviderConventionSetBuilderDependencies dependencies)
    {
    }

    /// <inheritdoc/>
    public void ProcessModelFinalizing(IConventionModelBuilder modelBuilder, IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes())
        {
            foreach (var property in entityType.GetDeclaredProperties())
            {
                if (property.GetElementType() != null || IsVectorIndexKey(property))
                {
                    continue;
                }

                var elementType = TryGetPrimitiveCollectionElementType(property.ClrType);
                if (elementType != null)
                {
                    property.Builder.SetElementType(elementType, fromDataAnnotation: false);
                }
            }
        }
    }

    private static Type? TryGetPrimitiveCollectionElementType(Type clrType)
    {
        if (clrType == typeof(byte[]))
        {
            return null;
        }

        if (clrType.IsArray)
        {
            var elementType = clrType.TryGetItemType();
            return elementType == typeof(byte) ? null : elementType;
        }

        if (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(List<>))
        {
            var elementType = clrType.GetGenericArguments()[0];
            return elementType == typeof(byte) ? null : elementType;
        }

        return null;
    }

    private static bool IsVectorIndexKey(IConventionProperty property)
        => property.DeclaringType is IConventionEntityType entityType
           && entityType.GetIndexes().Any(i => i.Properties.Contains(property) && i.GetVectorIndexOptions() != null);
}
