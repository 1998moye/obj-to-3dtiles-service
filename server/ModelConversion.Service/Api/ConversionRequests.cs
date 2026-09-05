using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Api;

public sealed class CreateConversionRequest
{
    public required string InputPath { get; init; }
    public string? ReferenceLlaPath { get; init; }
    public string? OutputPath { get; init; }
    public string Profile { get; init; } = "industrial-jpeg";
    public GeoReference? GeoReference { get; init; }
    public ConversionOverrides? Overrides { get; init; }
}
