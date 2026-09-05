using ModelConversion.Service.Domain;

namespace ModelConversion.Service.Api;

public sealed record ConversionJobResponse
{
    public required Guid Id { get; init; }
    public required string InputRelativePath { get; init; }
    public string? ReferenceLlaRelativePath { get; init; }
    public required string OutputRelativePath { get; init; }
    public required string ProfileName { get; init; }
    public ConversionSettings? SettingsSnapshot { get; init; }
    public GeoReference? GeoReference { get; init; }
    public required ConversionJobState State { get; init; }
    public int Attempt { get; init; }
    public Guid? PreviousJobId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? CompletedAt { get; init; }
    public int? ExitCode { get; init; }
    public string? Diagnostic { get; init; }
    public TilesetValidationReport? Validation { get; init; }
    public string? TilesetUrl { get; init; }
    public required ConversionProgressSnapshot Progress { get; init; }
    public bool CanCancel { get; init; }

    public static ConversionJobResponse FromJob(ConversionJob job, ConversionProgressSnapshot progress) => new()
    {
        Id = job.Id,
        InputRelativePath = job.InputRelativePath,
        ReferenceLlaRelativePath = job.ReferenceLlaRelativePath,
        OutputRelativePath = job.OutputRelativePath,
        ProfileName = job.ProfileName,
        SettingsSnapshot = job.SettingsSnapshot,
        GeoReference = job.GeoReference,
        State = job.State,
        Attempt = job.Attempt,
        PreviousJobId = job.PreviousJobId,
        CreatedAt = job.CreatedAt,
        UpdatedAt = job.UpdatedAt,
        StartedAt = job.StartedAt,
        CompletedAt = job.FinishedAt,
        ExitCode = job.ExitCode,
        Diagnostic = job.Diagnostic,
        Validation = job.Validation,
        Progress = progress,
        CanCancel = !job.IsTerminal,
        TilesetUrl = job.State == ConversionJobState.Succeeded
            ? $"/api/v1/conversions/{job.Id}/result/tileset.json"
            : null
    };
}

public sealed record ConversionStatusResponse(
    Guid Id,
    ConversionJobState State,
    bool CanCancel,
    string? Diagnostic,
    DateTimeOffset UpdatedAt,
    ConversionProgressSnapshot Progress);

public sealed record ConversionProgressResponse(
    Guid Id,
    ConversionJobState State,
    int Percent,
    ConversionProgressStage Stage,
    string? Message,
    DateTimeOffset UpdatedAt);
