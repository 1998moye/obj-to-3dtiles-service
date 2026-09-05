using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Application;

public interface ITilesetValidator
{
    Task<TilesetValidationReport> ValidateAsync(string outputRoot, CancellationToken cancellationToken);
}
