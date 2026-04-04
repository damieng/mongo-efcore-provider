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
using Microsoft.EntityFrameworkCore.Metadata;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.Query.Expressions;

/// <summary>
/// Represents a pending $lookup aggregation stage needed to include
/// a cross-collection navigation property.
/// </summary>
internal sealed class LookupExpression
{
    /// <summary>
    /// Create a <see cref="LookupExpression"/> for the given navigation.
    /// </summary>
    /// <param name="navigation">The <see cref="INavigation"/> that requires a $lookup.</param>
    public LookupExpression(INavigation navigation)
    {
        Navigation = navigation;

        var foreignKey = navigation.ForeignKey;
        var targetEntityType = navigation.TargetEntityType;
        From = targetEntityType.GetCollectionName();

        if (navigation.IsOnDependent)
        {
            // e.g., Order.Customer where FK (CustomerId) is on Order
            LocalField = foreignKey.Properties[0].GetElementName();
            ForeignField = foreignKey.PrincipalKey.Properties[0].GetElementName();
        }
        else
        {
            // e.g., Customer.Orders where FK (CustomerId) is on Order
            LocalField = foreignKey.PrincipalKey.Properties[0].GetElementName();
            ForeignField = foreignKey.Properties[0].GetElementName();
        }

        As = $"_lookup_{navigation.Name}";
    }

    /// <summary>The navigation this lookup supports.</summary>
    public INavigation Navigation { get; }

    /// <summary>The target collection name to look up from.</summary>
    public string From { get; }

    /// <summary>The field on the local document to match.</summary>
    public string LocalField { get; }

    /// <summary>The field on the foreign document to match.</summary>
    public string ForeignField { get; }

    /// <summary>The output array field name in the resulting document.</summary>
    public string As { get; }

    /// <summary>Whether this lookup is for a single reference (not a collection).</summary>
    public bool IsReference => !Navigation.IsCollection;
}
