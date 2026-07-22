using RegNeps.Application.Abstractions;
using RegNeps.Domain.Constants;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
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

    public NepRecordService(INepRecordRepository records, IAlertConfigRepository alertConfig)
    {
        _records = records;
        _alertConfig = alertConfig;
    }

    public async Task<NepRecord> CreateAsync(CreateNepRecordRequest request, CancellationToken ct = default)
    {
        Validate(request);
        var record = new NepRecord
        {
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
            CreatedByUserId = request.CreatedByUserId,
            CreatedByEmail = request.CreatedByEmail,
            CreatedByRole = request.CreatedByRole
        };
        return await _records.AddAsync(record, ct);
    }

    public async Task<NepRecord> UpdateAsync(UpdateNepRecordRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Telar))
            throw new ArgumentException("El telar es obligatorio.", nameof(request.Telar));
        if (request.Neps < 0)
            throw new ArgumentException("Los neps no pueden ser negativos.", nameof(request.Neps));

        var record = await _records.GetByIdAsync(request.Id, ct)
            ?? throw new InvalidOperationException("Registro no encontrado.");

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

        await _records.UpdateAsync(record, ct);
        return record;
    }

    public Task<IReadOnlyList<NepRecord>> GetRecentAsync(int take = 100, CancellationToken ct = default) =>
        _records.GetRecentAsync(take, ct);

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

    public async Task ApplyCorrectiveAsync(CorrectiveActionRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(request.Accion))
        {
            throw new ArgumentException("La acción correctiva es obligatoria.");
        }

        var record = await _records.GetByIdAsync(request.RecordId, ct)
            ?? throw new InvalidOperationException("Registro no encontrado.");

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

    public Task DeleteAsync(Guid id, CancellationToken ct = default) => _records.DeleteAsync(id, ct);
    public Task ClearAllAsync(CancellationToken ct = default) => _records.ClearAllAsync(ct);

    public async Task<DashboardSummary> GetDashboardSummaryAsync(
        string? viewerUserId,
        bool viewerSeesAll,
        int take = 100,
        CancellationToken ct = default)
    {
        var config = await _alertConfig.GetAsync(ct);
        var records = await _records.QueryAsync(new RecordFilters(), viewerUserId, viewerSeesAll, take, ct);
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
        string? viewerUserId,
        bool viewerSeesAll,
        CancellationToken ct = default)
    {
        var config = await _alertConfig.GetAsync(ct);
        var all = await _records.QueryAsync(new RecordFilters(), viewerUserId, viewerSeesAll, 1000, ct);
        return all
            .Where(r => r.GetAlertLevel(config) != AlertLevel.Normal)
            .OrderByDescending(r => r.GetAlertLevel(config))
            .ThenByDescending(r => r.CreatedAt)
            .ToList();
    }

    private static void Validate(CreateNepRecordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Telar))
        {
            throw new ArgumentException("El telar es obligatorio.", nameof(request.Telar));
        }

        if (request.Neps < 0)
        {
            throw new ArgumentException("Los neps no pueden ser negativos.", nameof(request.Neps));
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
