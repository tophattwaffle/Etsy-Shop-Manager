using System.Text.Json.Serialization;

namespace EtsyShopManager.Etsy.Models;

/// <summary>ShopReceipt (subset the app uses; the raw JSON is stored as well).</summary>
public sealed class EtsyReceipt
{
    [JsonPropertyName("receipt_id")] public long ReceiptId { get; set; }
    [JsonPropertyName("buyer_user_id")] public long BuyerUserId { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("status")] public string? Status { get; set; }
    [JsonPropertyName("message_from_buyer")] public string? MessageFromBuyer { get; set; }
    [JsonPropertyName("is_paid")] public bool IsPaid { get; set; }
    [JsonPropertyName("is_shipped")] public bool IsShipped { get; set; }
    [JsonPropertyName("created_timestamp")] public long? CreatedTimestamp { get; set; }
    [JsonPropertyName("updated_timestamp")] public long? UpdatedTimestamp { get; set; }
    [JsonPropertyName("is_gift")] public bool IsGift { get; set; }
    [JsonPropertyName("gift_message")] public string? GiftMessage { get; set; }
    [JsonPropertyName("gift_sender")] public string? GiftSender { get; set; }
    [JsonPropertyName("grandtotal")] public EtsyMoney? Grandtotal { get; set; }
    [JsonPropertyName("subtotal")] public EtsyMoney? Subtotal { get; set; }
    [JsonPropertyName("total_shipping_cost")] public EtsyMoney? TotalShippingCost { get; set; }
    [JsonPropertyName("total_tax_cost")] public EtsyMoney? TotalTaxCost { get; set; }
    [JsonPropertyName("discount_amt")] public EtsyMoney? DiscountAmt { get; set; }
    [JsonPropertyName("transactions")] public List<EtsyReceiptTransaction>? Transactions { get; set; }
}

/// <summary>ShopReceiptTransaction.</summary>
public sealed class EtsyReceiptTransaction
{
    [JsonPropertyName("transaction_id")] public long TransactionId { get; set; }
    [JsonPropertyName("title")] public string? Title { get; set; }
    [JsonPropertyName("receipt_id")] public long ReceiptId { get; set; }
    [JsonPropertyName("listing_id")] public long? ListingId { get; set; }
    [JsonPropertyName("product_id")] public long? ProductId { get; set; }
    [JsonPropertyName("listing_image_id")] public long? ListingImageId { get; set; }
    [JsonPropertyName("sku")] public string? Sku { get; set; }
    [JsonPropertyName("quantity")] public int Quantity { get; set; }
    [JsonPropertyName("price")] public EtsyMoney? Price { get; set; }
    [JsonPropertyName("created_timestamp")] public long? CreatedTimestamp { get; set; }
    [JsonPropertyName("expected_ship_date")] public long? ExpectedShipDate { get; set; }
    [JsonPropertyName("variations")] public List<EtsyTransactionVariation>? Variations { get; set; }
}

public sealed class EtsyTransactionVariation
{
    [JsonPropertyName("property_id")] public long PropertyId { get; set; }
    [JsonPropertyName("value_id")] public long? ValueId { get; set; }
    [JsonPropertyName("formatted_name")] public string? FormattedName { get; set; }
    [JsonPropertyName("formatted_value")] public string? FormattedValue { get; set; }
}
