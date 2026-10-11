using System.Dynamic;
using System.Text.Json;

namespace GAMIQ.Api.Helpers;

// System.Text.Json binds "object" values as JsonElement, and the Mongo driver refuses to
// serialize JsonElement. Convert them to plain values (string, int, double, bool, list, object)
// before inserting.
public static class JsonValues
{
    public static Dictionary<string, object> ToPlain(Dictionary<string, object>? source)
    {
        var result = new Dictionary<string, object>();
        if (source is null)
        {
            return result;
        }

        foreach (var (key, value) in source)
        {
            var plain = value is JsonElement element ? Convert(element) : value;
            if (plain is not null)
            {
                result[key] = plain;
            }
        }

        return result;
    }

    private static object? Convert(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                return element.GetString();
            case JsonValueKind.Number:
                if (element.TryGetInt32(out var i)) return i;
                if (element.TryGetInt64(out var l)) return l;
                return element.GetDouble();
            case JsonValueKind.True:
                return true;
            case JsonValueKind.False:
                return false;
            case JsonValueKind.Array:
                return element.EnumerateArray().Select(Convert).ToList();
            case JsonValueKind.Object:
                IDictionary<string, object?> obj = new ExpandoObject();
                foreach (var prop in element.EnumerateObject())
                {
                    obj[prop.Name] = Convert(prop.Value);
                }
                return obj;
            default:
                return null;
        }
    }
}
