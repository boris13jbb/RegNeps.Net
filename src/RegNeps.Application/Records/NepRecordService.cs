using RegNeps.Application.Abstractions;
using RegNeps.Application.Permissions;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using RegNeps.Domain.Permissions;
using RegNeps.Domain.Services;

namespace RegNeps.Application.Records;

public sealed class CorrectiveActionRequest
{
    public Guid RecordId { get; set; }
    public string Accion { get; set; } = string.Empty;
    public string Responsable { get; set; } = string.Empty;
    public bool MarcarRevisado { get; set; } = true;
}

public sealed class NepRecordService
{
    private readonly INepRecordRepository _records;
    private readonly IAlertConfigRepository _alertConfig;
    private readonly IPermissionService? _permissions;

    public NepRecordService(
        INepRecordRepository records,
        IAlertConfigRepository alertConfig,
        IPermissionService? permissions = null)
    {
        _records = records;
        _alertConfig = alertConfig;
        _permissions = permissions;
    }

    /// <summary>
    /// Usa la matriz persistida cuando el servicio está registrado. Sin él, las pruebas
    /// siguen la matriz inicial para no exigir base de permisos en cada caso.
    /// </summary>
    private bool ActorHas(RecordActor actor, AppPermission permission) =>
        _permissions?.HasPermission(actor.Role, actor.IsSuperAdmin, true, permission)
        ?? actor.Has(permission);

    public async Task<NepRecord> CreateAsync(
        CreateNepRecordRequest request,
        RecordActor actor,
        CancellationToken ct = default)
    {
        EnsureAuthenticated(actor);
        if (!ActorHas(actor, AppPermission.CaptureRecords))
        {
            throw new UnauthorizedRecordAccessException("No tiene permiso para capturar registros.");
        }

        Validate(request);

        var opId = string.IsNullOrWhiteSpace(request.ClientOperationId)
            ? null
            : request.ClientOperationId.Trim();

        var sessionId = string.IsNullOrWhiteSpace(request.CaptureSessionId)
            ? null
            : request.CaptureSessionId.Trim();

        if (opId is not null)
        {
            var existing = await _records.FindByClientOperationAsync(actor.UserId, opId, ct);
            if (existing is not null)
            {
                // El reintento debe pertenecer a la misma sesión de captura del usuario.
                if (sessionId is not null
                    && !string.Equals(existing.CaptureSessionId, sessionId, StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(existing.CaptureSessionId))
                {
                    throw new UnauthorizedRecordAccessException(
                        "La operación no pertenece a la sesión de captura actual.");
                }

                return existing;
            }
        }

        var record = new NepRecord
        {
            Id = Guid.NewGuid(),
            Telar = request.Telar.Trim(),
            Neps = request.Neps,
            Tela = request.Tela.Trim(),
            LoteTrama = string.IsNullOrWhiteSpace(request.LoteTrama)
                ? NepsConstants.LoteTramaPrefix
                : request.LoteTrama.Trim().ToUpperInvariant(),
            Turno = request.Turno.Trim(),
            Operario = request.Operario.Trim(),
            LineaProduccion = request.LineaProduccion.Trim(),
            Observacion = request.Observacion.Trim(),
            CreatedAt = DateTime.UtcNow,
            CreatedByUserId = actor.UserId,
            CreatedByEmail = actor.Username,
            CreatedByRole = actor.EffectiveRole.ToString(),
            ClientOperationId = opId,
            CaptureSessionId = sessionId,
            ConcurrencyStamp = Guid.NewGuid().ToString("N")
        };

        return await _records.AddAsync(record, ct);
    }

    /// <summary>
    /// Crea el registro y, si es posible, evalúa alertas sin confundir fallos posteriores con fallo de guardado.
    /// </summary>
    public async Task<RecordSaveResult> CreateWithOutcomeAsync(
        CreateNepRecordRequest request,
        RecordActor actor,
        CancellationToken ct = default)
    {
        try
        {
            EnsureAuthenticated(actor);
            if (!ActorHas(actor, AppPermission.CaptureRecords))
            {
                return new RecordSaveResult
                {
                    Status = RecordSaveStatus.Unauthorized,
                    Error = "No tiene permiso para capturar registros."
                };
            }

            Validate(request);
        }
        catch (UnauthorizedRecordAccessException ex)
        {
            return new RecordSaveResult { Status = RecordSaveStatus.Unauthorized, Error = ex.Message };
        }
        catch (ArgumentException ex)
        {
            return new RecordSaveResult { Status = RecordSaveStatus.ValidationFailed, Error = ex.Message };
        }

        var opId = string.IsNullOrWhiteSpace(request.ClientOperationId)
            ? null
            : request.ClientOperationId.Trim();
        if (opId is not null)
        {
            var existing = await _records.FindByClientOperationAsync(actor.UserId, opId, ct);
            if (existing is not null)
            {
                return await BuildSavedResultAsync(existing, alreadySaved: true, ct);
            }
        }

        NepRecord saved;
        try
        {
            saved = await CreateAsync(request, actor, ct);
        }
        catch (UnauthorizedRecordAccessException ex)
        {
            return new RecordSaveResult { Status = RecordSaveStatus.Unauthorized, Error = ex.Message };
        }
        catch (ArgumentException ex)
        {
            return new RecordSaveResult { Status = RecordSaveStatus.ValidationFailed, Error = ex.Message };
        }
        catch (Exception ex)
        {
            return new RecordSaveResult
            {
                Status = RecordSaveStatus.PersistenceFailed,
                Error = ex.Message
            };
        }

        // Tras insert: Saved. Idempotencia previa ya devolvió AlreadySaved.
        return await BuildSavedResultAsync(saved, alreadySaved: false, ct);
    }

    private async Task<RecordSaveResult> BuildSavedResultAsync(
        NepRecord saved,
        bool alreadySaved,
        CancellationToken ct)
    {
        AlertLevel? level = null;
        var alertFailed = false;
        try
        {
            var eval = await EvaluateAsync(saved.Neps, saved.Telar, ct);
            level = eval.Level;
        }
        catch
        {
            alertFailed = true;
        }

        return new RecordSaveResult
        {
            Status = alreadySaved ? RecordSaveStatus.AlreadySaved : RecordSaveStatus.Saved,
            Record = saved,
            AlertLevel = level,
            AlertEvaluationFailed = alertFailed
        };
    }

    public async Task<NepRecord> UpdateAsync(
        UpdateNepRecordRequest request,
        RecordActor actor,
        CancellationToken ct = default)
    {
        EnsureAuthenticated(actor);
        if (!ActorHas(actor, AppPermission.EditRecords))
        {
            throw new UnauthorizedRecordAccessException("No tiene permiso para editar registros.");
        }

        if (string.IsNullOrWhiteSpace(request.Telar))
            throw new ArgumentException("El telar es obligatorio.", nameof(request.Telar));
        if (request.Neps <= 0)
            throw new ArgumentException("Los neps deben ser mayores que cero.", nameof(request.Neps));

        var record = await _records.GetByIdAsync(request.Id, ct)
            ?? throw new InvalidOperationException("Registro no encontrado.");

        EnsureCanMutate(actor, record);

        if (!string.IsNullOrWhiteSpace(request.ExpectedConcurrencyStamp)
            && !string.Equals(record.ConcurrencyStamp, request.ExpectedConcurrencyStamp, StringComparison.Ordinal))
        {
            throw new RecordConcurrencyConflictException(
                "Este registro fue modificado por otro usuario. Recargue y revise antes de guardar.");
        }

        record.Telar = request.Telar.Trim();
        record.Neps = request.Neps;
        record.Tela = request.Tela.Trim();
        record.LoteTrama = string.IsNullOrWhiteSpace(request.LoteTrama)
            ? NepsConstants.LoteTramaPrefix
            : request.LoteTrama.Trim().ToUpperInvariant();
        record.Turno = request.Turno.Trim();
        record.Operario = request.Operario.Trim();
        record.LineaProduccion = request.LineaProduccion.Trim();
        record.Observacion = request.Observacion.Trim();
        record.UpdatedAt = DateTime.UtcNow;

        try
        {
            await _records.UpdateAsync(record, ct);
        }
        catch (RecordConcurrencyConflictException)
        {
            throw;
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("modificado por otro", StringComparison.OrdinalIgnoreCase)
            || ex.Message.Contains("concurrency", StringComparison.OrdinalIgnoreCase))
        {
            throw new RecordConcurrencyConflictException(
                "Este registro fue modificado por otro usuario. Recargue y revise antes de guardar.");
        }

        return record;
    }

    public Task<IReadOnlyList<NepRecord>> GetRecentAsync(int take = 100, CancellationToken ct = default) =>
        _records.GetRecentAsync(take, ct);

    public Task<IReadOnlyList<NepRecord>> QueryAsync(
        RecordFilters filters,
        RecordActor actor,
        RecordQueryScope scope = RecordQueryScope.Default,
        int take = 500,
        CancellationToken ct = default)
    {
        EnsureAuthenticated(actor);
        if (!ActorHas(actor, AppPermission.ViewRecords) && !ActorHas(actor, AppPermission.CaptureRecords))
        {
            throw new UnauthorizedRecordAccessException("No tiene permiso para consultar registros.");
        }

        var seesAll = scope == RecordQueryScope.PersonalOnly
            ? false
            : actor.SeesAllRecords;

        if (!seesAll && string.IsNullOrWhiteSpace(actor.UserId))
        {
            throw new UnauthorizedRecordAccessException(
                "Se requiere un usuario autenticado con identificador válido.");
        }

        return _records.QueryAsync(filters, actor.UserId, seesAll, take, ct);
    }

    /// <summary>Compatibilidad interna/tests: consulta con parámetros explícitos (fail-closed en repo).</summary>
    public Task<IReadOnlyList<NepRecord>> QueryAsync(
        RecordFilters filters,
        string? viewerUserId,
        bool viewerSeesAll,
        int take = 500,
        CancellationToken ct = default) =>
        _records.QueryAsync(filters, viewerUserId, viewerSeesAll, take, ct);

    public async Task<(AlertLevel Level, IReadOnlyList<string> Recommendations, bool Reincidencia)> EvaluateAsync(
        double neps,
        string? telar = null,
        CancellationToken ct = default)
    {
        var config = await _alertConfig.GetAsync(ct);
        var level = AlertEvaluator.GetLevel(neps, config);
        var reincidencia = false;
        if (!string.IsNullOrWhiteSpace(telar) && level == AlertLevel.Critico)
        {
            var recent = await _records.GetRecentAsync(500, ct);
            reincidencia = AlertEvaluator.HasCriticalRecurrence(recent, telar, config);
        }

        return (level, AlertEvaluator.GetRecommendations(level, reincidencia), reincidencia);
    }

    public async Task ApplyCorrectiveAsync(
        CorrectiveActionRequest request,
        RecordActor actor,
        CancellationToken ct = default)
    {
        EnsureAuthenticated(actor);
        if (!ActorHas(actor, AppPermission.ApplyCorrectiveAction))
        {
            throw new UnauthorizedRecordAccessException("No tiene permiso para acciones correctivas.");
        }

        if (string.IsNullOrWhiteSpace(request.Accion))
        {
            throw new ArgumentException("La acción correctiva es obligatoria.");
        }

        var record = await _records.GetByIdAsync(request.RecordId, ct)
            ?? throw new InvalidOperationException("Registro no encontrado.");

        EnsureCanMutate(actor, record, requireEditPermission: false);

        var entry = new CorrectiveActionEntry
        {
            NepRecordId = record.Id,
            Accion = request.Accion.Trim(),
            Responsable = request.Responsable.Trim(),
            Fecha = DateTime.UtcNow
        };
        record.HistorialAcciones.Add(entry);
        record.AccionCorrectiva = entry.Accion;
        record.ResponsableRevision = entry.Responsable;
        if (request.MarcarRevisado)
        {
            record.RevisadoPorSupervisor = true;
            record.FechaRevision = DateTime.UtcNow;
        }

        await _records.UpdateAsync(record, ct);
    }

    public async Task DeleteAsync(Guid id, RecordActor actor, CancellationToken ct = default)
    {
        EnsureAuthenticated(actor);
        if (!ActorHas(actor, AppPermission.DeleteRecords))
        {
            throw new UnauthorizedRecordAccessException("No tiene permiso para eliminar registros.");
        }

        var record = await _records.GetByIdAsync(id, ct)
            ?? throw new InvalidOperationException("Registro no encontrado.");

        EnsureCanMutate(actor, record, requireEditPermission: false);
        await _records.DeleteAsync(id, ct);
    }

    public async Task ClearAllAsync(RecordActor actor, CancellationToken ct = default)
    {
        EnsureAuthenticated(actor);
        if (!ActorHas(actor, AppPermission.ClearAllRecords))
        {
            throw new UnauthorizedRecordAccessException("No tiene permiso para vaciar registros.");
        }

        await _records.ClearAllAsync(ct);
    }

    public Task<int> CountAllAsync(CancellationToken ct = default) => _records.CountAsync(ct);

    public async Task<DashboardSummary> GetDashboardSummaryAsync(
        RecordActor actor,
        int take = 100,
        CancellationToken ct = default)
    {
        EnsureAuthenticated(actor);
        var config = await _alertConfig.GetAsync(ct);
        var records = await QueryAsync(new RecordFilters(), actor, RecordQueryScope.Default, take, ct);
        var total = records.Count;
        var sumNeps = records.Sum(r => r.Neps);
        var sumMts = records.Sum(r => r.MtsCalculados);
        var criticos = records.Count(r => r.GetAlertLevel(config) == AlertLevel.Critico);
        var advertencias = records.Count(r => r.GetAlertLevel(config) == AlertLevel.Advertencia);
        var pendientes = records.Count(r => r.RequiereSeguimiento(config));

        return new DashboardSummary
        {
            TotalRegistros = total,
            PromedioNeps = total == 0 ? 0 : sumNeps / total,
            TotalMts = sumMts,
            Criticos = criticos,
            Advertencias = advertencias,
            PendientesRevision = pendientes,
            Ultimos = records.Take(10).ToList()
        };
    }

    public async Task<IReadOnlyList<NepRecord>> GetAlertsAsync(
        RecordActor actor,
        CancellationToken ct = default)
    {
        EnsureAuthenticated(actor);
        if (!ActorHas(actor, AppPermission.ViewAlerts) && !ActorHas(actor, AppPermission.ViewRecords))
        {
            throw new UnauthorizedRecordAccessException("No tiene permiso para ver alertas.");
        }

        var config = await _alertConfig.GetAsync(ct);
        var all = await QueryAsync(new RecordFilters(), actor, RecordQueryScope.Default, 1000, ct);
        return all
            .Where(r => r.GetAlertLevel(config) != AlertLevel.Normal)
            .OrderByDescending(r => r.GetAlertLevel(config))
            .ThenByDescending(r => r.CreatedAt)
            .ToList();
    }

    /// <summary>
    /// Resuelve registros para compartir exclusivamente por ID.
    /// No expandirá por CaptureSessionId, fecha, telar, lote ni listado de sesión.
    /// </summary>
    public async Task<IReadOnlyList<NepRecord>> GetAccessibleByIdsAsync(
        IEnumerable<Guid> recordIds,
        RecordActor actor,
        CancellationToken ct = default)
    {
        EnsureAuthenticated(actor);
        if (!ActorHas(actor, AppPermission.ViewRecords) && !ActorHas(actor, AppPermission.CaptureRecords))
        {
            throw new UnauthorizedRecordAccessException("No tiene permiso para consultar registros.");
        }

        var orderedUnique = new List<Guid>();
        var seen = new HashSet<Guid>();
        foreach (var id in recordIds ?? Array.Empty<Guid>())
        {
            if (id == Guid.Empty || !seen.Add(id))
            {
                continue;
            }

            orderedUnique.Add(id);
        }

        if (orderedUnique.Count == 0)
        {
            return Array.Empty<NepRecord>();
        }

        var result = new List<NepRecord>(orderedUnique.Count);
        foreach (var id in orderedUnique)
        {
            var record = await _records.GetByIdAsync(id, ct);
            if (record is null)
            {
                continue;
            }

            // Solo acceso explícito por Id: nunca expandir por CaptureSessionId u otros filtros.
            if (!actor.SeesAllRecords && !OwnsRecord(actor, record))
            {
                continue;
            }

            result.Add(record);
        }

        return result;
    }

    /// <summary>Construye el texto de compartir solo para los IDs solicitados y accesibles.</summary>
    public async Task<string> BuildShareTextForIdsAsync(
        IEnumerable<Guid> recordIds,
        RecordActor actor,
        CancellationToken ct = default)
    {
        var records = await GetAccessibleByIdsAsync(recordIds, actor, ct);
        return RecordShareFormatter.Format(records);
    }

    public bool OwnsRecord(RecordActor actor, NepRecord record) =>
        OwnsRecord(actor, record.CreatedByUserId);

    public bool OwnsRecord(RecordActor actor, string? createdByUserId)
    {
        if (string.IsNullOrWhiteSpace(createdByUserId))
        {
            return false;
        }

        if (string.Equals(createdByUserId, actor.UserId, StringComparison.Ordinal))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(actor.ExternalUserId)
               && string.Equals(createdByUserId, actor.ExternalUserId, StringComparison.Ordinal);
    }

    private void EnsureCanMutate(
        RecordActor actor,
        NepRecord record,
        bool requireEditPermission = true)
    {
        if (requireEditPermission && !ActorHas(actor, AppPermission.EditRecords)
            && !ActorHas(actor, AppPermission.DeleteRecords)
            && !ActorHas(actor, AppPermission.ApplyCorrectiveAction))
        {
            throw new UnauthorizedRecordAccessException("No tiene permiso sobre este registro.");
        }

        if (actor.SeesAllRecords)
        {
            return;
        }

        if (!OwnsRecord(actor, record))
        {
            throw new UnauthorizedRecordAccessException(
                "No puede modificar registros de otro usuario.");
        }
    }

    private static void EnsureAuthenticated(RecordActor actor)
    {
        if (string.IsNullOrWhiteSpace(actor.UserId))
        {
            throw new UnauthorizedRecordAccessException(
                "Se requiere un usuario autenticado con identificador válido.");
        }
    }

    private static void Validate(CreateNepRecordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Telar))
        {
            throw new ArgumentException("El telar es obligatorio.", nameof(request.Telar));
        }

        if (request.Neps <= 0)
        {
            throw new ArgumentException("Los neps deben ser mayores que cero.", nameof(request.Neps));
        }
    }
}

public sealed class DashboardSummary
{
    public int TotalRegistros { get; init; }
    public double PromedioNeps { get; init; }
    public double TotalMts { get; init; }
    public int Criticos { get; init; }
    public int Advertencias { get; init; }
    public int PendientesRevision { get; init; }
    public IReadOnlyList<NepRecord> Ultimos { get; init; } = [];
}
