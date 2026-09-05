using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Application;

public interface IConversionJobRepository
{
    Task SaveAsync(ConversionJob job, CancellationToken cancellationToken);
    Task<ConversionJob?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversionJob>> ListAsync(CancellationToken cancellationToken);
}
