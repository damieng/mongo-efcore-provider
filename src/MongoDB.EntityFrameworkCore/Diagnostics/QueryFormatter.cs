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
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.IO;
using MongoDB.Bson;
using MongoDB.Bson.IO;

namespace MongoDB.EntityFrameworkCore.Diagnostics;

/// <summary>
/// Formats a <see cref="BsonDocument"/> to JSON for logging.
/// </summary>
public static class QueryFormatter
{
    private static readonly RecyclableMemoryStreamManager MemoryStreamManager = new();

    private static readonly JsonWriterOptions JsonWriterOptions = new()
    {
        Indented = true,
        SkipValidation = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Formats a <see cref="BsonDocument"/> representing a query to JSON for logging.
    /// </summary>
    /// <param name="document">The <see cref="BsonDocument"/> to format.</param>
    /// <param name="jsonOutputMode">The <see cref="JsonOutputMode"/> declaring the style of output.</param>
    /// <returns>JSON representation of the query.</returns>
    public static string ToJson(BsonDocument document, JsonOutputMode? jsonOutputMode = JsonOutputMode.Shell)
    {
        using var memoryStream = MemoryStreamManager.GetStream();
        using var writer = new Utf8JsonWriter((IBufferWriter<byte>)memoryStream, JsonWriterOptions);

        WriteBsonDocument(document, writer, jsonOutputMode ?? JsonOutputMode.Shell);
        writer.Flush();

        return Encoding.UTF8.GetString(memoryStream.GetReadOnlySequence());
    }

    /// <summary>
    /// Serializes a BsonDocument to the provided Utf8JsonWriter.
    /// </summary>
    private static void WriteBsonDocument(BsonDocument document, Utf8JsonWriter writer, JsonOutputMode jsonOutputMode)
    {
        writer.WriteStartObject();

        foreach (var element in document.Elements)
        {
            writer.WritePropertyName(element.Name);
            WriteBsonValue(element.Value, writer, jsonOutputMode);
        }

        writer.WriteEndObject();
    }

    /// <summary>
    /// Serializes a BsonValue to the provided Utf8JsonWriter.
    /// </summary>
    private static void WriteBsonValue(BsonValue value, Utf8JsonWriter writer, JsonOutputMode jsonOutputMode)
    {
        Span<char> charBuffer = stackalloc char[32];

        switch (value.BsonType)
        {
            case BsonType.Array:
                {
                    writer.WriteStartArray();
                    foreach (var item in value.AsBsonArray)
                    {
                        WriteBsonValue(item, writer, jsonOutputMode);
                    }

                    writer.WriteEndArray();
                    break;
                }

            case BsonType.Binary:
                {
                    var binaryData = value.AsBsonBinaryData;
                    writer.WriteStartObject();
                    writer.WriteStartObject(BinaryKey);

                    writer.WriteBase64String(Base64Key, binaryData.Bytes);
                    writer.WriteString("subType", ((int)binaryData.SubType).ToString("X2"));

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.Boolean:
                {
                    writer.WriteBooleanValue(value.AsBoolean);
                    break;
                }

            case BsonType.DateTime:
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName(DateKey);
                    writer.WriteStartObject();
                    writer.WriteString(NumberLongKey, (value.AsUniversalTime - Epoch).TotalMilliseconds.ToString(NumberFormatInfo.InvariantInfo));
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.Decimal128:
                {
                    writer.WriteStartObject();
                    writer.WriteString(NumberDecimalKey, value.AsDecimal128.ToString());
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.Document:
                {
                    WriteBsonDocument(value.AsBsonDocument, writer, jsonOutputMode);
                    break;
                }

            case BsonType.Double:
                {
                    writer.WriteStartObject();
                    value.AsDouble.TryFormat(charBuffer, out var charsWritten, format:"G17", provider: NumberFormatInfo.InvariantInfo);
                    writer.WriteString(NumberDoubleKey, charBuffer[..charsWritten]);
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.Int32 when jsonOutputMode == JsonOutputMode.Shell:
                {
                    writer.WriteNumberValue(value.AsInt32);
                    break;
                }

            case BsonType.Int32:
                {
                    writer.WriteStartObject();
                    value.AsInt32.TryFormat(charBuffer, out var charsWritten, provider: NumberFormatInfo.InvariantInfo);
                    writer.WriteString(NumberIntKey, charBuffer[..charsWritten]);
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.Int64:
                {
                    writer.WriteStartObject();
                    value.AsInt64.TryFormat(charBuffer, out var charsWritten, provider: NumberFormatInfo.InvariantInfo);
                    writer.WriteString(NumberLongKey, charBuffer[..charsWritten]);
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.JavaScript:
                {
                    writer.WriteStartObject();
                    writer.WriteString(CodeKey, value.AsBsonJavaScript.Code);
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.JavaScriptWithScope:
                {
                    writer.WriteStartObject();
                    writer.WriteString(CodeKey, value.AsBsonJavaScriptWithScope.Code);
                    writer.WritePropertyName(ScopeKey);
                    WriteBsonDocument(value.AsBsonJavaScriptWithScope.Scope, writer, jsonOutputMode);
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.MaxKey:
                {
                    writer.WriteStartObject();
                    writer.WriteNumber(MaxKeyKey, 1);
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.MinKey:
                {
                    writer.WriteStartObject();
                    writer.WriteNumber(MinKeyKey, 1);
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.Null:
                {
                    writer.WriteNullValue();
                    break;
                }

            case BsonType.ObjectId:
                {
                    writer.WriteStartObject();
                    writer.WriteString(ObjectIdKey, value.AsObjectId.ToString());
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.RegularExpression:
                {
                    writer.WriteStartObject();
                    writer.WritePropertyName(RegularExpressionKey);
                    writer.WriteStartObject();
                    writer.WriteString(OptionsKey, value.AsBsonRegularExpression.Options);
                    writer.WriteString(PatternKey, value.AsBsonRegularExpression.Pattern);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.String:
                {
                    writer.WriteStringValue(value.AsString);
                    break;
                }

            case BsonType.Symbol:
                {
                    writer.WriteStartObject();
                    writer.WriteString(SymbolKey, value.AsBsonSymbol.Name);
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.Timestamp:
                {
                    writer.WriteStartObject();
                    writer.WriteStartObject(TimespanKey);
                    writer.WriteNumber(TimestampKey, (uint)value.AsBsonTimestamp.Timestamp);
                    writer.WriteNumber(IncrementKey, value.AsBsonTimestamp.Increment);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.Undefined:
                {
                    writer.WriteStartObject();
                    writer.WriteBoolean(UndefinedKey, true);
                    writer.WriteEndObject();
                    break;
                }

            case BsonType.EndOfDocument:
            default:
                {
                    throw new NotSupportedException("");
                }
        }
    }

    private static readonly DateTime Epoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly JsonEncodedText Base64Key = JsonEncodedText.Encode("base64");
    private static readonly JsonEncodedText BinaryKey = JsonEncodedText.Encode("$binary");
    private static readonly JsonEncodedText CodeKey = JsonEncodedText.Encode("$code");
    private static readonly JsonEncodedText DateKey = JsonEncodedText.Encode("$date");
    private static readonly JsonEncodedText IncrementKey = JsonEncodedText.Encode("i");
    private static readonly JsonEncodedText MaxKeyKey = JsonEncodedText.Encode("$maxKey");
    private static readonly JsonEncodedText MinKeyKey = JsonEncodedText.Encode("$minKey");
    private static readonly JsonEncodedText NumberDecimalKey = JsonEncodedText.Encode("$numberDecimal");
    private static readonly JsonEncodedText NumberDoubleKey = JsonEncodedText.Encode("$numberDouble");
    private static readonly JsonEncodedText NumberIntKey = JsonEncodedText.Encode("$numberInt");
    private static readonly JsonEncodedText NumberLongKey = JsonEncodedText.Encode("$numberLong");
    private static readonly JsonEncodedText ObjectIdKey = JsonEncodedText.Encode("$oid");
    private static readonly JsonEncodedText OptionsKey = JsonEncodedText.Encode("options");
    private static readonly JsonEncodedText PatternKey = JsonEncodedText.Encode("pattern");
    private static readonly JsonEncodedText RegularExpressionKey = JsonEncodedText.Encode("$regularExpression");
    private static readonly JsonEncodedText ScopeKey = JsonEncodedText.Encode("scopeKey");
    private static readonly JsonEncodedText SymbolKey = JsonEncodedText.Encode("$symbol");
    private static readonly JsonEncodedText TimespanKey = JsonEncodedText.Encode("$timespan");
    private static readonly JsonEncodedText TimestampKey = JsonEncodedText.Encode("t");
    private static readonly JsonEncodedText UndefinedKey = JsonEncodedText.Encode("$undefined");
}
