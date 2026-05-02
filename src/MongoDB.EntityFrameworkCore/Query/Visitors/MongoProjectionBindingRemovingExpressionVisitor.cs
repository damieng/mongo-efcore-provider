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
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.EntityFrameworkCore.Storage;
using MongoDB.Bson;
using MongoDB.EntityFrameworkCore.Extensions;
using MongoDB.EntityFrameworkCore.Query.Expressions;
using MongoDB.EntityFrameworkCore.Storage;

namespace MongoDB.EntityFrameworkCore.Query.Visitors;

/// <summary>
/// Translates an shaper expression tree to use <see cref="BsonDocument"/> and the right
/// methods to obtain data instead of the <see cref="ValueBuffer"/> EF provides.
/// </summary>
internal class MongoProjectionBindingRemovingExpressionVisitor : ExpressionVisitor
{
    private readonly MongoQueryExpression _queryExpression;
    private readonly IEntityType _rootEntityType;
    private readonly ParameterExpression DocParameter;
    private readonly bool _trackQueryResults;
    private BindingScope _bindingScope = new(null);
    private List<IncludeExpression> _pendingIncludes = [];

    /// <summary>
    /// Create a <see cref="MongoProjectionBindingRemovingExpressionVisitor"/>.
    /// </summary>
    /// <param name="rootEntityType">The <see cref="IEntityType"/> this projection relates to.</param>
    /// <param name="queryExpression">The <see cref="MongoQueryExpression"/> this visitor should use.</param>
    /// <param name="docParameter">The parameter that will hold the <see cref="BsonDocument"/> input parameter to the shaper.</param>
    /// <param name="trackQueryResults">
    /// <see langword="true"/> if the results from this query are being tracked for changes,
    /// <see langword="false"/> if they are not.
    /// </param>
    public MongoProjectionBindingRemovingExpressionVisitor(
        IEntityType rootEntityType,
        MongoQueryExpression queryExpression,
        ParameterExpression docParameter,
        bool trackQueryResults)
    {
        _queryExpression = queryExpression;
        _rootEntityType = rootEntityType;
        DocParameter = docParameter;
        _trackQueryResults = trackQueryResults;
    }

    protected override Expression VisitExtension(Expression extensionExpression)
    {
        switch (extensionExpression)
        {
            case ProjectionBindingExpression projectionBindingExpression:
                {
                    var projection = GetProjection(projectionBindingExpression);
                    var fieldAccess = TryResolveFieldAccess(projection.Expression);
                    if (fieldAccess.Property != null)
                    {
                        return BsonBinding.CreateGetValueExpression(
                            DocParameter,
                            projection.Alias,
                            fieldAccess.Property,
                            projectionBindingExpression.Type);
                    }

                    return CreateGetValueExpression(
                        DocParameter,
                        projection.Alias,
                        !projectionBindingExpression.Type.IsNullableType(),
                        projectionBindingExpression.Type);
                }

            case CollectionShaperExpression collectionShaperExpression:
                {
                    ObjectArrayProjectionExpression objectArrayProjection;
                    switch (collectionShaperExpression.Projection)
                    {
                        case ProjectionBindingExpression projectionBindingExpression:
                            var projection = GetProjection(projectionBindingExpression);
                            objectArrayProjection = (ObjectArrayProjectionExpression)projection.Expression;
                            break;
                        case ObjectArrayProjectionExpression objectArrayProjectionExpression:
                            objectArrayProjection = objectArrayProjectionExpression;
                            break;
                        default:
                            throw new InvalidOperationException(CoreStrings.TranslationFailed(extensionExpression.Print()));
                    }

                    var bsonArray = GetProjectionBinding(objectArrayProjection);
                    var jObjectParameter = Expression.Parameter(typeof(BsonDocument), bsonArray.Name + "Object");
                    var ordinalParameter = Expression.Parameter(typeof(int), bsonArray.Name + "Ordinal");

                    BlockExpression innerShaper;
                    using (PushBindingScope())
                    {
                        var accessExpression = objectArrayProjection.InnerProjection.ParentAccessExpression;
                        BindProjection(accessExpression, jObjectParameter);
                        BindOwner(
                            accessExpression,
                            objectArrayProjection.Navigation.DeclaringEntityType,
                            objectArrayProjection.AccessExpression);
                        BindOwner(
                            jObjectParameter,
                            objectArrayProjection.Navigation.DeclaringEntityType,
                            objectArrayProjection.AccessExpression);
                        BindOrdinal(
                            accessExpression,
                            Expression.Add(ordinalParameter, Expression.Constant(1, typeof(int))));
                        BindOrdinal(
                            jObjectParameter,
                            Expression.Add(ordinalParameter, Expression.Constant(1, typeof(int))));

                        innerShaper = (BlockExpression)Visit(collectionShaperExpression.InnerShaper);
                    }

                    innerShaper = AddIncludes(innerShaper);

                    var entities = Expression.Call(
                        EnumerableMethods.SelectWithOrdinal.MakeGenericMethod(typeof(BsonDocument), innerShaper.Type),
                        Expression.Call(
                            EnumerableMethods.Cast.MakeGenericMethod(typeof(BsonDocument)),
                            bsonArray),
                        Expression.Lambda(innerShaper, jObjectParameter, ordinalParameter));

                    var navigation = collectionShaperExpression.Navigation!;
                    return Expression.Call(
                        PopulateCollectionMethodInfo.MakeGenericMethod(navigation.TargetEntityType.ClrType, navigation.ClrType),
                        Expression.Constant(navigation.GetCollectionAccessor()),
                        entities);
                }

            case IncludeExpression includeExpression:
                {
                    if (!(includeExpression.Navigation is INavigation navigation)
                        || navigation.IsOnDependent
                        || navigation.ForeignKey.DeclaringEntityType.IsDocumentRoot())
                    {
                        throw new InvalidOperationException(
                            $"Including navigation '{includeExpression.Navigation
                            }' is not supported as the navigation is not embedded in same resource.");
                    }

                    var isFirstInclude = _pendingIncludes.Count == 0;
                    _pendingIncludes.Add(includeExpression);

                    var bsonDocBlock = (Visit(includeExpression.EntityExpression) as BlockExpression)!;

                    if (!isFirstInclude)
                    {
                        return bsonDocBlock;
                    }

                    var bsonDocCondition = (ConditionalExpression)bsonDocBlock.Expressions[^1];

                    var shaperBlock = (BlockExpression)bsonDocCondition.IfFalse;
                    shaperBlock = AddIncludes(shaperBlock);

                    List<Expression> jObjectExpressions = [..bsonDocBlock.Expressions];
                    jObjectExpressions.RemoveAt(jObjectExpressions.Count - 1);

                    jObjectExpressions.Add(
                        bsonDocCondition.Update(bsonDocCondition.Test, bsonDocCondition.IfTrue, shaperBlock));

                    return bsonDocBlock.Update(bsonDocBlock.Variables, jObjectExpressions);
                }
        }

        return base.VisitExtension(extensionExpression);
    }

    /// <summary>
    /// Visits a <see cref="BinaryExpression"/> replacing empty ProjectionBindingExpressions
    /// while passing through visitation of all others.
    /// </summary>
    /// <param name="binaryExpression">The <see cref="BinaryExpression"/> to visit.</param>
    /// <returns>A <see cref="BinaryExpression"/> with any necessary adjustments.</returns>
    protected override Expression VisitBinary(BinaryExpression binaryExpression)
    {
        if (binaryExpression.NodeType == ExpressionType.Assign)
        {
            if (binaryExpression.Left is ParameterExpression parameterExpression)
            {
                if (parameterExpression.Type == typeof(BsonDocument) || parameterExpression.Type == typeof(BsonArray))
                {
                    string? fieldName = null;
                    var fieldRequired = true;

                    var projectionExpression = ((UnaryExpression)binaryExpression.Right).Operand;
                    if (projectionExpression is ProjectionBindingExpression projectionBindingExpression)
                    {
                        var projection = GetProjection(projectionBindingExpression);
                        projectionExpression = projection.Expression;
                        fieldName = projection.Alias;
                    }
                    else if (projectionExpression is UnaryExpression convertExpression &&
                             convertExpression.NodeType == ExpressionType.Convert)
                    {
                        projectionExpression = ((UnaryExpression)convertExpression.Operand).Operand;
                    }

                    Expression innerAccessExpression;
                    if (projectionExpression is ObjectArrayProjectionExpression objectArrayProjectionExpression)
                    {
                        innerAccessExpression = objectArrayProjectionExpression.AccessExpression;
                        BindProjection(objectArrayProjectionExpression, parameterExpression);
                        fieldName ??= FindProjectionAlias(objectArrayProjectionExpression) ?? objectArrayProjectionExpression.Name;
                    }
                    else
                    {
                        var entityProjectionExpression = (EntityProjectionExpression)projectionExpression;
                        var accessExpression = entityProjectionExpression.ParentAccessExpression;
                        BindProjection(accessExpression, parameterExpression);
                        fieldName ??= entityProjectionExpression.Name;

                        switch (accessExpression)
                        {
                            case ObjectAccessExpression innerObjectAccessExpression:
                                innerAccessExpression = innerObjectAccessExpression.AccessExpression;
                                BindOwner(
                                    accessExpression,
                                    innerObjectAccessExpression.Navigation.DeclaringEntityType,
                                    innerAccessExpression);
                                BindOwner(
                                    parameterExpression,
                                    innerObjectAccessExpression.Navigation.DeclaringEntityType,
                                    innerAccessExpression);
                                if (TryGetOrdinalBinding(accessExpression, out var rootOrdinalExpression))
                                {
                                    BindOrdinal(parameterExpression, rootOrdinalExpression);
                                }

                                fieldRequired = innerObjectAccessExpression.Required;
                                break;
                            case RootReferenceExpression rootReferenceExpression:
                                innerAccessExpression = rootReferenceExpression;
                                if (TryGetOwnerBinding(accessExpression, out var ownerInfo))
                                {
                                    BindOwner(parameterExpression, ownerInfo.EntityType, ownerInfo.BsonDocExpression);
                                }

                                if (TryGetOrdinalBinding(accessExpression, out var ordinalExpression))
                                {
                                    BindOrdinal(parameterExpression, ordinalExpression);
                                }

                                break;
                            default:
                                throw new InvalidOperationException(
                                    $"Unknown access expression type {accessExpression.Type.ShortDisplayName()}.");
                        }
                    }

                    var valueExpression =
                        CreateGetValueExpression(innerAccessExpression, fieldName, fieldRequired, parameterExpression.Type);

                    return Expression.MakeBinary(ExpressionType.Assign, binaryExpression.Left, valueExpression);
                }

                if (parameterExpression.Type == typeof(MaterializationContext))
                {
                    var newExpression = (NewExpression)binaryExpression.Right;

                    EntityProjectionExpression entityProjectionExpression;
                    if (newExpression.Arguments[0] is ProjectionBindingExpression projectionBindingExpression)
                    {
                        var projection = GetProjection(projectionBindingExpression);
                        entityProjectionExpression = (EntityProjectionExpression)projection.Expression;
                    }
                    else
                    {
                        var projection = ((UnaryExpression)((UnaryExpression)newExpression.Arguments[0]).Operand).Operand;
                        entityProjectionExpression = (EntityProjectionExpression)projection;
                    }

                    BindMaterializationContext(parameterExpression, entityProjectionExpression.ParentAccessExpression);

                    var updatedExpression = Expression.New(
                        newExpression.Constructor!,
                        Expression.Constant(ValueBuffer.Empty),
                        newExpression.Arguments[1]);

                    return Expression.MakeBinary(ExpressionType.Assign, binaryExpression.Left, updatedExpression);
                }
            }

            if (binaryExpression.Left is MemberExpression { Member: FieldInfo { IsInitOnly: true } } memberExpression)
            {
                return memberExpression.Assign(Visit(binaryExpression.Right));
            }
        }

        return base.VisitBinary(binaryExpression);
    }

    protected override Expression VisitBlock(BlockExpression node)
    {
        using (PushBindingScope())
        {
            return base.VisitBlock(node);
        }
    }

    protected override Expression VisitLambda<T>(Expression<T> node)
    {
        using (PushBindingScope())
        {
            return base.VisitLambda(node);
        }
    }

    /// <summary>
    /// Visits a <see cref="MethodCallExpression"/> replacing calls to <see cref="ValueBuffer"/>
    /// with replacement alternatives from <see cref="BsonDocument"/>.
    /// </summary>
    /// <param name="methodCallExpression">The <see cref="MethodCallExpression"/> to visit.</param>
    /// <returns>A <see cref="Expression"/> to replace the original method call with.</returns>
    protected override Expression VisitMethodCall(MethodCallExpression methodCallExpression)
    {
        var method = methodCallExpression.Method;
        var genericMethod = method.IsGenericMethod ? method.GetGenericMethodDefinition() : null;

        if (genericMethod == ExpressionExtensions.ValueBufferTryReadValueMethod)
        {
            var property = methodCallExpression.Arguments[2].GetConstantValue<IProperty>();
            Expression innerExpression;
            if (methodCallExpression.Arguments[0] is ProjectionBindingExpression projectionBindingExpression)
            {
                var projection = GetProjection(projectionBindingExpression);
                innerExpression =
                    CreateGetValueExpression(DocParameter, projection.Alias, projection.Required, typeof(BsonDocument));
            }
            else
            {
                innerExpression =
                    GetMaterializationContextBinding(
                        (ParameterExpression)((MethodCallExpression)methodCallExpression.Arguments[0]).Object!);
            }

            return CreateGetValueExpression(innerExpression, property, methodCallExpression.Type);
        }

        if (method.DeclaringType == typeof(Enumerable)
            && method.Name == nameof(Enumerable.Select)
            && genericMethod == EnumerableMethods.Select)
        {
            var lambda = (LambdaExpression)methodCallExpression.Arguments[1];
            if (lambda.Body is IncludeExpression includeExpression)
            {
                if (!(includeExpression.Navigation is INavigation navigation)
                    || navigation.IsOnDependent
                    || navigation.ForeignKey.DeclaringEntityType.IsDocumentRoot())
                {
                    throw new InvalidOperationException($"Including navigation '{nameof(navigation)
                    }' is not supported as the navigation is not embedded in same resource.");
                }

                _pendingIncludes.Add(includeExpression);

                Visit(includeExpression.EntityExpression);

                // Includes on collections are processed when visiting CollectionShaperExpression
                return Visit(methodCallExpression.Arguments[0]);
            }
        }

        return base.VisitMethodCall(methodCallExpression);
    }

    protected Expression CreateGetValueExpression(
        Expression docExpression,
        IProperty property,
        Type type)
    {
        if (property.IsOwnedTypeKey())
        {
            var entityType = (IReadOnlyEntityType)property.DeclaringType;
            if (!entityType.IsDocumentRoot())
            {
                var ownership = entityType.FindOwnership();
                if (ownership?.IsUnique == false && property.IsOwnedTypeOrdinalKey())
                {
                    var readExpression = GetOrdinalBinding(docExpression);
                    if (readExpression.Type != type)
                    {
                        readExpression = Expression.Convert(readExpression, type);
                    }

                    return readExpression;
                }

                var principalProperty = property.FindFirstPrincipal();
                if (principalProperty != null)
                {
                    Expression? ownerBsonDocExpression = null;
                    if (TryGetOwnerBinding(docExpression, out var ownerInfo))
                    {
                        ownerBsonDocExpression = ownerInfo.BsonDocExpression;
                    }
                    else if (docExpression is RootReferenceExpression rootReferenceExpression)
                    {
                        ownerBsonDocExpression = rootReferenceExpression;
                    }
                    else if (docExpression is ObjectAccessExpression objectAccessExpression)
                    {
                        ownerBsonDocExpression = objectAccessExpression.AccessExpression;
                    }

                    if (ownerBsonDocExpression != null)
                    {
                        return CreateGetValueExpression(ownerBsonDocExpression, principalProperty, type);
                    }
                }
            }

            return Expression.Default(type);
        }

        return Expression.Convert(
            CreateGetValueExpression(docExpression, property.Name, !type.IsNullableType(), type, property.DeclaringType,
                property.GetTypeMapping()),
            type);
    }

    /// <summary>
    /// Obtain the registered <see cref="ProjectionExpression"/> for a given <see cref="ProjectionBindingExpression"/>.
    /// </summary>
    /// <param name="projectionBindingExpression">The <see cref="ProjectionBindingExpression"/> to look-up.</param>
    /// <returns>The registered <see cref="ProjectionExpression"/> this <paramref name="projectionBindingExpression"/> relates to.</returns>
    protected ProjectionExpression GetProjection(ProjectionBindingExpression projectionBindingExpression)
        => _queryExpression.Projection[GetProjectionIndex(projectionBindingExpression)];

    /// <summary>
    /// Create a new compilable <see cref="Expression"/> the shaper can use to obtain the value from the <see cref="BsonDocument"/>.
    /// </summary>
    /// <param name="docExpression">The <see cref="Expression"/> used to access the <see cref="BsonDocument"/>.</param>
    /// <param name="propertyName">The name of the property.</param>
    /// <param name="required"><see langword="true"/> if the field is required, <see langword="false"/> if it is optional.</param>
    /// <param name="type">The <see cref="Type"/> of the value as it is within the document.</param>
    /// <param name="declaredType">The optional <see cref="ITypeBase"/> this element comes from.</param>
    /// <param name="typeMapping">Any associated <see cref="CoreTypeMapping"/> to be used in mapping the value.</param>
    /// <returns>A compilable <see cref="Expression"/> to obtain the desired value as the correct type.</returns>
    protected Expression CreateGetValueExpression(
        Expression docExpression,
        string? propertyName,
        bool required,
        Type type,
        ITypeBase? declaredType = null,
        CoreTypeMapping? typeMapping = null)
    {
        var entityType = declaredType ?? docExpression switch
        {
            RootReferenceExpression rootReferenceExpression => rootReferenceExpression.EntityType,
            ObjectAccessExpression docAccessExpression => docAccessExpression.Navigation.TargetEntityType,
            _ => _rootEntityType
        };

        var innerExpression = docExpression;
        if (TryGetProjectionBinding(docExpression, out var innerVariable))
        {
            innerExpression = innerVariable;
        }
        else
        {
            innerExpression = docExpression switch
            {
                RootReferenceExpression => CreateGetValueExpression(DocParameter, null, required, typeof(BsonDocument)),
                ObjectAccessExpression docAccessExpression => CreateGetValueExpression(docAccessExpression.AccessExpression,
                    docAccessExpression.Name, required, typeof(BsonDocument)),
                _ => innerExpression
            };
        }

        var elementType = typeMapping?.ClrType ?? type;
        return BsonBinding.CreateGetValueExpression(innerExpression, propertyName, required, elementType, entityType);
    }

    protected ResolvedFieldAccess TryResolveFieldAccess(Expression expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        if (expression is MemberExpression memberExpression)
        {
            var source = TryResolveFieldAccessSource(memberExpression.Expression);
            var property = source.EntityType?.FindProperty(memberExpression.Member);
            return property != null
                ? new ResolvedFieldAccess(property, source.DocumentExpression, null)
                : new ResolvedFieldAccess(null, source.DocumentExpression, memberExpression.Member.Name);
        }

        if (expression is MethodCallExpression methodCall)
        {
            if (methodCall.Method.IsEFPropertyMethod()
                && methodCall.Arguments[1] is ConstantExpression { Value: string propertyName })
            {
                var source = TryResolveFieldAccessSource(methodCall.Arguments[0]);
                var property = source.EntityType?.FindProperty(propertyName);
                return property != null
                    ? new ResolvedFieldAccess(property, source.DocumentExpression, null)
                    : new ResolvedFieldAccess(null, source.DocumentExpression, propertyName);
            }

            if (methodCall.Method is { Name: "Field", DeclaringType.FullName: "MongoDB.Driver.Mql" }
                && methodCall.Arguments[1] is ConstantExpression { Value: string fieldName })
            {
                var source = TryResolveFieldAccessSource(methodCall.Arguments[0]);
                var property = source.EntityType?.FindProperty(fieldName);
                return property != null
                    ? new ResolvedFieldAccess(property, source.DocumentExpression, null)
                    : new ResolvedFieldAccess(null, source.DocumentExpression, fieldName);
            }
        }

        return new ResolvedFieldAccess(null, null, null);
    }

    private (IEntityType? EntityType, Expression? DocumentExpression) TryResolveFieldAccessSource(Expression? expression)
    {
        while (expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary)
        {
            expression = unary.Operand;
        }

        if (expression is EntityTypedExpression entityTypedExpression)
        {
            return (entityTypedExpression.EntityType, entityTypedExpression);
        }

        if (expression is RootReferenceExpression rootReferenceExpression)
        {
            return (rootReferenceExpression.EntityType, rootReferenceExpression);
        }

        if (expression is ObjectAccessExpression objectAccessExpression)
        {
            return (objectAccessExpression.Navigation.TargetEntityType, objectAccessExpression);
        }

        if (expression != null && TryGetOwnerBinding(expression, out var ownerInfo))
        {
            return (ownerInfo.EntityType, ownerInfo.BsonDocExpression);
        }

        return (_rootEntityType, DocParameter);
    }

    private IDisposable PushBindingScope()
    {
        _bindingScope = new BindingScope(_bindingScope);
        return new BindingScopeLease(this);
    }

    private void BindMaterializationContext(ParameterExpression parameterExpression, Expression accessExpression)
        => _bindingScope.MaterializationContexts[parameterExpression] = accessExpression;

    private Expression GetMaterializationContextBinding(ParameterExpression parameterExpression)
        => _bindingScope.TryGetMaterializationContext(parameterExpression, out var accessExpression)
            ? accessExpression
            : throw new InvalidOperationException($"No BSON binding is available for materialization context '{
                parameterExpression.Name}'.");

    private void BindProjection(Expression expression, ParameterExpression parameterExpression)
        => _bindingScope.Projections[expression] = parameterExpression;

    private ParameterExpression GetProjectionBinding(Expression expression)
        => TryGetProjectionBinding(expression, out var parameterExpression)
            ? parameterExpression
            : throw new InvalidOperationException($"No BSON projection binding is available for expression '{expression.Print()}'.");

    private bool TryGetProjectionBinding(Expression expression, out ParameterExpression parameterExpression)
        => _bindingScope.TryGetProjection(expression, out parameterExpression!);

    private void BindOwner(Expression expression, IEntityType entityType, Expression bsonDocExpression)
        => _bindingScope.Owners[expression] = (entityType, bsonDocExpression);

    private bool TryGetOwnerBinding(
        Expression expression,
        out (IEntityType EntityType, Expression BsonDocExpression) ownerInfo)
        => _bindingScope.TryGetOwner(expression, out ownerInfo);

    private void BindOrdinal(Expression expression, Expression ordinalExpression)
        => _bindingScope.Ordinals[expression] = ordinalExpression;

    private Expression GetOrdinalBinding(Expression expression)
        => _bindingScope.TryGetOrdinal(expression, out var ordinalExpression)
            ? ordinalExpression
            : throw new InvalidOperationException($"No ordinal binding is available for expression '{expression.Print()}'.");

    private bool TryGetOrdinalBinding(Expression expression, out Expression ordinalExpression)
        => _bindingScope.TryGetOrdinal(expression, out ordinalExpression!);

    protected readonly record struct ResolvedFieldAccess(
        IProperty? Property,
        Expression? DocumentExpression,
        string? FieldName);

    private sealed class BindingScope(BindingScope? parent)
    {
        public BindingScope? Parent { get; } = parent;
        public Dictionary<ParameterExpression, Expression> MaterializationContexts { get; } = new();
        public Dictionary<Expression, ParameterExpression> Projections { get; } = new();
        public Dictionary<Expression, (IEntityType EntityType, Expression BsonDocExpression)> Owners { get; } = new();
        public Dictionary<Expression, Expression> Ordinals { get; } = new();

        public bool TryGetMaterializationContext(ParameterExpression parameterExpression, out Expression expression)
        {
            for (var scope = this; scope != null; scope = scope.Parent)
            {
                if (scope.MaterializationContexts.TryGetValue(parameterExpression, out expression!))
                {
                    return true;
                }
            }

            expression = null!;
            return false;
        }

        public bool TryGetProjection(Expression key, out ParameterExpression parameterExpression)
        {
            for (var scope = this; scope != null; scope = scope.Parent)
            {
                if (scope.Projections.TryGetValue(key, out parameterExpression!))
                {
                    return true;
                }
            }

            parameterExpression = null!;
            return false;
        }

        public bool TryGetOwner(Expression key, out (IEntityType EntityType, Expression BsonDocExpression) ownerInfo)
        {
            for (var scope = this; scope != null; scope = scope.Parent)
            {
                if (scope.Owners.TryGetValue(key, out ownerInfo))
                {
                    return true;
                }
            }

            ownerInfo = default;
            return false;
        }

        public bool TryGetOrdinal(Expression key, out Expression ordinalExpression)
        {
            for (var scope = this; scope != null; scope = scope.Parent)
            {
                if (scope.Ordinals.TryGetValue(key, out ordinalExpression!))
                {
                    return true;
                }
            }

            ordinalExpression = null!;
            return false;
        }
    }

    private sealed class BindingScopeLease(MongoProjectionBindingRemovingExpressionVisitor visitor) : IDisposable
    {
        private readonly BindingScope _previousScope = visitor._bindingScope.Parent!;

        public void Dispose()
            => visitor._bindingScope = _previousScope;
    }

    private BlockExpression AddIncludes(BlockExpression shaperBlock)
    {
        if (_pendingIncludes.Count == 0)
        {
            return shaperBlock;
        }

        List<Expression> shaperExpressions = [..shaperBlock.Expressions];
        var instanceVariable = shaperExpressions[^1];
        shaperExpressions.RemoveAt(shaperExpressions.Count - 1);

        var includesToProcess = _pendingIncludes;
        _pendingIncludes = [];

        foreach (var include in includesToProcess)
        {
            AddInclude(shaperExpressions, include, shaperBlock, instanceVariable);
        }

        shaperExpressions.Add(instanceVariable);
        shaperBlock = shaperBlock.Update(shaperBlock.Variables, shaperExpressions);
        return shaperBlock;
    }

    private void AddInclude(
        List<Expression> shaperExpressions,
        IncludeExpression includeExpression,
        BlockExpression shaperBlock,
        Expression instanceVariable)
    {
        var navigation = (INavigation)includeExpression.Navigation;
        var includeMethod = navigation.IsCollection ? IncludeCollectionMethodInfo : IncludeReferenceMethodInfo;
        var includingClrType = navigation.DeclaringEntityType.ClrType;
        var relatedEntityClrType = navigation.TargetEntityType.ClrType;
#pragma warning disable EF1001 // Internal EF Core API usage.
        Expression entityEntryVariable = _trackQueryResults
            ? shaperBlock.Variables.Single(v => v.Type == typeof(InternalEntityEntry))
            : Expression.Constant(null, typeof(InternalEntityEntry));
#pragma warning restore EF1001 // Internal EF Core API usage.

        var concreteEntityTypeVariable = shaperBlock.Variables.Single(v => v.Type == typeof(IEntityType));
        var inverseNavigation = navigation.Inverse;
        var fixup = GenerateFixup(
            includingClrType, relatedEntityClrType, navigation, inverseNavigation!);

        var navigationExpression = Visit(includeExpression.NavigationExpression);

        shaperExpressions.Add(
            Expression.IfThen(
                Expression.Call(
                    Expression.Constant(navigation.DeclaringEntityType, typeof(IReadOnlyEntityType)),
                    IsAssignableFromMethodInfo,
                    Expression.Convert(concreteEntityTypeVariable, typeof(IReadOnlyEntityType))),
                Expression.Call(
                    includeMethod.MakeGenericMethod(includingClrType, relatedEntityClrType),
                    entityEntryVariable,
                    instanceVariable,
                    concreteEntityTypeVariable,
                    navigationExpression,
                    Expression.Constant(navigation),
                    Expression.Constant(inverseNavigation, typeof(INavigation)),
                    Expression.Constant(fixup),
#pragma warning disable EF1001 // Internal EF Core API usage.
                    Expression.Constant(includeExpression.SetLoaded))));
#pragma warning restore EF1001 // Internal EF Core API usage.
    }

    private static readonly MethodInfo IncludeReferenceMethodInfo
        = typeof(MongoProjectionBindingRemovingExpressionVisitor).GetTypeInfo()
            .GetDeclaredMethod(nameof(IncludeReference))!;

    private static void IncludeReference<TIncludingEntity, TIncludedEntity>(
#pragma warning disable EF1001 // Internal EF Core API usage.
        InternalEntityEntry entry,
#pragma warning restore EF1001 // Internal EF Core API usage.
        object entity,
        IEntityType entityType,
        TIncludedEntity relatedEntity,
        INavigation navigation,
        INavigation inverseNavigation,
        Action<TIncludingEntity, TIncludedEntity> fixup,
        bool __)
    {
        if (entity == null
            || !navigation.DeclaringEntityType.IsAssignableFrom(entityType))
        {
            return;
        }

        if (entry == null)
        {
            var includingEntity = (TIncludingEntity)entity;
            navigation.SetIsLoadedWhenNoTracking(includingEntity);
            if (relatedEntity != null)
            {
                fixup(includingEntity, relatedEntity);
                if (inverseNavigation != null
                    && !inverseNavigation.IsCollection)
                {
                    inverseNavigation.SetIsLoadedWhenNoTracking(relatedEntity);
                }
            }
        }
        // For non-null relatedEntity StateManager will set the flag
        else if (relatedEntity == null)
        {
#pragma warning disable EF1001 // Internal EF Core API usage.
            entry.SetIsLoaded(navigation);
#pragma warning restore EF1001 // Internal EF Core API usage.
        }
    }

    private static readonly MethodInfo IncludeCollectionMethodInfo
        = typeof(MongoProjectionBindingRemovingExpressionVisitor).GetTypeInfo()
            .GetDeclaredMethod(nameof(IncludeCollection))!;

    private static void IncludeCollection<TIncludingEntity, TIncludedEntity>(
#pragma warning disable EF1001 // Internal EF Core API usage.
        InternalEntityEntry? entry,
#pragma warning restore EF1001 // Internal EF Core API usage.
        object? entity,
        IEntityType entityType,
        IEnumerable<TIncludedEntity>? relatedEntities,
        INavigation navigation,
        INavigation inverseNavigation,
        Action<TIncludingEntity, TIncludedEntity> fixup,
        bool setLoaded)
    {
        if (entity == null
            || !navigation.DeclaringEntityType.IsAssignableFrom(entityType))
        {
            return;
        }

        if (entry == null)
        {
            var includingEntity = (TIncludingEntity)entity;
            navigation.SetIsLoadedWhenNoTracking(includingEntity);

            if (relatedEntities != null)
            {
                foreach (var relatedEntity in relatedEntities)
                {
                    fixup(includingEntity, relatedEntity);
                    inverseNavigation?.SetIsLoadedWhenNoTracking(relatedEntity!);
                }
            }
        }
        else
        {
            if (setLoaded)
            {
#pragma warning disable EF1001 // Internal EF Core API usage.
                entry.SetIsLoaded(navigation);
#pragma warning restore EF1001 // Internal EF Core API usage.
            }

            if (relatedEntities != null)
            {
                using var enumerator = relatedEntities.GetEnumerator();
                while (enumerator.MoveNext())
                {
                }
            }
        }

        // Ensure empty collections still initialize a new CLR object for them
        if (relatedEntities != null && !navigation.IsShadowProperty())
        {
            navigation.GetCollectionAccessor()!.GetOrCreate(entity, forMaterialization: true);
        }
    }

    private static Delegate GenerateFixup(
        Type entityType,
        Type relatedEntityType,
        INavigation navigation,
        INavigation inverseNavigation)
    {
        var entityParameter = Expression.Parameter(entityType);
        var relatedEntityParameter = Expression.Parameter(relatedEntityType);
        List<Expression> expressions =
        [
            navigation.IsCollection
                ? AddToCollectionNavigation(entityParameter, relatedEntityParameter, navigation)
                : AssignReferenceNavigation(entityParameter, relatedEntityParameter, navigation)
        ];

        if (inverseNavigation != null)
        {
            expressions.Add(
                inverseNavigation.IsCollection
                    ? AddToCollectionNavigation(relatedEntityParameter, entityParameter, inverseNavigation)
                    : AssignReferenceNavigation(relatedEntityParameter, entityParameter, inverseNavigation));
        }

        return Expression.Lambda(Expression.Block(typeof(void), expressions), entityParameter, relatedEntityParameter)
            .Compile();
    }

    private static Expression AssignReferenceNavigation(
        ParameterExpression entity,
        ParameterExpression relatedEntity,
        INavigation navigation)
        => entity.MakeMemberAccess(navigation.GetMemberInfo(true, true)).Assign(relatedEntity);

    private static Expression AddToCollectionNavigation(
        ParameterExpression entity,
        ParameterExpression relatedEntity,
        INavigation navigation)
        => Expression.Call(
            Expression.Constant(navigation.GetCollectionAccessor()),
            CollectionAccessorAddMethodInfo,
            entity,
            relatedEntity,
            Expression.Constant(true));

    private static readonly MethodInfo PopulateCollectionMethodInfo
        = typeof(MongoProjectionBindingRemovingExpressionVisitor).GetTypeInfo()
            .GetDeclaredMethod(nameof(PopulateCollection))!;

    private static readonly MethodInfo IsAssignableFromMethodInfo
        = typeof(IReadOnlyEntityType).GetMethod(nameof(IReadOnlyEntityType.IsAssignableFrom), [
            typeof(IReadOnlyEntityType)
        ])!;

    private static TCollection PopulateCollection<TEntity, TCollection>(
        IClrCollectionAccessor accessor,
        IEnumerable<TEntity> entities)
    {
        // TODO: throw a better exception for non-ICollection navigations
        var collection = (ICollection<TEntity>)accessor.Create();
        foreach (var entity in entities)
        {
            collection.Add(entity);
        }

        return (TCollection)collection;
    }

    private static readonly MethodInfo CollectionAccessorAddMethodInfo
        = typeof(IClrCollectionAccessor).GetTypeInfo()
            .GetDeclaredMethod(nameof(IClrCollectionAccessor.Add))!;

    private int GetProjectionIndex(ProjectionBindingExpression projectionBindingExpression)
        => projectionBindingExpression.ProjectionMember != null
            ? _queryExpression.GetMappedProjection(projectionBindingExpression.ProjectionMember).GetConstantValue<int>()
            : projectionBindingExpression.Index
              ?? throw new InvalidOperationException("Internal error - projection mapping has neither member nor index.");

    private string? FindProjectionAlias(Expression expression)
        => _queryExpression.Projection.FirstOrDefault(p => p.Expression.Equals(expression))?.Alias;
}
