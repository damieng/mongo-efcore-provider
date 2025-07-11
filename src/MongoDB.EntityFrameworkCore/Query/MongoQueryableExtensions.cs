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
using System.Linq;
using System.Linq.Expressions;
using MongoDB.Bson;
using MongoDB.Driver;

// ReSharper disable once CheckNamespace
namespace MongoDB.EntityFrameworkCore;

/// <summary>
/// Extensions to <see cref="IQueryable{T}"/> for the MongoDB EF Core Provider.
/// </summary>
/// <remarks>
/// Some of these are duplicates of what is exposed in the MongoDB C# Driver extensions. They are exposed here
/// to avoid conflicts with the Async overloads present in the MongoDB C# Driver extensions/namespace versions
/// that conflict with EF Core.
/// </remarks>
public static class MongoQueryableExtensions
{
    private static readonly BsonDocument AddScoreField =
        new("$addFields", new BsonDocument {{"score", new BsonDocument("$meta", "vectorSearchScore")}});

    /// <summary>
    /// Appends a $vectorSearch stage to an <see cref="IQueryable{T}"/> LINQ pipeline.
    /// </summary>
    /// <typeparam name="TSource">The type of the elements of <paramref name="source" />.</typeparam>
    /// <typeparam name="TField">The type of the vector property to search.</typeparam>
    /// <param name="source">The <see cref="IQueryable{T}"/> LINQ pipeline to append to.</param>
    /// <param name="field">The property containing the vectors in the source.</param>
    /// <param name="queryVector">The vector to search with - typically an array of floats.</param>
    /// <param name="limit">The number of items to limit the vector search to.</param>
    /// <param name="options">An optional <see cref="VectorSearchOptions{TDocument}"/> containing additional filters, index names etc.</param>
    /// <returns>
    /// The <see cref="IQueryable{T}"/> with the $vectorSearch stage appended.
    /// </returns>
    public static IQueryable<TSource> VectorSearch<TSource, TField>(
        this IQueryable<TSource> source,
        Expression<Func<TSource, TField>> field,
        QueryVector queryVector,
        int limit,
        VectorSearchOptions<TSource>? options = null)
    {
        var vectorSource = Driver.Linq.MongoQueryable.AppendStage(source, PipelineStageDefinitionBuilder.VectorSearch(field, queryVector, limit, options));
        var scoredVectorSource = Driver.Linq.MongoQueryable.AppendStage<TSource, TSource>(vectorSource,AddScoreField);
        return scoredVectorSource;
    }
}
