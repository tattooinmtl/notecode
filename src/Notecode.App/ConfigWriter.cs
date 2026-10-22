using System.Text.Json;
using System.Text.Json.Nodes;

namespace Notecode.App;

/// <summary>
/// Writes values into notecode.config.json by path, keeping everything else in the
/// file as it is - the other keys, their order and the "$comment" notes.
///
/// A path is dotted: "llama.context.ctxSize", "mcp.servers.rea.enabled". A missing
/// object on the way is created.
/// </summary>
internal sealed class ConfigWriter
{
    private readonly string _path;
    private readonly JsonNode _root;

    public ConfigWriter(string path)
    {
        _path = path;
        _root = JsonNode.Parse(File.ReadAllText(path)) ?? throw new InvalidDataException("the config is empty");
    }

    public ConfigWriter Set(string dotted, JsonNode? value)
    {
        var parts = dotted.Split('.');
        var node = _root.AsObject();
        foreach (var part in parts[..^1])
        {
            if (node[part] is not JsonObject child)
            {
                child = new JsonObject();
                node[part] = child;
            }
            node = child;
        }
        node[parts[^1]] = value;
        return this;
    }

    public ConfigWriter Set(string dotted, IEnumerable<string> values) =>
        Set(dotted, new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()));

    public void Save() =>
        File.WriteAllText(_path, _root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
}
