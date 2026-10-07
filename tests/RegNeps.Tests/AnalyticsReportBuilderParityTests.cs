using RegNeps.Application.Abstractions;
using RegNeps.Application.Analytics;
using RegNeps.Application.Reports;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Filters;
using Xunit;

namespace RegNeps.Tests;

/// <summary>
/// Paridad Fase 5: totales globales de AnalyticsService y ReportBuilderService sobre el mismo dataset.
/// </summary>
public sealed class AnalyticsReportBuilderParityTests
{
    [Fact]
    public async Task Same_Dataset_Yields_Same_Global_Totals()
    {
        var config = new AlertConfig { LimiteNormalMax = 18, LimiteAdvertenciaMax = 45 };
        var records = new List<NepRecord>
        {
            Make("003", 20),
            Make("003", 45),
            Make("104", 70),
            Make("104", -5),
        };

        var filters = new RecordFilters();
        ReportDateRange.FromLocalCalendarDates(DateTime.Today.AddDays(-1), DateTime.Today.AddDays(1))
            .ApplyTo(filters);

        var analytics = new AnalyticsService(new FixedRecords(records), new FixedAlertConfig(config));
        var builderSvc = new ReportBuilderService(new FixedRecords(records), new FixedAlertConfig(config));

        var summary = await analytics.BuildAsync(filters, viewerUserId: null, viewerSeesAll: true);
        var builder = await builderSvc.BuildFromFiltersAsync(
            filters, null, true, new ReportBuilderOptions { Primary = ReportBuilderDimension.Telar });

        Assert.Equal(summary.TotalRecords, builder.TotalRecords);
        Assert.Equal(summary.TotalNeps, builder.SumNeps);
        Assert.Equal(summary.AverageNeps, builder.AverageNeps);
        Assert.Equal(summary.TotalMts, builder.TotalMts);
        Assert.Equal(summary.MinNeps, builder.MinNeps);
        Assert.Equal(summary.MaxNeps, builder.MaxNeps);
        Assert.Equal(summary.NormalCount, builder.NormalCount);
        Assert.Equal(summary.WarningCount, builder.WarningCount);
        Assert.Equal(summary.CriticalCount, builder.CriticalCount);
        Assert.Equal(summary.QualityIndex, builder.QualityIndex);
    }

    private static NepRecord Make(string telar, double neps) => new()
    {
        Id = Guid.NewGuid(),
        Telar = telar,
        Tela = "Denim",
        LoteTrama = "L1",
        Neps = neps,
        CreatedAt = DateTime.UtcNow,
        Turno = "A",
        Operario = "Op",
        CreatedByUserId = "u1"
    };

    private sealed class FixedRecords : INepRecordRepository
    {
        private readonly IReadOnlyList<NepRecord> _records;
        public FixedRecords(IReadOnlyList<NepRecord> records) => _records = records;

        public Task<IReadOnlyList<NepRecord>> QueryAsync(
            RecordFilters filters, string? viewerUserId, bool viewerSeesAll, int take = 500,
            CancellationToken ct = default) =>
            Task.FromResult(_records);

        public Task<RegNeps.Application.Common.PagedResult<NepRecord>> QueryPagedAsync(
            RecordFilters filters,
            string? viewerUserId,
            bool viewerSeesAll,
            int pageNumber,
            int pageSize,
            CancellationToken ct = default) =>
            Task.FromResult(new RegNeps.Application.Common.PagedResult<NepRecord>
            {
                Items = _records.Take(pageSize).ToList(),
                PageNumber = Math.Max(1, pageNumber),
                PageSize = pageSize,
                TotalCount = _records.Count
            });

        public Task<int> CountFilteredAsync(
            RecordFilters filters,
            string? viewerUserId,
            bool viewerSeesAll,
            CancellationToken ct = default) =>
            Task.FromResult(_records.Count);

        public Task<IReadOnlyList<NepRecord>> GetRecentAsync(int take = 100, CancellationToken ct = default) =>
            Task.FromResult(_records);
        public Task<IReadOnlyList<NepRecord>> GetByIdsAsync(
            IReadOnlyCollection<Guid> ids, string? viewerUserId, bool viewerSeesAll, CancellationToken ct = default) =>
            Task.FromResult(_records.Where(r => ids.Contains(r.Id)).ToList() as IReadOnlyList<NepRecord>);
        public Task<NepRecord?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
            Task.FromResult(_records.FirstOrDefault(r => r.Id == id));
        public Task<NepRecord?> FindByClientOperationAsync(string userId, string clientOperationId, CancellationToken ct = default) =>
            Task.FromResult<NepRecord?>(null);
        public Task<IReadOnlyList<NepRecord>> FindRecentByUserAsync(
            string userId, string? externalUserId, DateTime sinceUtc, int take = 100, CancellationToken ct = default) =>
            Task.FromResult(_records);
        public Task<NepRecord> AddAsync(NepRecord record, CancellationToken ct = default) => Task.FromResult(record);
        public Task UpdateAsync(NepRecord record, CancellationToken ct = default) => Task.CompletedTask;
        public Task DeleteAsync(Guid id, CancellationToken ct = default) => Task.CompletedTask;
        public Task ClearAllAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> CountAsync(CancellationToken ct = default) => Task.FromResult(_records.Count);
    }

    private sealed class FixedAlertConfig : IAlertConfigRepository
    {
        private readonly AlertConfig _config;
        public FixedAlertConfig(AlertConfig config) => _config = config;
        public Task<AlertConfig> GetAsync(CancellationToken ct = default) => Task.FromResult(_config);
        public Task SaveAsync(AlertConfig config, CancellationToken ct = default) => Task.CompletedTask;
    }
}
