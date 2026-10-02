using RegNeps.Domain.Sync;

namespace RegNeps.Application.Sync;

public static class SyncProtocol
{
    public const int Version = SyncConstants.ProtocolVersion;

    public static int NormalizePageSize(int? pageSize)
    {
        if (pageSize is null || pageSize <= 0)
        {
            return SyncConstants.DefaultPageSize;
        }

        return Math.Clamp(pageSize.Value, SyncConstants.MinPageSize, SyncConstants.MaxPageSize);
    }
}
