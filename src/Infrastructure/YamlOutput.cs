using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Linkedin.Console.Infrastructure;

public static class YamlOutput
{
    private static readonly ISerializer Serializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .Build();

    public static void Write(JsonDocument doc)
    {
        var obj = JsonToObject(doc.RootElement);
        System.Console.WriteLine(Serializer.Serialize(obj).TrimEnd());
    }

    public static void Write(object? obj, HashSet<string>? includeFields)
    {
        if (includeFields != null && obj is List<object?> list)
        {
            foreach (var item in list)
                FilterProfileFields(item, includeFields);
        }
        else if (includeFields != null)
        {
            FilterProfileFields(obj, includeFields);
        }

        System.Console.WriteLine(Serializer.Serialize(obj!).TrimEnd());
    }

    public static object? JsonToObject(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => element.EnumerateObject()
            .ToDictionary(p => p.Name, p => JsonToObject(p.Value)),
        JsonValueKind.Array => element.EnumerateArray().Select(JsonToObject).ToList(),
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null
    };

    private static readonly HashSet<string> OptionalFields = new(StringComparer.OrdinalIgnoreCase)
    {
        "experience", "education", "skills", "certifications", "projects",
        "volunteering", "publications", "courses", "honorsAndAwards", "languages",
        "causes", "featured", "receivedRecommendations", "moreProfiles"
    };

    private static void FilterProfileFields(object? data, HashSet<string> includeFields)
    {
        if (data is List<object?> list)
        {
            foreach (var item in list)
                FilterProfileFields(item, includeFields);
        }
        else if (data is Dictionary<string, object?> dict)
        {
            var keysToRemove = dict.Keys
                .Where(key => OptionalFields.Contains(key) && !includeFields.Contains(key))
                .ToList();

            foreach (var key in keysToRemove)
                dict.Remove(key);

            foreach (var value in dict.Values.ToList())
                FilterProfileFields(value, includeFields);
        }
    }
}
