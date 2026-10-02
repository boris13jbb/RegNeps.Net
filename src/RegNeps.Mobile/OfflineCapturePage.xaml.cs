using Microsoft.Extensions.DependencyInjection;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;

namespace RegNeps.Mobile;

public partial class OfflineCapturePage : ContentPage
{
    private readonly IServiceScopeFactory _scopeFactory;

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
            var outbox = scope.ServiceProvider.GetRequiredService<RegNeps.OfflineStore.Abstractions.IOfflineOutboxQuery>();

            var session = await sessions.GetValidSessionAsync();
            if (session is null)
            {
                SessionLabel.Text =
                    "Sin sesión offline. Debe iniciar sesión online al menos una vez (snapshot UX).";
            }
            else
            {
                var pending = await outbox.CountPendingAsync();
                SessionLabel.Text =
                    $"Usuario: {session.Username} · Pendientes: {pending} · Expira: {session.ExpiresAtUtc.ToLocalTime():g}";
            }

            var recent = await capture.ListRecentAsync(30);
            RecentList.ItemsSource = recent.Select(r => new RecentRow(
                r.Telar,
                r.Neps.ToString("0.##"),
                r.GetQualityLabel() + (r.SyncStatus == LocalSyncStatus.PendingSync ? " · pendiente" : "")))
                .ToList();
        }
        catch (Exception ex)
        {
            MessageLabel.Text = ex.Message;
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
