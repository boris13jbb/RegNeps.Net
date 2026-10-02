using RegNeps.Domain.Entities;

namespace RegNeps.Application.Alerts;

public interface IAlertCriticalPublisher
{
    Task PublishNewCriticalAsync(NepRecord record, CancellationToken ct = default);
}
