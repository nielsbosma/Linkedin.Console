using System.Text.Encodings.Web;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Linkedin.Console.Infrastructure;

/// <summary>Output for the commands that read a member's own data. Status goes to stderr, data to stdout.</summary>
public static class OutputHelper
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // LinkedIn data is full of names and messages in any language; keep them readable.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly ISerializer YamlSerializer = new SerializerBuilder()
        .WithNamingConvention(CamelCaseNamingConvention.Instance)
        .ConfigureDefaultValuesHandling(DefaultValuesHandling.OmitNull)
        .Build();

    public static void Write(object data, string format) =>
        System.Console.Out.WriteLine(Serialize(data, format));

    public static string Serialize(object data, string format)
    {
        var pruned = Prune(data) ?? new Dictionary<string, object?>();

        return format.Equals("json", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(pruned, JsonOptions)
            : YamlSerializer.Serialize(pruned).TrimEnd();
    }

    /// <summary>
    /// Drops null entries recursively. Both serializers' "omit null" settings apply to object
    /// properties only, and these payloads are dictionaries.
    /// </summary>
    private static object? Prune(object? value) => value switch
    {
        null => null,
        IDictionary<string, object?> map => map
            .Select(kv => (kv.Key, Value: Prune(kv.Value)))
            .Where(entry => entry.Value is not null)
            .ToDictionary(entry => entry.Key, entry => entry.Value),
        IEnumerable<object?> list => list.Select(Prune).Where(item => item is not null).ToList(),
        _ => value
    };

    public static void WriteError(LinkedInException ex, string format)
    {
        var payload = new Dictionary<string, object?>
        {
            ["error"] = ex.Message,
            ["code"] = ErrorCodes.Name(ex.Code)
        };
        if (!string.IsNullOrWhiteSpace(ex.Detail)) payload["detail"] = ex.Detail;
        if (!string.IsNullOrWhiteSpace(ex.Remediation)) payload["remediation"] = ex.Remediation;

        System.Console.Error.WriteLine(Serialize(payload, format));
    }

    public static void Status(string message) => System.Console.Error.WriteLine(message);
}
