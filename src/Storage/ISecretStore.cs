namespace Linkedin.Console.Storage;

/// <summary>Anything bearer-shaped - today that is one LinkedIn token.</summary>
public interface ISecretStore
{
    /// <summary>Backend name, as reported by <c>linkedin auth status</c>.</summary>
    string Name { get; }

    string? Get(string key);
    void Set(string key, string value);
    void Delete(string key);
}
