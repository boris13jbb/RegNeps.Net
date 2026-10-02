using System.Text.Json;
using RegNeps.OfflineStore.Entities;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Sync.Contracts;

namespace RegNeps.OfflineStore.Services;

/// <summary>
/// Clasifica Conflict de forma inequívoca a partir de OperationType + error + forma del snapshot.
/// Snapshot JSON v1 = <see cref="ClientNepRecordSnapshot"/> o <see cref="ClientNepRecordDeletedSnapshot"/>.
/// </summary>
public static class ConflictKindClassifier
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static OfflineConflictKind Classify(PendingOperation op)
    {
        var deletedServer = IsServerDeleted(op);
        return op.OperationType switch
        {
            OfflineOperationType.UpdateRecord => deletedServer
                ? OfflineConflictKind.UpdateDelete
                : OfflineConflictKind.UpdateUpdate,
            OfflineOperationType.DeleteRecord => deletedServer
                ? OfflineConflictKind.DeleteDelete
                : OfflineConflictKind.DeleteUpdate,
            _ => OfflineConflictKind.Unknown
        };
    }

    public static bool IsServerDeleted(PendingOperation op)
    {
        if (string.Equals(op.LastServerErrorCode, "ENTITY_DELETED", StringComparison.OrdinalIgnoreCase)
            || string.Equals(op.LastServerErrorCode, "ENTITY_NOT_FOUND", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (TryParseTombstone(op.ConflictServerSnapshotJson, out _))
        {
            return true;
        }

        // Update/Delete Conflict con snapshot activo → servidor vivo.
        if (TryParseActiveSnapshot(op.ConflictServerSnapshotJson, out var active)
            && !string.IsNullOrWhiteSpace(active.ConcurrencyStamp))
        {
            return false;
        }

        // ENTITY_DELETED vía SyncEngine deja IsDeleted local sin snapshot útil.
        if (op.LocalNepRecord is { IsDeleted: true }
            && string.IsNullOrWhiteSpace(op.ConflictServerSnapshotJson)
            && op.OperationType == OfflineOperationType.UpdateRecord)
        {
            return true;
        }

        return false;
    }

    public static bool TryParseActiveSnapshot(string? json, out ClientNepRecordSnapshot snapshot)
    {
        snapshot = null!;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            // Tombstone: DeletedAtUtc sin Telar de negocio.
            if (root.TryGetProperty("deletedAtUtc", out _) || root.TryGetProperty("DeletedAtUtc", out _))
            {
                if (!root.TryGetProperty("telar", out _) && !root.TryGetProperty("Telar", out _))
                {
                    return false;
                }
            }

            var parsed = JsonSerializer.Deserialize<ClientNepRecordSnapshot>(json, JsonOpts);
            if (parsed is null || parsed.Id == Guid.Empty)
            {
                return false;
            }

            snapshot = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static bool TryParseTombstone(string? json, out ClientNepRecordDeletedSnapshot tombstone)
    {
        tombstone = null!;
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var hasDeleted = root.TryGetProperty("deletedAtUtc", out _)
                             || root.TryGetProperty("DeletedAtUtc", out _);
            var hasTelar = root.TryGetProperty("telar", out _) || root.TryGetProperty("Telar", out _);
            if (!hasDeleted || hasTelar)
            {
                return false;
            }

            var parsed = JsonSerializer.Deserialize<ClientNepRecordDeletedSnapshot>(json, JsonOpts);
            if (parsed is null || parsed.Id == Guid.Empty)
            {
                return false;
            }

            tombstone = parsed;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string KindLabel(OfflineConflictKind kind) => kind switch
    {
        OfflineConflictKind.UpdateUpdate => "Actualización vs actualización",
        OfflineConflictKind.UpdateDelete => "Actualización vs eliminación",
        OfflineConflictKind.DeleteUpdate => "Eliminación vs actualización",
        OfflineConflictKind.DeleteDelete => "Eliminación ya aplicada en servidor",
        _ => "Conflicto"
    };

    public static string ReasonLabel(PendingOperation op, OfflineConflictKind kind) => kind switch
    {
        OfflineConflictKind.UpdateUpdate => "Registro modificado por otro usuario o sesión.",
        OfflineConflictKind.UpdateDelete => "Registro eliminado en el servidor.",
        OfflineConflictKind.DeleteUpdate => "Registro modificado en el servidor; la eliminación local quedó desactualizada.",
        OfflineConflictKind.DeleteDelete => "El registro ya estaba eliminado en el servidor.",
        _ => string.IsNullOrWhiteSpace(op.LastServerErrorCode)
            ? "Concurrencia desactualizada."
            : op.LastServerErrorCode!
    };
}
