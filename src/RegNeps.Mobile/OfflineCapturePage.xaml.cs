using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;
using RegNeps.OfflineStore;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync;

namespace RegNeps.Mobile;

public partial class OfflineCapturePage : ContentPage
{
    private readonly IServiceScopeFactory _scopeFactory;
    private bool _syncing;

    public OfflineCapturePage(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        InitializeComponent();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sessions = scope.ServiceProvider.GetRequiredService<OfflineSessionService>();
            var capture = scope.ServiceProvider.GetRequiredService<OfflineCaptureService>();
            var db = scope.ServiceProvider.GetRequiredService<LocalSyncDbContext>();

            var session = await sessions.GetValidSessionAsync();
            if (session is null)
            {
                SessionLabel.Text =
                    "Sin sesión offline. Debe iniciar sesión online al menos una vez (snapshot UX).";
                OutboxLabel.Text = string.Empty;
            }
            else
            {
                SessionLabel.Text =
                    $"Usuario: {session.Username} · Expira: {session.ExpiresAtUtc.ToLocalTime():g}";

                var pending = await db.PendingOperations.CountAsync(o =>
                    o.UserId == session.UserId && o.Status == PendingOperationStatus.Pending);
                var synced = await db.PendingOperations.CountAsync(o =>
                    o.UserId == session.UserId && o.Status == PendingOperationStatus.Synced);
                var conflicts = await db.PendingOperations.CountAsync(o =>
                    o.UserId == session.UserId && o.Status == PendingOperationStatus.Conflict);
                var errors = await db.PendingOperations.CountAsync(o =>
                    o.UserId == session.UserId && o.Status == PendingOperationStatus.SyncError);
                OutboxLabel.Text =
                    $"Outbox — Pendientes: {pending} · Sincronizados: {synced} · Conflictos: {conflicts} · Errores: {errors}";
            }

            var recent = await capture.ListRecentAsync(30);
            RecentList.ItemsSource = recent.Select(r => new RecentRow(
                r.Telar,
                r.Neps.ToString("0.##"),
                FormatSyncLabel(r.SyncStatus, r.GetQualityLabel())))
                .ToList();
        }
        catch (Exception ex)
        {
            MessageLabel.Text = ex.Message;
        }
    }

    private static string FormatSyncLabel(LocalSyncStatus status, string quality) => status switch
    {
        LocalSyncStatus.PendingSync => quality + " · pendiente",
        LocalSyncStatus.Synced => quality + " · sync",
        LocalSyncStatus.Conflict => quality + " · conflicto",
        _ => quality
    };

    private async void OnSyncClicked(object? sender, EventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        MessageLabel.Text = "Sincronizando…";
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<ISyncEngine>();
            var result = await engine.SyncAsync();
            if (!result.Started)
            {
                MessageLabel.Text = result.Message ?? "Sync no iniciado.";
            }
            else
            {
                MessageLabel.Text =
                    $"Sync — Acc:{result.PushedAccepted} Dup:{result.PushedDuplicate} " +
                    $"Conf:{result.PushedConflict} Err:{result.PushedSyncError} Transient:{result.PushedTransient} | " +
                    $"Pull ↑{result.PulledUpserts} ✕{result.PulledDeletes} cursor={result.CursorAfter}" +
                    (result.AuthRequired ? " · Auth requerida" : "") +
                    (string.IsNullOrEmpty(result.Message) ? "" : " · " + result.Message);
            }

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageLabel.Text = ex.Message;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnNepsChanged(object? sender, TextChangedEventArgs e)
    {
        if (!double.TryParse(NepsEntry.Text?.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var neps))
        {
            QualityLabel.Text = "Calidad: —";
            return;
        }

        var level = AlertEvaluator.GetLevel(neps);
        QualityLabel.Text = $"Calidad: {level.ToDisplayLabel()}";
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        MessageLabel.Text = string.Empty;
        if (!double.TryParse(NepsEntry.Text?.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var neps))
        {
            MessageLabel.Text = "Neps inválido.";
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var capture = scope.ServiceProvider.GetRequiredService<OfflineCaptureService>();
            var result = await capture.CreateRecordAsync(new OfflineCreateRecordRequest
            {
                Telar = TelarEntry.Text ?? string.Empty,
                Neps = neps,
                Tela = TelaEntry.Text ?? string.Empty,
                LoteTrama = LoteEntry.Text ?? string.Empty,
                Turno = TurnoEntry.Text ?? string.Empty,
                Operario = OperarioEntry.Text ?? string.Empty,
                Observacion = ObservacionEditor.Text ?? string.Empty
            });

            MessageLabel.Text =
                $"Guardado offline: {result.QualityLabel} · Op {result.Operation.ClientOperationId[..8]}… · PendingSync";
            NepsEntry.Text = string.Empty;
            QualityLabel.Text = "Calidad: —";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageLabel.Text = ex.Message;
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        if (Navigation.NavigationStack.Count > 1)
        {
            await Navigation.PopAsync();
        }
    }

    private sealed record RecentRow(string Telar, string NepsText, string Quality);
}
