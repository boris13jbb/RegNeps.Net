using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;

namespace RegNeps.Application.Records;

/// <summary>
/// Reglas puras de validación y selección en captura (sin I/O).
/// </summary>
public static class CaptureValidationRules
{
    public static bool RequiresHighNepsConfirmation(double neps) =>
        neps > CaptureValidationConstants.NepsConfirmationThreshold;

    /// <summary>
    /// Duplicado reciente: mismo usuario, telar, tela, lote, neps y registro existente dentro de la ventana.
    /// </summary>
    public static bool IsRecentDuplicate(
        string candidateUserId,
        string candidateTelar,
        string candidateTela,
        string candidateLoteTrama,
        double candidateNeps,
        NepRecord existing,
        DateTime nowUtc,
        int windowMinutes,
        string? candidateExternalUserId = null)
    {
        ArgumentNullException.ThrowIfNull(existing);
        if (string.IsNullOrWhiteSpace(candidateUserId))
        {
            return false;
        }

        if (windowMinutes <= 0)
        {
            return false;
        }

        var windowStart = nowUtc.AddMinutes(-windowMinutes);
        if (existing.CreatedAt < windowStart || existing.CreatedAt > nowUtc)
        {
            return false;
        }

        if (!SameUser(candidateUserId, existing.CreatedByUserId)
            && !SameUser(candidateExternalUserId, existing.CreatedByUserId))
        {
            return false;
        }

        return string.Equals(NormalizeTelar(candidateTelar), NormalizeTelar(existing.Telar), StringComparison.OrdinalIgnoreCase)
               && string.Equals(NormalizeText(candidateTela), NormalizeText(existing.Tela), StringComparison.OrdinalIgnoreCase)
               && string.Equals(NormalizeLote(candidateLoteTrama), NormalizeLote(existing.LoteTrama), StringComparison.Ordinal)
               && candidateNeps.Equals(existing.Neps);
    }

    /// <summary>
    /// Prioridad para compartir del día: último creado → selección → más reciente del día del usuario.
    /// </summary>
    public static IReadOnlyList<Guid> ResolveShareTodayIds(
        Guid? lastCreatedId,
        IEnumerable<Guid> selectedIds,
        IEnumerable<(Guid Id, DateTime CreatedAt)> todayUserRecords)
    {
        if (lastCreatedId is { } last && last != Guid.Empty)
        {
            return [last];
        }

        var selected = (selectedIds ?? [])
            .Where(id => id != Guid.Empty)
            .Distinct()
            .ToList();
        if (selected.Count > 0)
        {
            return selected;
        }

        var mostRecent = (todayUserRecords ?? [])
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => r.Id)
            .FirstOrDefault();
        return mostRecent == Guid.Empty ? Array.Empty<Guid>() : [mostRecent];
    }

    /// <summary>
    /// IDs de la sesión aún no persistidos; <paramref name="preferLastPending"/> primero si sigue pendiente.
    /// </summary>
    public static IReadOnlyList<Guid> PendingCaptureReportIds(
        IEnumerable<Guid> sessionIds,
        HashSet<Guid> alreadySaved,
        Guid? preferLastPending)
    {
        ArgumentNullException.ThrowIfNull(alreadySaved);
        var pending = (sessionIds ?? [])
            .Where(id => id != Guid.Empty && !alreadySaved.Contains(id))
            .ToList();
        if (pending.Count == 0)
        {
            return Array.Empty<Guid>();
        }

        if (preferLastPending is { } prefer
            && prefer != Guid.Empty
            && pending.Contains(prefer))
        {
            var ordered = new List<Guid>(pending.Count) { prefer };
            foreach (var id in pending)
            {
                if (id != prefer)
                {
                    ordered.Add(id);
                }
            }

            return ordered;
        }

        return pending;
    }

    private static bool SameUser(string? candidateUserId, string? existingUserId)
    {
        if (string.IsNullOrWhiteSpace(candidateUserId) || string.IsNullOrWhiteSpace(existingUserId))
        {
            return false;
        }

        return string.Equals(candidateUserId.Trim(), existingUserId.Trim(), StringComparison.Ordinal);
    }

    private static string NormalizeTelar(string value) => value.Trim();

    private static string NormalizeText(string value) => value.Trim();

    private static string NormalizeLote(string value) =>
        string.IsNullOrWhiteSpace(value)
            ? NepsConstants.LoteTramaPrefix
            : value.Trim().ToUpperInvariant();
}
