using Microsoft.Extensions.DependencyInjection;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync;
using RegNeps.OfflineStore.Sync.Ux;

namespace RegNeps.Mobile;

public partial class OfflineConflictResolvePage : ContentPage
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ManualSyncGate _syncGate;
    private Guid _operationId;
    private ConflictResolutionView? _view;
    private bool _busy;

    public OfflineConflictResolvePage(IServiceScopeFactory scopeFactory, ManualSyncGate syncGate)
    {
        _scopeFactory = scopeFactory;
        _syncGate = syncGate;
        InitializeComponent();
    }

    public void Initialize(Guid operationId) => _operationId = operationId;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (_operationId == Guid.Empty)
        {
            MessageLabel.Text = "Operación no especificada.";
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<ConflictResolutionService>();
            _view = await resolver.GetViewAsync(_operationId);
            if (_view is null)
            {
                MessageLabel.Text = "Conflicto no encontrado o sin permiso para revisarlo.";
                HideActions();
                return;
            }

            BindView(_view);
        }
        catch (Exception ex)
        {
            MessageLabel.Text = OfflineSyncUxService.SanitizeError(ex.Message) ?? ex.Message;
            HideActions();
        }
    }

    private void BindView(ConflictResolutionView v)
    {
        KindLabel.Text = v.KindLabel;
        ReasonLabel.Text = v.ReasonLabel;
        EntityLabel.Text =
            $"EntityId: {v.EntityId?.ToString("D") ?? "—"} · Op: {v.OriginalClientOperationId[..Math.Min(12, v.OriginalClientOperationId.Length)]}… · {v.ConflictedAtUtc.ToLocalTime():g}";

        LocalSummaryLabel.Text = FormatSnapshot(v.Local, isLocal: true);
        ServerSummaryLabel.Text = v.ServerIsDeleted
            ? "Eliminado en el servidor (tombstone)."
            : v.Server is null
                ? "Snapshot servidor no disponible."
                : FormatSnapshot(v.Server, isLocal: false);

        DiffsLabel.Text = v.Differences.Count == 0
            ? "Sin diferencias de campos (o conflicto de eliminación)."
            : string.Join(Environment.NewLine,
                v.Differences.Select(d => $"• {d.FieldName}: local [{d.LocalValue}] vs servidor [{d.ServerValue}]"));

        KeepServerButton.Text = v.KeepServerButtonText;
        KeepServerButton.IsVisible = v.AllowedActions.HasFlag(ConflictResolutionActions.KeepServer);
        KeepLocalButton.Text = v.KeepLocalButtonText ?? "Mantener mis cambios";
        KeepLocalButton.IsVisible = v.AllowedActions.HasFlag(ConflictResolutionActions.KeepLocal);
        EditRetryButton.IsVisible = v.AllowedActions.HasFlag(ConflictResolutionActions.EditAndRetry);

        var showEdit = v.AllowedActions.HasFlag(ConflictResolutionActions.EditAndRetry);
        EditSectionTitle.IsVisible = showEdit;
        EditFields.IsVisible = showEdit;
        if (showEdit)
        {
            // Base = snapshot servidor (no local), según diseño Edit&Retry.
            var baseSnap = v.Server ?? v.Local;
            TelarEntry.Text = baseSnap.Telar;
            NepsEntry.Text = baseSnap.Neps.ToString(System.Globalization.CultureInfo.InvariantCulture);
            TelaEntry.Text = baseSnap.Tela;
            LoteEntry.Text = baseSnap.LoteTrama;
            TurnoEntry.Text = baseSnap.Turno;
            OperarioEntry.Text = baseSnap.Operario;
            LineaEntry.Text = baseSnap.LineaProduccion;
            ObservacionEditor.Text = baseSnap.Observacion;
            QualityLabel.Text = $"Calidad: {AlertEvaluator.GetLevel(baseSnap.Neps).ToDisplayLabel()}";
        }

        BlockedHintLabel.IsVisible = !string.IsNullOrWhiteSpace(v.BlockedKeepLocalReason)
                                     || !string.IsNullOrWhiteSpace(v.EditAndRetryHint);
        BlockedHintLabel.Text = string.Join(Environment.NewLine,
            new[] { v.BlockedKeepLocalReason, v.EditAndRetryHint }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        MessageLabel.Text = string.Empty;
    }

    private static string FormatSnapshot(ConflictFieldSnapshot s, bool isLocal)
    {
        if (s.IsDeleted && isLocal)
        {
            return $"Intención: eliminar · Telar {s.Telar} · Neps {s.Neps} · {s.QualityLabel}";
        }

        return $"Telar {s.Telar} · Neps {s.Neps} · {s.QualityLabel} · Tela {s.Tela} · Lote {s.LoteTrama} · Turno {s.Turno}";
    }

    private void HideActions()
    {
        KeepServerButton.IsVisible = false;
        KeepLocalButton.IsVisible = false;
        EditRetryButton.IsVisible = false;
        EditFields.IsVisible = false;
        EditSectionTitle.IsVisible = false;
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

        QualityLabel.Text = $"Calidad: {AlertEvaluator.GetLevel(neps).ToDisplayLabel()}";
    }

    private async void OnKeepServerClicked(object? sender, EventArgs e) =>
        await RunResolutionAsync(async resolver => await resolver.KeepServerAsync(_operationId));

    private async void OnKeepLocalClicked(object? sender, EventArgs e)
    {
        var confirm = await DisplayAlertAsync(
            "Mantener cambios locales",
            "Se encolará una NUEVA operación con un ClientOperationId nuevo, usando la marca de concurrencia actual del servidor. No es Last-Write-Wins.",
            "Continuar",
            "Cancelar");
        if (!confirm)
        {
            return;
        }

        await RunResolutionAsync(async resolver => await resolver.KeepLocalAsync(_operationId));
    }

    private async void OnEditRetryClicked(object? sender, EventArgs e)
    {
        if (!double.TryParse(NepsEntry.Text?.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var neps))
        {
            MessageLabel.Text = "Neps inválido.";
            return;
        }

        var fields = new ConflictEditFields
        {
            Telar = TelarEntry.Text ?? string.Empty,
            Neps = neps,
            Tela = TelaEntry.Text,
            LoteTrama = LoteEntry.Text,
            Turno = TurnoEntry.Text,
            Operario = OperarioEntry.Text,
            LineaProduccion = LineaEntry.Text,
            Observacion = ObservacionEditor.Text
        };

        await RunResolutionAsync(async resolver => await resolver.EditAndRetryAsync(_operationId, fields));
    }

    private async Task RunResolutionAsync(Func<ConflictResolutionService, Task<ConflictResolutionResult>> action)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        SetActionsEnabled(false);
        MessageLabel.Text = "Aplicando resolución…";
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var resolver = scope.ServiceProvider.GetRequiredService<ConflictResolutionService>();
            var result = await action(resolver);
            MessageLabel.Text = result.Message;
            MessageLabel.TextColor = result.IsSuccess
                ? Color.FromArgb("#14532D")
                : Color.FromArgb("#7C2D12");

            if (!result.IsSuccess)
            {
                SetActionsEnabled(true);
                await LoadAsync();
                return;
            }

            // Si se encoló mutación, intentar Push (sin auto-aceptar Conflict).
            if (result.NewOperationId is not null && !_syncGate.IsBusy)
            {
                var engine = scope.ServiceProvider.GetRequiredService<ISyncEngine>();
                var syncUx = scope.ServiceProvider.GetRequiredService<OfflineSyncUxService>();
                var sessions = scope.ServiceProvider.GetRequiredService<OfflineSessionService>();
                var session = await sessions.GetValidSessionAsync();
                var runner = new ManualSyncRunner(engine, _syncGate);
                var (invoked, syncResult) = await runner.TrySyncAsync();
                if (invoked && syncResult is not null && session is not null)
                {
                    var counters = await syncUx.GetCountersAsync(session.UserId);
                    var feedback = OfflineSyncUxService.MapRunResult(syncResult, counters);
                    MessageLabel.Text = $"{result.Message} {feedback.Title}: {feedback.Message}";
                }
            }

            await Task.Delay(350);
            if (Navigation.NavigationStack.Count > 1)
            {
                await Navigation.PopAsync();
            }
        }
        catch (Exception ex)
        {
            MessageLabel.Text = OfflineSyncUxService.SanitizeError(ex.Message) ?? ex.Message;
            MessageLabel.TextColor = Color.FromArgb("#7C2D12");
            SetActionsEnabled(true);
        }
        finally
        {
            _busy = false;
        }
    }

    private void SetActionsEnabled(bool enabled)
    {
        KeepServerButton.IsEnabled = enabled;
        KeepLocalButton.IsEnabled = enabled;
        EditRetryButton.IsEnabled = enabled;
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        if (Navigation.NavigationStack.Count > 1)
        {
            await Navigation.PopAsync();
        }
    }
}
