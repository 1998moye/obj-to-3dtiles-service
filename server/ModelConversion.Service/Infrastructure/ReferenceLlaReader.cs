using System.Text.Json;
using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Infrastructure;

public sealed class ReferenceLlaReader
{
    public async Task<GeoReference> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        var result = new GeoReference(
            root.GetProperty("latitude").GetDouble(),
            root.GetProperty("longitude").GetDouble(),
            root.GetProperty("altitude").GetDouble());
        result.EnsureValid();
        return result;
    }
}
