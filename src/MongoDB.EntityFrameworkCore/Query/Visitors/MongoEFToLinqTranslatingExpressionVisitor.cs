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
using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Bson.Serialization.Serializers;
using MongoDB.Driver;
using MongoDB.Driver.Linq;
using MongoDB.EntityFrameworkCore.Diagnostics;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Serializers;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Visits the tree resolving any query context parameter bindings and EF references so the query can be used with the MongoDB V3 LINQ provider.
/// </summary>
internal sealed class MongoEFToLinqTranslatingExpressionVisitor : System.Linq.Expressions.ExpressionVisitor
{
    private static readonly MethodInfo MqlFieldMethodInfo =
        typeof(Mql).GetMethod(nameof(Mql.Field), BindingFlags.Public | BindingFlags.Static)!;

    private readonly QueryContext _queryContext;
    private readonly Expression _source;
    private readonly BsonSerializerFactory _bsonSerializerFactory;
    private readonly IReadOnlyList<LookupExpression> _pendingLookups;
    private readonly Dictionary<IEntityType, Expression> _innerSources;
    private EntityQueryRootExpression? _foundEntityQueryRootExpression;

    internal MongoEFToLinqTranslatingExpressionVisitor(
        QueryContext queryContext,
        Expression source,
        BsonSerializerFactory bsonSerializerFactory,
        IReadOnlyList<LookupExpression>? pendingLookups = null,
        Dictionary<IEntityType, Expression>? innerSources = null)
    {
        _queryContext = queryContext;
        _source = source;
        _bsonSerializerFactory = bsonSerializerFactory;
        _pendingLookups = pendingLookups ?? Array.Empty<LookupExpression>();
        _innerSources = innerSources ?? new Dictionary<IEntityType, Expression>();
    }

    public Dictionary<string, object> AdditionalState { get; } = new();

    public MethodCallExpression Translate(
        Expression? efQueryExpression,
        ResultCardinality resultCardinality)
    {
        if (efQueryExpression == null) // No LINQ methods, e.g. Direct ToList() against DbSet
        {
            var source = AppendLookupStages(_source);
            return ApplyAsSerializer(source, BsonDocumentSerializer.Instance, typeof(BsonDocument));
        }

        // For queries with pending $lookup stages (Include), strip any Join+Select
        // that EF generated and use $lookup stages appended to the base source instead.
        // This keeps entity fields at the root of the BsonDocument.
        var expressionToTranslate = efQueryExpression;
        if (_pendingLookups.Count > 0)
        {
            expressionToTranslate = StripJoinForLookup(efQueryExpression) ?? efQueryExpression;
        }

        var query = (MethodCallExpression)Visit(expressionToTranslate)!;

        if (resultCardinality == ResultCardinality.Enumerable)
        {
            query = (MethodCallExpression)AppendLookupStages(query);
            return ApplyAsSerializer(query, BsonDocumentSerializer.Instance, typeof(BsonDocument));
        }

        // For single cardinality: append lookup to the source, then wrap with terminal op
        var sourceWithLookups = AppendLookupStages(query.Arguments[0]);
        var documentQueryableSource = ApplyAsSerializer(sourceWithLookups, BsonDocumentSerializer.Instance, typeof(BsonDocument));

        return Expression.Call(
            null,
            query.Method.GetGenericMethodDefinition().MakeGenericMethod(typeof(BsonDocument)),
            documentQueryableSource);
    }

    private static MethodCallExpression ApplyAsSerializer(
        Expression query,
        IBsonSerializer resultSerializer,
        Type resultType)
    {
        var asMethodInfo = AsMethodInfo.MakeGenericMethod(query.Type.GenericTypeArguments[0], resultType);
        var serializerExpression = Expression.Constant(resultSerializer, resultSerializer.GetType());

        return Expression.Call(
            null,
            asMethodInfo,
            query,
            serializerExpression
        );
    }

    private static bool IsAsQueryableMethod(MethodInfo method)
        => method.Name == "AsQueryable" && method.DeclaringType == typeof(Queryable);

    public override Expression? Visit(Expression? expression)
    {
        switch (expression)
        {
            // Replace materialization collection expression with the actual nav property in order for Mql.Exists etc. to work.
            case MaterializeCollectionNavigationExpression materializeCollectionNavigationExpression:
                var subQuery = Visit(materializeCollectionNavigationExpression.Subquery);
                if (subQuery is MethodCallExpression mce && IsAsQueryableMethod(mce.Method))
                {
                    return Visit(mce.Arguments[0]);
                }

                return subQuery;

#if EF8 || EF9
            // Replace the QueryContext parameter values with constant values for this execution.
            case ParameterExpression parameterExpression:
                if (parameterExpression.Name?.StartsWith(QueryCompilationContext.QueryParameterPrefix, StringComparison.Ordinal)
                    == true)
                {
                    if (_queryContext.ParameterValues.TryGetValue(parameterExpression.Name, out var value))
                    {
                        return ConvertIfRequired(Expression.Constant(value), expression.Type);
                    }
                }

                break;
#else
            case QueryParameterExpression queryParameterExpression:
                return ConvertIfRequired(Expression.Constant(_queryContext.Parameters[queryParameterExpression.Name]), expression.Type);
#endif

            // Wrap OfType<T> with As(serializer) to re-attach the custom serializer in LINQ3
            case MethodCallExpression
                {
                    Method.Name: nameof(Queryable.OfType), Method.IsGenericMethod: true, Arguments.Count: 1
                } ofTypeCall
                when ofTypeCall.Method.DeclaringType == typeof(Queryable):
                var resultType = ofTypeCall.Method.GetGenericArguments()[0];
                var resultEntityType = _queryContext.Context.Model.FindEntityType(resultType)
                                       ?? throw new NotSupportedException($"OfType type '{resultType.ShortDisplayName()
                                       }' does not map to an entity type.");
                var resultSerializer = _bsonSerializerFactory.GetEntitySerializer(resultEntityType);
                var translatedOfTypeCall = Expression.Call(null, ofTypeCall.Method, Visit(ofTypeCall.Arguments[0])!);
                return ApplyAsSerializer(translatedOfTypeCall, resultSerializer, resultType);

            // Replace object.Equals(Property(p, "propName"), ConstantExpression) elements generated by EF's Find.
            case MethodCallExpression { Method.Name: nameof(object.Equals), Object: null, Arguments.Count: 2 } methodCallExpression:
                var left = Visit(RemoveObjectConvert(methodCallExpression.Arguments[0]))!;
                var right = Visit(RemoveObjectConvert(methodCallExpression.Arguments[1]))!;
                var method = methodCallExpression.Method;

                if (left.Type == right.Type)
                {
                    return Expression.Equal(RemoveObjectConvert(left), RemoveObjectConvert(right));
                }

                var parameters = method.GetParameters();
                left = ConvertIfRequired(left, parameters[0].ParameterType);
                right = ConvertIfRequired(right, parameters[1].ParameterType);
                return Expression.Call(null, method, left, right);

            // Replace EF-generated Property(p, "propName") with Property(p.propName) or Mql.Field(p, "propName", serializer)
            case MethodCallExpression methodCallExpression
                when methodCallExpression.Method.IsEFPropertyMethod()
                     && methodCallExpression.Arguments[1] is ConstantExpression propertyNameExpression:
                var source = Visit(methodCallExpression.Arguments[0])
                             ?? throw new InvalidOperationException("Unsupported source to EF.Property expression.");

                var propertyName = propertyNameExpression.GetConstantValue<string>();
                var entityType = _queryContext.Context.Model.FindEntityType(source.Type);
                if (entityType != null)
                {
                    // Try an EF property
                    var efProperty = entityType.FindProperty(propertyName);
                    if (efProperty != null)
                    {
                        var doc = source;

                        // Composite keys need to go via the _id document
                        var isCompositeKeyAccess = efProperty.IsPrimaryKey() && entityType.FindPrimaryKey()?.Properties.Count > 1;
                        if (isCompositeKeyAccess)
                        {
                            var mqlFieldDoc = MqlFieldMethodInfo.MakeGenericMethod(source.Type, typeof(BsonValue));
                            doc = Expression.Call(null, mqlFieldDoc, source, Expression.Constant("_id"),
                                Expression.Constant(BsonValueSerializer.Instance));
                        }

                        var mqlField = MqlFieldMethodInfo.MakeGenericMethod(doc.Type, efProperty.ClrType);
                        var serializer = BsonSerializerFactory.CreateTypeSerializer(efProperty);
                        var callExpression = Expression.Call(null, mqlField, doc,
                            Expression.Constant(efProperty.GetElementName()),
                            Expression.Constant(serializer));
                        return ConvertIfRequired(callExpression, methodCallExpression.Method.ReturnType);
                    }

                    // Try an EF navigation if no property
                    var efNavigation = entityType.FindNavigation(propertyName);
                    if (efNavigation != null)
                    {
                        var elementName = efNavigation.TargetEntityType.GetContainingElementName();
                        var mqlField = MqlFieldMethodInfo.MakeGenericMethod(source.Type, efNavigation.ClrType);
                        var serializer = _bsonSerializerFactory.GetNavigationSerializer(efNavigation);
                        var callExpression = Expression.Call(null, mqlField, source,
                            Expression.Constant(elementName),
                            Expression.Constant(serializer));
                        return ConvertIfRequired(callExpression, methodCallExpression.Method.ReturnType);
                    }
                }

                // Try CLR property
                // This should not really be required but is kept here for backwards compatibility with any edge cases.
                var clrProperty = source.Type.GetProperties().FirstOrDefault(p => p.Name == propertyName);
                if (clrProperty != null)
                {
                    var propertyExpression = Expression.Property(source, clrProperty);
                    return ConvertIfRequired(propertyExpression, methodCallExpression.Method.ReturnType);
                }

                var defaultSerializer = BsonSerializer.LookupSerializer(methodCallExpression.Type);
                if (defaultSerializer != null)
                {
                    var mqlField = MqlFieldMethodInfo.MakeGenericMethod(source.Type, methodCallExpression.Type);
                    var callExpression = Expression.Call(null, mqlField, source,
                        propertyNameExpression,
                        Expression.Constant(defaultSerializer));
                    return ConvertIfRequired(callExpression, methodCallExpression.Method.ReturnType);
                }

                return VisitMethodCall(methodCallExpression);

            // Handle method call to VectorQuery
            case MethodCallExpression methodCallExpression
                when methodCallExpression.IsVectorSearch():
                return ProcessVectorSearch(methodCallExpression);

            case MethodCallExpression methodCallExpression
                when IsLeftJoinMethod(methodCallExpression):
                return RewriteLeftJoinAsGroupJoin(methodCallExpression);

            case MethodCallExpression methodCallExpression:
                return VisitMethodCall(methodCallExpression);

            // Unwrap include expressions.
            case IncludeExpression includeExpression:
                return Visit(includeExpression.EntityExpression);

            // Replace the root with the MongoDB LINQ V3 provider source.
            case EntityQueryRootExpression entityQueryRootExpression:
                if (_foundEntityQueryRootExpression == null)
                {
                    _foundEntityQueryRootExpression = entityQueryRootExpression;
                    return _source;
                }

                // Check inner sources first (handles self-joins where outer and inner are the same type)
                if (_innerSources.TryGetValue(entityQueryRootExpression.EntityType, out var innerSource))
                {
                    return innerSource;
                }

                if (_foundEntityQueryRootExpression.EntityType == entityQueryRootExpression.EntityType)
                {
                    return _source;
                }

                throw new InvalidOperationException($"Unsupported cross-DbSet query between '{_foundEntityQueryRootExpression.EntityType.Name}' " +
                                                    $"and '{entityQueryRootExpression.EntityType.Name}'. " +
                                                    "The MongoDB EF Core Provider does not support this cross-collection query. " +
                                                    "Consider using Join, Include, or restructuring your query.");
        }

        return base.Visit(expression);

        Expression ProcessVectorSearch(MethodCallExpression methodCallExpression)
        {
            var propertyExpression = methodCallExpression.Arguments[1].UnwrapLambdaFromQuote();
            var preFilterExpression = methodCallExpression.Arguments[2] is UnaryExpression
                ? methodCallExpression.Arguments[2].UnwrapLambdaFromQuote()
                : null;
            var queryVector = ParamValue<QueryVector>(3);
            var limit = ParamValue<int>(4);
            var options = ParamValue<VectorQueryOptions?>(5);

            var concreteOptions = options ?? new();

            if (concreteOptions is { NumberOfCandidates: not null, Exact: true })
            {
                throw new InvalidOperationException(
                    "The option 'Exact' is set to 'true' on a call to 'VectorQuery', indicating an exact nearest neighbour (ENN) search, and the number of candidates has also been set. Either 'NumberOfCandidates' or 'Exact' can be set, but not both.");
            }

            var members = propertyExpression.GetMemberAccess<MemberInfo>();
            var entityType = _queryContext.Context.Model.FindEntityType(_source.Type.TryGetItemType()!);
            var memberMetadata = entityType?.FindMember(members[0].Name);

            if (memberMetadata == null)
            {
                throw new InvalidOperationException(
                    $"Could not create a vector query for '{(entityType?.ClrType ?? _source.Type).ShortDisplayName()}.{members[0].Name}'. Make sure the entity type is included in the EF Core model and that the property or field is mapped.");
            }

            foreach (var memberInfo in members.Skip(1))
            {
                memberMetadata = (memberMetadata as INavigation)?.TargetEntityType.FindMember(memberInfo.Name);
            }

            AdditionalState[MongoExecutableQuery.VectorQueryProperty] = memberMetadata!;

            var vectorIndexesInModel = memberMetadata?.DeclaringType.ContainingEntityType
                .GetIndexes().Where(i => i.GetVectorIndexOptions() != null && i.Properties[0] == memberMetadata).ToList();

            if (concreteOptions.IndexName == null)
            {
                // Index to use was not specified in the query. Throw or warn if there is anything but one index in the model.
                if (vectorIndexesInModel == null || vectorIndexesInModel.Count == 0)
                {
                    ThrowForBadOptions(
                        "the vector index for this query could not be found. Use 'HasIndex' on the EF model builder to specify the index, or " +
                        "specify the index name in the call to 'VectorQuery' if indexes are being managed outside of EF Core.");
                }

                if (vectorIndexesInModel!.Count > 1)
                {
                    ThrowForBadOptions(
                        "multiple vector indexes are defined for this property in the EF Core model. Specify the index to use in the call to 'VectorSearch'.");
                }

                // There is only one index and none was specified, so use that index.
                concreteOptions = concreteOptions with { IndexName = vectorIndexesInModel[0].Name };
            }
            else
            {
                // Index to use was specified in the query. Throw or warn if it doesn't match any index in the model.
                if (vectorIndexesInModel == null || vectorIndexesInModel!.All(i => i.Name != concreteOptions.IndexName))
                {
                    _queryContext.QueryLogger.VectorSearchNeedsIndex((IProperty)memberMetadata!);
                }
                // Index name in query already matches, so just continue.
            }

            AdditionalState[MongoExecutableQuery.VectorQueryIndexName] = concreteOptions.IndexName!;

            var searchOptionsType = typeof(VectorSearchOptions<>).MakeGenericType(entityType!.ClrType);
            var searchOptions = Activator.CreateInstance(searchOptionsType)!;

            searchOptionsType.GetProperty(nameof(VectorSearchOptions<object>.IndexName))!.SetValue(searchOptions,
                concreteOptions.IndexName);
            searchOptionsType.GetProperty(nameof(VectorSearchOptions<object>.NumberOfCandidates))!.SetValue(searchOptions,
                concreteOptions.NumberOfCandidates);
            searchOptionsType.GetProperty(nameof(VectorSearchOptions<object>.Exact))!.SetValue(searchOptions,
                concreteOptions.Exact);

            if (preFilterExpression != null)
            {
                var convertedExpression = Activator.CreateInstance(
                    typeof(ExpressionFilterDefinition<>).MakeGenericType(entityType.ClrType),
                    Visit(preFilterExpression));

                searchOptionsType.GetProperty(nameof(VectorSearchOptions<object>.Filter))!.SetValue(searchOptions,
                    convertedExpression);
            }

            var vectorSearchPipelineStage = typeof(PipelineStageDefinitionBuilder)
                .GetTypeInfo().GetDeclaredMethods(nameof(PipelineStageDefinitionBuilder.VectorSearch))
                .Single(mi =>
                    mi.GetParameters()[0].ParameterType.IsGenericType
                    && mi.GetParameters()[0].ParameterType.GetGenericTypeDefinition() == typeof(Expression<>))
                .MakeGenericMethod(entityType.ClrType, memberMetadata!.ClrType)
                .Invoke(null, [propertyExpression, queryVector, limit, searchOptions]);

            var appendStageMethod = typeof(MongoQueryable).GetMethod(nameof(MongoQueryable.AppendStage))!
                .MakeGenericMethod(entityType.ClrType, entityType.ClrType);

            var serializerType = typeof(IBsonSerializer<>).MakeGenericType(entityType.ClrType);

            var vectorSource = Expression.Call(
                null,
                appendStageMethod,
                Visit(methodCallExpression.Arguments[0])!,
                Expression.Constant(vectorSearchPipelineStage),
                Expression.Constant(null, serializerType));

            return Expression.Call(
                null,
                appendStageMethod,
                vectorSource,
                Expression.New(
                    typeof(BsonDocumentPipelineStageDefinition<,>)
                        .MakeGenericType(entityType.ClrType, entityType.ClrType)
                        .GetConstructor([typeof(BsonDocument), serializerType])!,
                    Expression.Constant(AddScoreField),
                    Expression.Constant(null, serializerType)),
                Expression.Constant(null, serializerType));

            void ThrowForBadOptions(string reason)
            {
                throw new InvalidOperationException(
                    $"A vector query for '{entityType!.DisplayName()}.{members[0].Name}' could not be executed because {reason}");
            }

#if EF8 || EF9
            TValue? ParamValue<TValue>(int index)
                => (TValue?)_queryContext.ParameterValues[((ParameterExpression)methodCallExpression.Arguments[index]).Name!];
#else
            TValue? ParamValue<TValue>(int index)
                => (TValue?)_queryContext.Parameters[((QueryParameterExpression)methodCallExpression.Arguments[index]).Name!];
#endif
        }
    }

    /// <summary>
    /// For Include queries, EF generates: First(Select(LeftJoin(Orders, Customers, ...), selector))
    /// We need to strip the Select+LeftJoin and keep just the terminal operation on the base source.
    /// E.g., First(Select(LeftJoin(Orders, Customers, ...), selector)) -> First(Orders)
    /// </summary>
    /// <summary>
    /// For Include queries, strip the Join+Select chain from the expression and return
    /// just the base source. The $lookup stages appended later handle the join.
    /// Handles patterns like:
    ///   Select(LeftJoin(Source, ...), selector) -> Source
    ///   First(Select(LeftJoin(Source, ...), selector)) -> First(Source)
    ///   Where(Select(LeftJoin(Source, ...), selector), pred) -> Source (pred stripped, $match from $lookup)
    /// </summary>
    private static Expression? StripJoinForLookup(Expression expression)
    {
        if (expression is not MethodCallExpression outerCall)
        {
            return null;
        }

        // If the outermost call IS the Select/Join chain itself, just return the base source
        var baseSource = FindBaseSourceThroughJoin(outerCall);
        if (baseSource != null && IsJoinRelatedMethod(outerCall))
        {
            return baseSource;
        }

        // Otherwise, the outermost call is a terminal op (First, etc.) wrapping the join chain
        var source = outerCall.Arguments[0];
        baseSource = FindBaseSourceThroughJoin(source);
        if (baseSource == null)
        {
            return null;
        }

        // Replace the source argument with the base source
        var newArgs = outerCall.Arguments.ToArray();
        newArgs[0] = baseSource;

        // Re-create the terminal operation with the correct generic type
        var method = outerCall.Method;
        if (method.IsGenericMethod)
        {
            var baseItemType = baseSource.Type.TryGetItemType();
            if (baseItemType != null)
            {
                var genericDef = method.GetGenericMethodDefinition();
                var genericParams = genericDef.GetGenericArguments();
                if (genericParams.Length == 1)
                {
                    method = genericDef.MakeGenericMethod(baseItemType);
                }
            }
        }

        return Expression.Call(null, method, newArgs);
    }

    private static bool IsJoinRelatedMethod(MethodCallExpression call)
        => call.Method.Name is "Select" or "LeftJoin" or "Join" or "GroupJoin" or "SelectMany" or "Where";

    private static Expression? FindBaseSourceThroughJoin(Expression expression)
    {
        if (expression is not MethodCallExpression call)
        {
            return null;
        }

        // Select(source, selector) where source contains a Join
        if (call.Method.Name == "Select" && call.Arguments.Count >= 2)
        {
            return FindBaseSourceThroughJoin(call.Arguments[0]);
        }

        // LeftJoin(outer, inner, ...) or Join(outer, inner, ...) or GroupJoin(...)
        // Recurse into the outer source to handle nested joins (multi-level Include).
        if (call.Method.Name is "LeftJoin" or "Join" or "GroupJoin")
        {
            return FindBaseSourceThroughJoin(call.Arguments[0]) ?? call.Arguments[0];
        }

        // SelectMany(source, ...) - part of GroupJoin+SelectMany pattern
        if (call.Method.Name == "SelectMany")
        {
            return FindBaseSourceThroughJoin(call.Arguments[0]);
        }

        // Where(source, predicate) - filter that may sit between Select and Join
        if (call.Method.Name == "Where" && call.Arguments.Count >= 2)
        {
            return FindBaseSourceThroughJoin(call.Arguments[0]);
        }

        return null;
    }

    private static bool IsLeftJoinMethod(MethodCallExpression methodCallExpression)
    {
        var method = methodCallExpression.Method;
        return method.IsGenericMethod
               && method.Name == "LeftJoin"
               && method.DeclaringType == typeof(Queryable);
    }

    /// <summary>
    /// Rewrites Queryable.LeftJoin into Queryable.GroupJoin + SelectMany + DefaultIfEmpty,
    /// which the MongoDB driver's LINQ3 provider can translate to $lookup.
    /// LeftJoin(outer, inner, outerKey, innerKey, (o,i) => result)
    /// becomes:
    /// GroupJoin(outer, inner, outerKey, innerKey, (o, group) => new { o, group })
    ///     .SelectMany(x => x.group.DefaultIfEmpty(), (x, i) => resultSelector(x.o, i))
    /// </summary>
    private Expression RewriteLeftJoinAsGroupJoin(MethodCallExpression leftJoinCall)
    {
        var outerSource = Visit(leftJoinCall.Arguments[0])!;
        var innerSource = Visit(leftJoinCall.Arguments[1])!;
        var outerKeySelector = Visit(leftJoinCall.Arguments[2])!;
        var innerKeySelector = Visit(leftJoinCall.Arguments[3])!;
        var resultSelector = (LambdaExpression)leftJoinCall.Arguments[4].UnwrapLambdaFromQuote();

        var outerType = outerSource.Type.TryGetItemType()!;
        var innerType = innerSource.Type.TryGetItemType()!;
        var innerEnumerableType = typeof(IEnumerable<>).MakeGenericType(innerType);

        // Get the key type from the outer key selector lambda
        var outerKeySelectorLambda = outerKeySelector is UnaryExpression { NodeType: ExpressionType.Quote } quote
            ? (LambdaExpression)quote.Operand
            : (LambdaExpression)outerKeySelector;
        var keyType = outerKeySelectorLambda.ReturnType;

        // Build GroupJoin: (o, group) => new { Outer = o, Group = group }
        var outerParam = Expression.Parameter(outerType, "o");
        var groupParam = Expression.Parameter(innerEnumerableType, "group");
        var anonType = typeof(LeftJoinIntermediate<,>).MakeGenericType(outerType, innerType);
        var anonCtor = anonType.GetConstructors()[0];
        var groupJoinSelector = Expression.Lambda(
            Expression.New(anonCtor, outerParam, groupParam),
            outerParam, groupParam);

        var groupJoinMethod = QueryableMethods.GroupJoin
            .MakeGenericMethod(outerType, innerType, keyType, anonType);

        var groupJoinCall = Expression.Call(null, groupJoinMethod,
            outerSource, innerSource, outerKeySelector, innerKeySelector,
            Expression.Quote(groupJoinSelector));

        // Build SelectMany: x => x.Group.DefaultIfEmpty()
        var xParam = Expression.Parameter(anonType, "x");
        var groupAccess = Expression.Property(xParam, "Group");
        var defaultIfEmptyMethod = typeof(Enumerable).GetMethods()
            .First(m => m.Name == "DefaultIfEmpty" && m.GetParameters().Length == 1)
            .MakeGenericMethod(innerType);
        var defaultIfEmptyCall = Expression.Call(null, defaultIfEmptyMethod, groupAccess);
        var collectionSelector = Expression.Lambda(defaultIfEmptyCall, xParam);

        // Build result selector: (x, i) => originalResultSelector(x.Outer, i)
        var iParam = Expression.Parameter(innerType, "i");
        var xParam2 = Expression.Parameter(anonType, "x");
        var outerAccess = Expression.Property(xParam2, "Outer");
        var resultBody = new ReplacingExpressionVisitor(
            [resultSelector.Parameters[0], resultSelector.Parameters[1]],
            [outerAccess, iParam]).Visit(resultSelector.Body);
        var selectManyResultSelector = Expression.Lambda(resultBody, xParam2, iParam);

        var selectManyMethod = QueryableMethods.SelectManyWithCollectionSelector
            .MakeGenericMethod(anonType, innerType, resultSelector.Body.Type);

        return Expression.Call(null, selectManyMethod,
            groupJoinCall,
            Expression.Quote(collectionSelector),
            Expression.Quote(selectManyResultSelector));
    }

    private Expression AppendLookupStages(Expression query)
    {
        if (_pendingLookups.Count == 0)
        {
            return query;
        }

        var sourceType = _source.Type.TryGetItemType()!;
        var appendStageMethod = typeof(MongoQueryable).GetMethod(nameof(MongoQueryable.AppendStage))!
            .MakeGenericMethod(sourceType, sourceType);
        var serializerType = typeof(IBsonSerializer<>).MakeGenericType(sourceType);
        var stageDefinitionType = typeof(BsonDocumentPipelineStageDefinition<,>).MakeGenericType(sourceType, sourceType);
        var stageConstructor = stageDefinitionType.GetConstructor([typeof(BsonDocument), serializerType])!;

        foreach (var lookup in _pendingLookups)
        {
            var lookupDoc = new BsonDocument("$lookup", new BsonDocument
            {
                { "from", lookup.From },
                { "localField", lookup.LocalField },
                { "foreignField", lookup.ForeignField },
                { "as", lookup.As }
            });

            query = Expression.Call(null, appendStageMethod, query,
                Expression.New(stageConstructor,
                    Expression.Constant(lookupDoc),
                    Expression.Constant(null, serializerType)),
                Expression.Constant(null, serializerType));

            if (lookup.ShouldUnwind)
            {
                var unwindDoc = new BsonDocument("$unwind", new BsonDocument
                {
                    { "path", $"${lookup.As}" },
                    { "preserveNullAndEmptyArrays", true }
                });

                query = Expression.Call(null, appendStageMethod, query,
                    Expression.New(stageConstructor,
                        Expression.Constant(unwindDoc),
                        Expression.Constant(null, serializerType)),
                    Expression.Constant(null, serializerType));
            }
        }

        return query;
    }

    private static readonly BsonDocument AddScoreField =
        new("$addFields", new BsonDocument { { "__score", new BsonDocument("$meta", "vectorSearchScore") } });

    private static Expression ConvertIfRequired(Expression expression, Type targetType) =>
        expression.Type == targetType ? expression : Expression.Convert(expression, targetType);

    private static Expression RemoveObjectConvert(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unaryExpression
           && unaryExpression.Type == typeof(object)
            ? unaryExpression.Operand
            : expression;

    private static readonly MethodInfo AsMethodInfo = typeof(MongoQueryable)
        .GetMethods()
        .First(mi => mi is { Name: nameof(MongoQueryable.As), IsPublic: true, IsStatic: true } && mi.GetParameters().Length == 2);
}
