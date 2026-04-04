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
using MongoDB.Bson;
using MongoDB.Driver;
using MongoDB.EntityFrameworkCore.Extensions;

namespace MongoDB.EntityFrameworkCore.FunctionalTests.Query;

[XUnitCollection("QueryTests")]
public class CrossCollectionIncludeTests(TemporaryDatabaseFixture database)
    : IClassFixture<TemporaryDatabaseFixture>
{
    [Fact]
    public void Model_has_correct_navigations_for_cross_collection_entities()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();
        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);

        var orderType = db.Model.FindEntityType(typeof(Order))!;
        var customerType = db.Model.FindEntityType(typeof(Customer))!;

        // Customer should NOT be owned - it has its own DbSet
        Assert.Null(customerType.FindOwnership());
        Assert.Null(orderType.FindOwnership());

        // Order should have a navigation to Customer
        var customerNav = orderType.FindNavigation(nameof(Order.Customer));
        Assert.NotNull(customerNav);
        Assert.Equal(typeof(Customer), customerNav.TargetEntityType.ClrType);

        // Customer should have a collection navigation to Orders
        var ordersNav = customerType.FindNavigation(nameof(Customer.Orders));
        Assert.NotNull(ordersNav);
        Assert.True(ordersNav.IsCollection);
    }

    [Fact]
    public void Basic_query_without_include_works()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();
        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);

        var order = db.Orders.First();
        Assert.NotNull(order);
        Assert.NotNull(order.Description);
    }

    [Fact]
    public void Include_reference_navigation_materializes_related_entity()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var order = db.Orders.Include(o => o.Customer).First();

        Assert.NotNull(order.Customer);
        Assert.Equal("Alice", order.Customer.Name);
    }

    [Fact]
    public void Include_reference_navigation_with_no_tracking()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var order = db.Orders.AsNoTracking().Include(o => o.Customer).First();

        Assert.NotNull(order.Customer);
        Assert.Equal("Alice", order.Customer.Name);
    }

    [Fact]
    public void Include_collection_navigation_materializes_related_entities()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var customer = db.Customers.Include(c => c.Orders).First(c => c.Name == "Alice");

        Assert.NotNull(customer.Orders);
        Assert.Equal(2, customer.Orders.Count);
    }

    [Fact]
    public void Include_reference_navigation_null_fk_returns_null()
    {
        var (ordersCollection, customersCollection) = SetupOrdersAndCustomers();

        // Insert an order without a customer reference
        var orphanOrder = new BsonDocument
        {
            { "_id", ObjectId.GenerateNewId() },
            { "Description", "Orphan order" }
        };
        database.MongoDatabase.GetCollection<BsonDocument>(ordersCollection).InsertOne(orphanOrder);

        using var db = new OrderCustomerDbContext(database, ordersCollection, customersCollection);
        var order = db.Orders.Include(o => o.Customer).First(o => o.Description == "Orphan order");

        Assert.Null(order.Customer);
    }

    private (string ordersCollection, string customersCollection) SetupOrdersAndCustomers()
    {
        var customersName = TemporaryDatabaseFixtureBase.CreateCollectionName("IncludeCustomers") + Guid.NewGuid().ToString("N")[..8];
        var ordersName = TemporaryDatabaseFixtureBase.CreateCollectionName("IncludeOrders") + Guid.NewGuid().ToString("N")[..8];

        var customerId1 = ObjectId.GenerateNewId();
        var customerId2 = ObjectId.GenerateNewId();

        var customers = database.MongoDatabase.GetCollection<BsonDocument>(customersName);
        customers.InsertMany([
            new BsonDocument { { "_id", customerId1 }, { "Name", "Alice" } },
            new BsonDocument { { "_id", customerId2 }, { "Name", "Bob" } }
        ]);

        var orders = database.MongoDatabase.GetCollection<BsonDocument>(ordersName);
        orders.InsertMany([
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Description", "Order 1" }, { "CustomerId", customerId1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Description", "Order 2" }, { "CustomerId", customerId1 } },
            new BsonDocument { { "_id", ObjectId.GenerateNewId() }, { "Description", "Order 3" }, { "CustomerId", customerId2 } }
        ]);

        return (ordersName, customersName);
    }

    class Order
    {
        public ObjectId _id { get; set; }
        public string Description { get; set; }
        public ObjectId? CustomerId { get; set; }
        public Customer Customer { get; set; }
    }

    class Customer
    {
        public ObjectId _id { get; set; }
        public string Name { get; set; }
        public List<Order> Orders { get; set; }
    }

    class OrderCustomerDbContext : DbContext
    {
        private readonly TemporaryDatabaseFixture _database;
        private readonly string _ordersCollection;
        private readonly string _customersCollection;

        public DbSet<Order> Orders { get; set; }
        public DbSet<Customer> Customers { get; set; }

        public OrderCustomerDbContext(
            TemporaryDatabaseFixture database,
            string ordersCollection,
            string customersCollection)
            : base(new DbContextOptionsBuilder<OrderCustomerDbContext>()
                .UseMongoDB(database.Client, database.MongoDatabase.DatabaseNamespace.DatabaseName)
                .ReplaceService<IModelCacheKeyFactory, IgnoreCacheKeyFactory>()
                .Options)
        {
            _database = database;
            _ordersCollection = ordersCollection;
            _customersCollection = customersCollection;
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<Customer>(b =>
            {
                b.ToCollection(_customersCollection);
                b.HasMany(c => c.Orders)
                    .WithOne(o => o.Customer)
                    .HasForeignKey(o => o.CustomerId);
            });

            modelBuilder.Entity<Order>(b =>
            {
                b.ToCollection(_ordersCollection);
            });
        }

        sealed class IgnoreCacheKeyFactory : IModelCacheKeyFactory
        {
            private static int _count;
            public object Create(DbContext context, bool designTime)
                => Interlocked.Increment(ref _count);
        }
    }
}
