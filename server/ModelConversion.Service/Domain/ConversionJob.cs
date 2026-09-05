namespace ModelConversion.Service.Domain;

public sealed record ConversionJob
{
    private static readonly IReadOnlyDictionary<ConversionJobState, ConversionJobState[]> AllowedTransitions =
        new Dictionary<ConversionJobState, ConversionJobState[]>
        {
            [ConversionJobState.Queued] = [ConversionJobState.Running, ConversionJobState.Canceled],
            [ConversionJobState.Running] = [ConversionJobState.Validating, ConversionJobState.Failed, ConversionJobState.Canceled],
            [ConversionJobState.Validating] = [ConversionJobState.Succeeded, ConversionJobState.Failed, ConversionJobState.Canceled]
        };

    public required Guid Id { get; init; }
    public required string InputRelativePath { get; init; }
    public string? ReferenceLlaRelativePath { get; init; }
    public required string OutputRelativePath { get; init; }
    public required string ProfileName { get; init; }
    public ConversionSettings? SettingsSnapshot { get; init; }
    public GeoReference? GeoReference { get; init; }
    public ConversionJobState State { get; init; } = ConversionJobState.Queued;
    public int Attempt { get; init; } = 1;
    public Guid? PreviousJobId { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset UpdatedAt { get; init; }
    public DateTimeOffset? StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public int? ExitCode { get; init; }
    public string? Diagnostic { get; init; }
    public TilesetValidationReport? Validation { get; init; }

    public bool IsTerminal => State is ConversionJobState.Succeeded or ConversionJobState.Failed or ConversionJobState.Canceled;

    public ConversionJob TransitionTo(
        ConversionJobState next,
        DateTimeOffset now,
        string? diagnostic = null,
        int? exitCode = null,
        TilesetValidationReport? validation = null)
    {
        if (!AllowedTransitions.TryGetValue(State, out var allowed) || !allowed.Contains(next))
            throw new InvalidOperationException($"非法任务状态迁移: {State} -> {next}");

        return this with
        {
            State = next,
            UpdatedAt = now,
            StartedAt = next == ConversionJobState.Running ? now : StartedAt,
            FinishedAt = next is ConversionJobState.Succeeded or ConversionJobState.Failed or ConversionJobState.Canceled ? now : null,
            Diagnostic = diagnostic,
            ExitCode = exitCode,
            Validation = validation
        };
    }

    public ConversionJob RecoverToQueue(DateTimeOffset now) => this with
    {
        State = ConversionJobState.Queued,
        UpdatedAt = now,
        StartedAt = null,
        FinishedAt = null,
        ExitCode = null,
        Diagnostic = "服务重启后恢复未完成任务",
        Validation = null
    };
}

public sealed record TilesetValidationReport(
    int TilesetCount,
    int TileCount,
    int ContentCount,
    long TotalBytes,
    double RootGeometricError);
