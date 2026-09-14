using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Application;

public interface IConversionJobRepository
{
    Task SaveAsync(ConversionJob job, CancellationToken cancellationToken);
    Task<ConversionJob?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversionJob>> ListAsync(CancellationToken cancellationToken);
    // [2026-09-09 Issue01 终态清理] 仅供 TTL 清理工作器删除已过保留期的终态作业状态文件；
    // 不是业务删除 API，运行中作业由调用方二次读取保护。
    Task DeleteAsync(Guid id, CancellationToken cancellationToken);
}
