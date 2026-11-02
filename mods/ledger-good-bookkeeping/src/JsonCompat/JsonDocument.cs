using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AIWorldPawnshop.JsonCompat;

internal enum JsonValueKind
{
    Undefined,
    Object,
    Array,
    String,
    Number,
    True,
    False,
    Null
}

internal sealed class JsonDocument : IDisposable
{
    private readonly JToken _root;

    private JsonDocument(JToken root) => _root = root;

    public JsonElement RootElement => new(_root);

    public static JsonDocument Parse(string json)
    {
        using var input = new StringReader(json);
        using var reader = new JsonTextReader(input) { DateParseHandling = DateParseHandling.None };
        var root = JToken.ReadFrom(reader);
        if (reader.Read())
            throw new JsonReaderException("Unexpected content after JSON value.");
        return new JsonDocument(root);
    }

    public void Dispose() { }
}

internal readonly struct JsonElement
{
    private readonly JToken? _token;

    public JsonElement(JToken? token) => _token = token;

    public JsonValueKind ValueKind => _token?.Type switch
    {
        null or JTokenType.Undefined => JsonValueKind.Undefined,
        JTokenType.Object => JsonValueKind.Object,
        JTokenType.Array => JsonValueKind.Array,
        JTokenType.String => JsonValueKind.String,
        JTokenType.Integer or JTokenType.Float => JsonValueKind.Number,
        JTokenType.Boolean when _token.Value<bool>() => JsonValueKind.True,
        JTokenType.Boolean => JsonValueKind.False,
        JTokenType.Null => JsonValueKind.Null,
        _ => JsonValueKind.String
    };

    public JsonElement this[int index] => new(((JArray)_token!)[index]);

    public int GetArrayLength() => ((JArray)_token!).Count;

    public IEnumerable<JsonElement> EnumerateArray() =>
        ((JArray)_token!).Select(item => new JsonElement(item));

    public IEnumerable<JsonProperty> EnumerateObject() =>
        ((JObject)_token!).Properties().Select(property =>
            new JsonProperty(property.Name, new JsonElement(property.Value)));

    public bool TryGetProperty(string name, out JsonElement value)
    {
        if (_token is JObject obj && obj.TryGetValue(name, out var token))
        {
            value = new JsonElement(token);
            return true;
        }
        value = default;
        return false;
    }

    public JsonElement GetProperty(string name) =>
        TryGetProperty(name, out var value)
            ? value
            : throw new KeyNotFoundException($"JSON property '{name}' was not found.");

    public string? GetString() => ValueKind switch
    {
        JsonValueKind.String => _token!.Value<string>(),
        JsonValueKind.Null => null,
        _ => throw new InvalidOperationException("JSON value is not a string.")
    };

    public bool GetBoolean() => ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => throw new InvalidOperationException("JSON value is not a boolean.")
    };

    public bool TryGetInt32(out int value) =>
        int.TryParse(NumberText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public bool TryGetInt64(out long value) =>
        long.TryParse(NumberText(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public bool TryGetDouble(out double value) =>
        double.TryParse(NumberText(), NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private string? NumberText() => ValueKind == JsonValueKind.Number
        ? _token!.ToString(Formatting.None)
        : null;

    public override string ToString() => ValueKind == JsonValueKind.String
        ? GetString() ?? string.Empty
        : _token?.ToString(Formatting.None) ?? string.Empty;
}

internal readonly struct JsonProperty
{
    public JsonProperty(string name, JsonElement value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public JsonElement Value { get; }
}
