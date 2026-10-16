using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EtsyShopManager.Data;

public static class JsonConversion
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Stores a value as a JSON text column. For structured values the app reads and writes as a whole
    /// (e.g. variation property values); use primitive collections where SQL needs to look inside.
    /// </summary>
    public static PropertyBuilder<T> HasJsonConversion<T>(this PropertyBuilder<T> property) where T : class, new()
    {
        property.HasConversion(
            v => JsonSerializer.Serialize(v, Options),
            v => string.IsNullOrEmpty(v) ? new T() : JsonSerializer.Deserialize<T>(v, Options) ?? new T(),
            new ValueComparer<T>(
                (a, b) => JsonSerializer.Serialize(a, Options) == JsonSerializer.Serialize(b, Options),
                v => JsonSerializer.Serialize(v, Options).GetHashCode(),
                v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Options), Options)!));
        return property;
    }
}
