using EtsyShopManager.Core.Domain;
using EtsyShopManager.Web.Components.Shared;
using EtsyShopManager.Web.Modules.Listings;
using EtsyShopManager.Web.Modules.Orders;
using EtsyShopManager.Web.Modules.Stock;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace EtsyShopManager.Tests;

public class TableSortTests
{
    private sealed record Row(string Name, int? Count, DateTime? When);

    private static readonly Row[] Rows =
    [
        new("banana", 3, new DateTime(2026, 1, 2)),
        new("Apple", null, null),
        new("cherry", 10, new DateTime(2026, 1, 1)),
    ];

    private static TableSort<Row> Sort() => new()
    {
        ["name"] = r => r.Name,
        ["count"] = r => r.Count,
        ["when"] = r => r.When,
    };

    [Fact]
    public void Rows_keep_their_order_until_a_header_is_clicked()
    {
        Assert.Equal(["banana", "Apple", "cherry"], Sort().Apply(Rows).Select(r => r.Name));
    }

    [Fact]
    public void A_click_sorts_ascending_and_the_next_click_flips_it()
    {
        var sort = Sort();

        sort.Toggle("name");
        Assert.Equal(["Apple", "banana", "cherry"], sort.Apply(Rows).Select(r => r.Name)); // text ignores case
        Assert.False(sort.Descending);

        sort.Toggle("name");
        Assert.Equal(["cherry", "banana", "Apple"], sort.Apply(Rows).Select(r => r.Name));
        Assert.True(sort.Descending);

        sort.Toggle("count"); // another column starts ascending again
        Assert.False(sort.Descending);
    }

    [Fact]
    public void Numbers_and_dates_sort_by_value_with_empty_values_first()
    {
        var sort = Sort();

        sort.Toggle("count");
        Assert.Equal([null, 3, 10], sort.Apply(Rows).Select(r => r.Count)); // 10 after 3, not "10" before "3"

        sort.Toggle("when");
        sort.Toggle("when");
        Assert.Equal(["banana", "cherry", "Apple"], sort.Apply(Rows).Select(r => r.Name)); // newest first, no date last
    }

    [Fact]
    public void Unknown_columns_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => Sort().Toggle("nope"));
        Assert.Throws<ArgumentException>(() => new ColumnSort("a").Toggle("b"));
    }
}

public class ServerSortTests
{
    /// <summary>Three listings: "Bravo" (stock 7, SKU2…), "alpha" (stock 2, SKU3…, refill on below 5) and "Charlie" (stock 12, SKU1…, inactive).</summary>
    private static async Task<(FakeEtsyShop Shop, Dictionary<string, int> Ids)> ListingsAsync()
    {
        var shop = new FakeEtsyShop();
        shop.Listings.Add(FakeEtsyShop.Listing(2, "Bravo", quantity: 7, updated: 1_760_000_200));
        shop.Listings.Add(FakeEtsyShop.Listing(3, "alpha", quantity: 2, updated: 1_760_000_100));
        shop.Listings.Add(FakeEtsyShop.Listing(1, "Charlie", state: "inactive", quantity: 12, updated: 1_760_000_300));
        await shop.Importer.ImportShopAsync(FakeEtsyShop.ShopId, false, CancellationToken.None);
        Dictionary<string, int> ids;
        await using (var db = shop.Db.CreateDbContext())
            ids = await db.Listings.ToDictionaryAsync(l => l.Title, l => l.Id);
        await shop.Provider.GetRequiredService<StockRefiller>().SaveSettingsAsync(ids["alpha"], true, 5, 999, "tophattwaffle");
        return (shop, ids);
    }

    [Theory]
    [InlineData(ListingSort.TitleAsc, "alpha,Bravo,Charlie")]
    [InlineData(ListingSort.TitleDesc, "Charlie,Bravo,alpha")]
    [InlineData(ListingSort.QuantityAsc, "alpha,Bravo,Charlie")]
    [InlineData(ListingSort.QuantityDesc, "Charlie,Bravo,alpha")]
    [InlineData(ListingSort.UpdatedAsc, "alpha,Bravo,Charlie")]
    [InlineData(ListingSort.UpdatedDesc, "Charlie,Bravo,alpha")]
    [InlineData(ListingSort.StateAsc, "Bravo,alpha,Charlie")] // active before inactive (ties keep id order)
    [InlineData(ListingSort.SkuAsc, "Charlie,Bravo,alpha")]   // SKU1…, SKU2…, SKU3…
    [InlineData(ListingSort.SkuDesc, "alpha,Bravo,Charlie")]
    [InlineData(ListingSort.RefillDesc, "alpha,Bravo,Charlie")] // refill on first
    public async Task Listings_sort_by_every_list_column_across_all_pages(ListingSort sort, string expected)
    {
        var (shop, _) = await ListingsAsync();
        using var _s = shop;
        var queries = shop.Provider.GetRequiredService<ListingQueries>();

        // One row per page: the order must hold across pages, not just within one.
        var titles = new List<string>();
        for (var page = 0; page < 3; page++)
        {
            var (rows, _) = await queries.SearchAsync(new ListingFilter { States = [], Sort = sort, Skip = page, Take = 1 });
            titles.Add(Assert.Single(rows).Title);
        }

        Assert.Equal(expected, string.Join(",", titles));
    }

    [Theory]
    [InlineData(OrderQueries.HistorySort.Date, false, "1,2,3")]
    [InlineData(OrderQueries.HistorySort.Date, true, "3,2,1")]
    [InlineData(OrderQueries.HistorySort.Buyer, false, "2,3,1")]
    [InlineData(OrderQueries.HistorySort.Total, false, "3,1,2")]
    [InlineData(OrderQueries.HistorySort.Total, true, "2,1,3")]
    [InlineData(OrderQueries.HistorySort.Items, false, "3,1,2")]
    [InlineData(OrderQueries.HistorySort.Receipt, true, "3,2,1")]
    [InlineData(OrderQueries.HistorySort.Status, false, "3,1,2")] // not shipped first; ties newest first
    [InlineData(OrderQueries.HistorySort.Shop, false, "3,2,1")] // one shop: ties fall back to newest first
    public async Task Order_history_sorts_by_every_column_across_all_pages(string column, bool descending, string expected)
    {
        var (shop, _) = await ListingsAsync();
        using var _s = shop;
        await using (var db = shop.Db.CreateDbContext())
        {
            // receipt, buyer, total (cents), first item, shipped, created
            db.Orders.AddRange(
                Order(1, "Zed", 2_000, "Mug", shipped: false, day: 1),
                Order(2, "amy", 9_000, "Vase", shipped: true, day: 2),
                Order(3, "Bob", 500, "Lamp", shipped: false, day: 3));
            await db.SaveChangesAsync();
        }
        var queries = shop.Provider.GetRequiredService<OrderQueries>();

        var receipts = new List<long>();
        for (var page = 0; page < 3; page++)
        {
            var (orders, _) = await queries.GetHistoryAsync(null, null, page, 1, CancellationToken.None, column, descending);
            receipts.Add(Assert.Single(orders).EtsyReceiptId);
        }

        Assert.Equal(expected, string.Join(",", receipts));
    }

    private static Order Order(long receipt, string buyer, long total, string item, bool shipped, int day) => new()
    {
        ShopId = FakeEtsyShop.ShopId, EtsyReceiptId = receipt, BuyerName = buyer, Grandtotal = total, Divisor = 100,
        Status = shipped ? "completed" : "paid", IsPaid = true, IsShipped = shipped,
        EtsyCreatedAtUtc = new DateTime(2026, 10, day, 0, 0, 0, DateTimeKind.Utc),
        Items = [new OrderItem { EtsyTransactionId = receipt * 10, Title = item, Quantity = 1 }],
    };
}
