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
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Update;
using MongoDB.Bson;
using MongoDB.Bson.IO;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Storage;

internal class MongoUpdate(IUpdateEntry entry, WriteModel<BsonDocument> model)
{
    /// <summary>
    /// The <see cref="WriteModel{BsonDocument}"/> that contains both the document
    /// being modified and indication of the type of update being performed.
    /// </summary>
    public WriteModel<BsonDocument> Model
    {
        get => model;
    }

    /// <summary>
    /// The <see cref="IUpdateEntry"/> that is the source of this update.
    /// </summary>
    public IUpdateEntry Entry
    {
        get => entry;
    }

    /// <summary>
    /// Create a enumeration of <see cref="MongoUpdate"/> from an enumeration of EF-supplied
    /// <see cref="IUpdateEntry"/>.
    /// </summary>
    /// <param name="entries">The EF-supplied <see cref="IUpdateEntry"/> to process.</param>
    /// <returns>An enumeration of <see cref="MongoUpdate"/> that corresponds to these updates.</returns>
    public static IEnumerable<MongoUpdate> CreateAll(IEnumerable<IUpdateEntry> entries)
        => entries.Select(Create).OfType<MongoUpdate>();

    private static MongoUpdate? Create(IUpdateEntry entry)
        => entry.EntityState switch
        {
            EntityState.Added => ConvertAdded(entry),
            EntityState.Deleted => ConvertDeleted(entry),
            EntityState.Modified => ConvertModified(entry),
            EntityState.Detached => null,
            EntityState.Unchanged => null,
            _ => throw new NotSupportedException($"Unexpected entity state: {entry.EntityState}.")
        };

    private static MongoUpdate ConvertAdded(IUpdateEntry entry)
    {
        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);

        SetStoreGeneratedValues(entry);
        WriteEntity(writer, entry);

        return new MongoUpdate(entry, new InsertOneModel<BsonDocument>(document));
    }

    private static MongoUpdate ConvertDeleted(IUpdateEntry entry)
    {
        return new MongoUpdate(entry, new DeleteOneModel<BsonDocument>(CreateWhereFilter(entry)));
    }

    private static MongoUpdate ConvertModified(IUpdateEntry entry)
    {
        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);

        var whereFilter = CreateWhereFilter(entry); // Before row version incrementation
        SetStoreGeneratedValues(entry);
        WriteEntity(writer, entry);

        var updateDefinition = new BsonDocumentUpdateDefinition<BsonDocument>(new BsonDocument("$set", document));
        return new MongoUpdate(entry, new UpdateOneModel<BsonDocument>(whereFilter, updateDefinition));
    }

    private static FilterDefinition<BsonDocument> CreateWhereFilter(IUpdateEntry entry)
    {
        _ = entry.EntityType.FindPrimaryKey() ??
            throw new InvalidOperationException($"Cannot find the primary key for the entity: {entry.EntityType.Name}");

        var document = new BsonDocument();
        using var writer = new BsonDocumentWriter(document);

        writer.WriteStartDocument();
        WriteKeyProperties(writer, entry);
        WriteConcurrencyTokens(writer, entry);
        writer.WriteEndDocument();

        return writer.Document.Elements
            .Select(element => Builders<BsonDocument>.Filter.Eq(element.Name, element.Value))
            .Aggregate<FilterDefinition<BsonDocument>?, FilterDefinition<BsonDocument>?>(null, (current, nextFilter)
                => current == null
                    ? nextFilter
                    : Builders<BsonDocument>.Filter.And(current, nextFilter))!;
    }

    private static void WriteConcurrencyTokens(IBsonWriter writer, IUpdateEntry entry)
    {
        var concurrencyTokens = entry.EntityType.GetProperties().Where(p => p.IsConcurrencyToken).ToArray();
        foreach (var property in concurrencyTokens)
        {
            WriteProperty(writer, entry.GetOriginalValue(property), property);
        }
    }

    private static void WriteEntity(IBsonWriter writer, IUpdateEntry entry, Func<IProperty, bool>? propertyFilter = null)
    {
        if (propertyFilter == null && entry.EntityState == EntityState.Modified)
        {
            propertyFilter = entry.IsModified;
        }

        writer.WriteStartDocument();
        WriteKeyProperties(writer, entry);
        WriteNonKeyProperties(writer, entry, propertyFilter);
        WriteOwnedEntities(writer, entry);
        writer.WriteEndDocument();
    }

    private static IProperty? FindOrdinalKeyProperty(IEntityType entityType)
        => entityType.FindPrimaryKey()!.Properties.FirstOrDefault(p => p.GetElementName().Length == 0 && p.IsOwnedTypeOrdinalKey());

    private static void SetStoreGeneratedValues(IUpdateEntry entry)
    {
        foreach (var property in entry.EntityType.GetProperties())
        {
            if (entry.HasTemporaryValue(property))
            {
                entry.SetStoreGeneratedValue(property, entry.GetCurrentValue(property));
            }

            if (property.IsRowVersion())
            {
                entry.SetStoreGeneratedValue(property, entry.GetRowVersion(property));
            }
        }
    }

    private static void WriteKeyProperties(IBsonWriter writer, IUpdateEntry entry)
    {
        var keyProperties = entry.EntityType.FindPrimaryKey()?
            .Properties
            .Where(p => !p.IsOwnedTypeKey()).ToArray() ?? [];

        if (!keyProperties.Any()) return;

        var compositeKey = keyProperties.Length > 1;
        if (compositeKey)
        {
            writer.WriteName("_id");
            writer.WriteStartDocument();
        }

        foreach (var property in keyProperties)
        {
            WriteProperty(writer, entry.GetCurrentValue(property), property);
        }

        if (compositeKey)
        {
            writer.WriteEndDocument();
        }
    }

    internal static void WriteNonKeyProperties(IBsonWriter writer, IUpdateEntry entry, Func<IProperty, bool>? propertyFilter = null)
    {
        var properties = entry.EntityType.GetProperties()
            .Where(p => !p.IsPrimaryKey() && p.GetElementName() != "")
            .Where(p => propertyFilter == null || propertyFilter(p))
            .ToArray();

        foreach (var property in properties)
        {
            WriteProperty(writer, entry.GetCurrentValue(property), property);
        }
    }


    private static void WriteProperty(IBsonWriter writer, object? value, IProperty property)
    {
        var serializationInfo = BsonSerializerFactory.GetPropertySerializationInfo(property);
        writer.WriteName(serializationInfo.ElementPath?.Last() ?? serializationInfo.ElementName);
        var root = BsonSerializationContext.CreateRoot(writer);
        serializationInfo.Serializer.Serialize(root, value);
    }

    private static bool IsEmbeddedInOwner(INavigation navigation)
        => navigation is { IsOnDependent: false, ForeignKey.IsOwnership: true }
           && !navigation.ForeignKey.DeclaringEntityType.IsDocumentRoot();

    private static void WriteOwnedEntities(IBsonWriter writer, IUpdateEntry entry)
    {
        foreach (var navigation in entry.EntityType.GetNavigations().Where(IsEmbeddedInOwner))
        {
            if (navigation.IsCollection)
            {
                WriteOwnedEntityCollection(writer, entry, navigation);
            }
            else
            {
                WriteOwnedEntity(writer, entry, navigation);
            }
        }
    }

    private static void WriteOwnedEntity(IBsonWriter writer, IUpdateEntry entry, INavigation navigation)
    {
        writer.WriteName(navigation.TargetEntityType.GetContainingElementName());

        var value = entry.GetCurrentValue(navigation);
        if (value == null)
        {
            writer.WriteNull();
        }
        else
        {
            var stateManager = ((InternalEntityEntry)entry).StateManager;
            var ownedEntry = stateManager.TryGetEntry(value, navigation.ForeignKey.DeclaringEntityType)!;
            WriteEntity(writer, ownedEntry, _ => true);
        }
    }

    private static void WriteOwnedEntityCollection(IBsonWriter writer, IUpdateEntry entry, INavigation navigation)
    {
        var value = entry.GetCurrentValue(navigation);

        if (value is null or ICollection<object> { Count: 0 })
        {
            // TODO: Can't get original value for a nav
            var originalValue = entry.GetOriginalValue(navigation);
            if (value == originalValue) return;

            writer.WriteName(navigation.TargetEntityType.GetContainingElementName());
            if (value == null)
            {
                writer.WriteNull();
            }
            else
            {
                writer.WriteStartArray();
                writer.WriteEndArray();
            }

            return;
        }

        var stateManager = ((InternalEntityEntry)entry).StateManager;

        var childEntries = new List<InternalEntityEntry>();
        foreach (var dependent in (IEnumerable)value)
        {
            childEntries.Add(stateManager.TryGetEntry(dependent, navigation.ForeignKey.DeclaringEntityType)!);
        }

        SetTemporaryOrdinals(childEntries, navigation);

        var isCollectionChanged = false;
        var ordinal = 1;
        foreach (var childEntry in childEntries)
        {
            // Owned entities have a synthetic key based on order, apply that here
            var ordinalKeyProperty = FindOrdinalKeyProperty(childEntry.EntityType);
            if (ordinalKeyProperty != null && childEntry.HasTemporaryValue(ordinalKeyProperty))
            {
                childEntry.SetStoreGeneratedValue(ordinalKeyProperty, ordinal);
            }

            isCollectionChanged = isCollectionChanged || childEntry.EntityState != EntityState.Unchanged;
            ordinal++;
        }

        if (!isCollectionChanged) return;

        writer.WriteName(navigation.TargetEntityType.GetContainingElementName());
        writer.WriteStartArray();
        foreach (var embeddedEntry in childEntries)
        {
            WriteEntity(writer, embeddedEntry, _ => true);
        }

        writer.WriteEndArray();
    }

    private static void SetTemporaryOrdinals(IList<InternalEntityEntry> childEntities, INavigation navigation)
    {
        var ordinalKeyProperty = FindOrdinalKeyProperty(navigation.ForeignKey.DeclaringEntityType);
        if (ordinalKeyProperty == null) return;

        var shouldSetTemporaryKeys = false;
        var embeddedOrdinal = 1;

        foreach (var childEntity in childEntities)
        {
            if ((int)childEntity.GetCurrentValue(ordinalKeyProperty)! != embeddedOrdinal
                && !childEntity.HasTemporaryValue(ordinalKeyProperty))
            {
                // We have old persisted ordinals that are no longer valid
                // Set temporary ones to avoid key conflicts when creating new
                // non-temporary keys.
                shouldSetTemporaryKeys = true;
                break;
            }

            embeddedOrdinal++;
        }

        if (!shouldSetTemporaryKeys)  return;

        var temporaryOrdinal = -1;
        foreach (var childEntity in childEntities)
        {
            childEntity.SetTemporaryValue(ordinalKeyProperty, temporaryOrdinal, setModified: false);
            temporaryOrdinal--;
        }
    }
}
