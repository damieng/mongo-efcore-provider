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
using System.Reflection;
using Microsoft.EntityFrameworkCore.Infrastructure;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;

namespace MongoDB.EntityFrameworkCore.Serializers;

internal class DictionarySerializationProvider : BsonSerializationProviderBase
{
    /// <inheritdoc/>
    public override IBsonSerializer GetSerializer(Type type, IBsonSerializerRegistry serializerRegistry)
    {
        ArgumentNullException.ThrowIfNull(type);

        if (type is {IsGenericType: true, ContainsGenericParameters: true})
        {
            throw new ArgumentException($"Generic type '{type.ShortDisplayName()}' has unassigned generic type parameters.",
                nameof(type));
        }

        return CreateDictionarySerializer(type, serializerRegistry)
               ?? throw new ArgumentException($"No known serializer for type '{type.ShortDisplayName()}'.", nameof(type));
    }

    private IBsonSerializer? CreateDictionarySerializer(Type type, IBsonSerializerRegistry serializerRegistry)
    {
        var dictionaryInterface = type.GetGenericTypeDefinition() == typeof(IDictionary<,>)
            ? type
            : type.GetInterfaces().FirstOrDefault(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));

        if (dictionaryInterface == null) return null;

        var genericArguments = dictionaryInterface.GetTypeInfo().GetGenericArguments();
        var keyType = genericArguments[0];
        var valueType = genericArguments[1];

        var readOnlyCollectionType = typeof(IReadOnlyDictionary<,>).MakeGenericType(keyType, valueType);
        if (type == readOnlyCollectionType)
        {
            return CreateGenericSerializer(typeof(ReadOnlyDictionaryInterfaceImplementerSerializer<,,>), [type, keyType, valueType], serializerRegistry);
        }

        return CreateGenericSerializer(typeof(DictionaryInterfaceImplementerSerializer<,,>), [type, keyType, valueType], serializerRegistry);
    }

}
