using RegNeps.Domain.Enums;
using RegNeps.OfflineStore.Entities;

namespace RegNeps.OfflineStore.Models;

public sealed class OfflineCaptureResult
{
    public required LocalNepRecord Record { get; init; }
    public required PendingOperation Operation { get; init; }
    public AlertLevel QualityLevel { get; init; }
    public string QualityLabel { get; init; } = string.Empty;
}
