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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Query;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

using MqlQueryEventDefinition = EventDefinition<string, CollectionNamespace, string>;

/// <summary>
/// MongoDB-specific logging extensions.
/// </summary>
internal static class MongoLoggerExtensions
{
    internal static void ExecutedMqlQuery(
        this IDiagnosticsLogger<DbLoggerCategory.Database.Command> diagnostics,
        MongoExecutableQuery mongoExecutableQuery)
        => ExecutedMqlQuery(diagnostics, mongoExecutableQuery.CollectionNamespace, mongoExecutableQuery.Provider.LoggedStages);

    public static void ExecutedMqlQuery(
        this IDiagnosticsLogger<DbLoggerCategory.Database.Command> diagnostics,
        CollectionNamespace collectionNamespace,
        BsonDocument[]? loggedStages)
    {
        var definition = LogExecutedMqlQuery(diagnostics);

        if (diagnostics.ShouldLog(definition))
        {
            // Ideally we would always log the query and then log parameter data
            // when sensitive data is enabled. Unfortunately the LINQ provider
            // only gives us the query with full values in so we only log MQL
            // when sensitive logging is enabled.
            var mql = diagnostics.ShouldLogSensitiveData() ? LoggedStagesToMql(loggedStages) : "?";
            definition.Log(
                diagnostics,
                Environment.NewLine,
                collectionNamespace,
                mql);
        }

        if (diagnostics.NeedsEventData(definition, out var diagnosticSourceEnabled, out var simpleLogEnabled))
        {
            var eventData = new MongoQueryEventData(
                definition,
                ExecutedMqlQuery,
                collectionNamespace,
                LoggedStagesToMql(loggedStages),
                diagnostics.ShouldLogSensitiveData());

            diagnostics.DispatchEventData(definition, eventData, diagnosticSourceEnabled, simpleLogEnabled);
        }
    }


    private static string LoggedStagesToMql(BsonDocument[]? documents)
        => documents == null
            ? ""
            : string.Join(", ", documents.Select(d => QueryFormatter.ToJson(d)));

    private static string ExecutedMqlQuery(EventDefinitionBase definition, EventData payload)
    {
        var d = (MqlQueryEventDefinition)definition;
        var p = (MongoQueryEventData)payload;
        return d.GenerateMessage(
            Environment.NewLine,
            p.CollectionNamespace,
            p.LogSensitiveData ? p.QueryMql : "?");
    }

    private static MqlQueryEventDefinition LogExecutedMqlQuery(IDiagnosticsLogger logger)
    {
        var definition = ((MongoLoggingDefinitions)logger.Definitions).LogExecutedMqlQuery;
        if (definition == null)
        {
            definition = NonCapturingLazyInitializer.EnsureInitialized(
                ref ((MongoLoggingDefinitions)logger.Definitions).LogExecutedMqlQuery,
                logger,
                static logger => new MqlQueryEventDefinition(
                    logger.Options,
                    MongoEventId.ExecutedMqlQuery,
                    LogLevel.Information,
                    "MongoEventId.ExecutedMqlQuery",
                    level => LoggerMessage.Define<string, CollectionNamespace, string>(
                        level,
                        MongoEventId.ExecutedMqlQuery,
                        LogExecutedMqlQueryString)));
        }

        return (MqlQueryEventDefinition)definition;
    }

    private const string LogExecutedMqlQueryString = "Executed MQL query{newLine}{collectionNamespace}.aggregate([{queryMql}])";
}


    /// <summary>
    /// A comprehensive BsonDocument to JSON converter using Utf8JsonWriter
    /// that supports all common BSON types
    /// </summary>
    public sealed class FastBson
    {
        private static readonly JsonWriterOptions JsonWriterOptions = new()
        {
            Indented = true,
            SkipValidation = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        /// <summary>
        /// Converts a BsonDocument to a JSON string
        /// </summary>
        /// <param name="document">The BsonDocument to convert</param>
        /// <returns>JSON representation of the document</returns>
        public static string ToJson(BsonDocument document)
        {
            using var memoryStream = new MemoryStream();
            using var writer = new Utf8JsonWriter(memoryStream, JsonWriterOptions);

            WriteBsonDocument(document, writer);
            writer.Flush();

            return Encoding.UTF8.GetString(memoryStream.GetBuffer().AsSpan(0, (int)memoryStream.Length));
        }

        /// <summary>
        /// Writes a BsonDocument to the provided Utf8JsonWriter
        /// </summary>
        public static void WriteBsonDocument(BsonDocument document, Utf8JsonWriter writer)
        {
            writer.WriteStartObject();

            foreach (var element in document)
            {
                writer.WritePropertyName(element.Name);
                WriteBsonValue(element.Value, writer);
            }

            writer.WriteEndObject();
        }

        /// <summary>
        /// Writes a BsonValue to a Utf8JsonWriter with comprehensive type handling
        /// </summary>
        private static void WriteBsonValue(BsonValue value, Utf8JsonWriter writer)
        {
            switch (value.BsonType)
            {
                case BsonType.Null:
                    writer.WriteNullValue();
                    break;

                case BsonType.Boolean:
                    writer.WriteBooleanValue(value.AsBoolean);
                    break;

                case BsonType.Int32:
                    writer.WriteNumberValue(value.AsInt32);
                    break;

                case BsonType.Int64:
                    writer.WriteNumberValue(value.AsInt64);
                    break;

                case BsonType.Double:
                    writer.WriteNumberValue(value.AsDouble);
                    break;

                case BsonType.String:
                    writer.WriteStringValue(value.AsString);
                    break;

                case BsonType.Document:
                    WriteBsonDocument(value.AsBsonDocument, writer);
                    break;

                case BsonType.Array:
                    writer.WriteStartArray();
                    foreach (var item in value.AsBsonArray)
                    {
                        WriteBsonValue(item, writer);
                    }
                    writer.WriteEndArray();
                    break;

                case BsonType.ObjectId:
                    writer.WriteStartObject();
                    writer.WriteString("$oid", value.AsObjectId.ToString());
                    writer.WriteEndObject();
                    break;

                case BsonType.DateTime:
                    writer.WriteStartObject();
                    writer.WriteString("$date", value.AsUniversalTime.ToString("O", CultureInfo.InvariantCulture));
                    writer.WriteEndObject();
                    break;

                case BsonType.Binary:
                    WriteBsonBinary(value.AsBsonBinaryData, writer);
                    break;

                case BsonType.Decimal128:
                    writer.WriteStartObject();
                    writer.WriteString("$numberDecimal", value.AsDecimal128.ToString());
                    writer.WriteEndObject();
                    break;

                case BsonType.JavaScript:
                    writer.WriteStartObject();
                    writer.WriteString("$code", value.AsBsonJavaScript.Code);
                    writer.WriteEndObject();
                    break;

                case BsonType.JavaScriptWithScope:
                    writer.WriteStartObject();
                    writer.WriteString("$code", value.AsBsonJavaScriptWithScope.Code);
                    writer.WritePropertyName("$scope");
                    WriteBsonDocument(value.AsBsonJavaScriptWithScope.Scope, writer);
                    writer.WriteEndObject();
                    break;

                case BsonType.RegularExpression:
                    writer.WriteStartObject();
                    writer.WriteString("$regex", value.AsBsonRegularExpression.Pattern);
                    writer.WriteString("$options", value.AsBsonRegularExpression.Options);
                    writer.WriteEndObject();
                    break;

                case BsonType.Symbol:
                    writer.WriteStartObject();
                    writer.WriteString("$symbol", value.AsBsonSymbol.Name);
                    writer.WriteEndObject();
                    break;

                case BsonType.Timestamp:
                    writer.WriteStartObject();
                    writer.WriteStartObject("$timestamp");
                    writer.WriteNumber("t", value.AsBsonTimestamp.Timestamp);
                    writer.WriteNumber("i", value.AsBsonTimestamp.Increment);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;

                case BsonType.MaxKey:
                    writer.WriteStartObject();
                    writer.WriteNumber("$maxKey", 1);
                    writer.WriteEndObject();
                    break;

                case BsonType.MinKey:
                    writer.WriteStartObject();
                    writer.WriteNumber("$minKey", 1);
                    writer.WriteEndObject();
                    break;

                case BsonType.Undefined:
                    writer.WriteStartObject();
                    writer.WriteBoolean("$undefined", true);
                    writer.WriteEndObject();
                    break;

                default:
                    writer.WriteStringValue(value.ToString());
                    break;
            }
        }

        /// <summary>
        /// Writes a BsonBinaryData to a Utf8JsonWriter
        /// </summary>
        private static void WriteBsonBinary(BsonBinaryData binaryData, Utf8JsonWriter writer)
        {
            // Handle GUID subtypes specially
            if (binaryData.SubType == BsonBinarySubType.UuidStandard ||
                binaryData.SubType == BsonBinarySubType.UuidLegacy)
            {
                writer.WriteStartObject();
                writer.WriteString("$guid", binaryData.ToGuid().ToString("D"));
                writer.WriteEndObject();
                return;
            }

            // Handle general binary data
            writer.WriteStartObject();
            writer.WriteStartObject("$binary");

            // Convert binary to base64
            writer.WriteString("base64", Convert.ToBase64String(binaryData.Bytes));

            // Write subtype
            writer.WriteString("subType", ((int)binaryData.SubType).ToString("X2"));

            writer.WriteEndObject();
            writer.WriteEndObject();
        }
    }


