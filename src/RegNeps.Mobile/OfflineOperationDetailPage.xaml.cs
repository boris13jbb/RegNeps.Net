using Microsoft.Extensions.DependencyInjection;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync;
using RegNeps.OfflineStore.Sync.Ux;

namespace RegNeps.Mobile;

public partial class OfflineOperationDetailPage : ContentPage
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ManualSyncGate _syncGate;
    private Guid _operationId;
    private string _userId = string.Empty;
    private OfflineOperationDetail? _detail;

    public OfflineOperationDetailPage(IServiceScopeFactory scopeFactory, ManualSyncGate syncGate)
    {
        _scopeFactory = scopeFactory;
        _syncGate = syncGate;
        InitializeComponent();
    }

    public void Initialize(Guid operationId, string userId)
    {
        _operationId = operationId;
        _userId = userId;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (string.IsNullOrWhiteSpace(_userId) || _operationId == Guid.Empty)
        {
            MessageLabel.Text = "Operación no especificada.";
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var opsUx = scope.ServiceProvider.GetRequiredService<OfflineOperationsUxService>();
            _detail = await opsUx.GetDetailAsync(_userId, _operationId);
            if (_detail is null)
            {
                MessageLabel.Text = "Operación no encontrada o no pertenece a tu sesión.";
                return;
            }

            BindDetail(_detail);
        }
        catch (Exception ex)
        {
            MessageLabel.Text = OfflineSyncUxService.SanitizeError(ex.Message) ?? "No se pudo cargar el detalle.";
        }
    }

    private void BindDetail(OfflineOperationDetail d)
    {
        TitleLabel.Text = d.OperationTypeLabel;
        StatusLabel.Text = d.StatusLabel;
        SummaryLabel.Text = d.UserFacingSummary;
        LocalLabel.Text = "Local: " + d.LocalRecordSummary;
        ServerLabel.Text = d.RequiresReview ? "Servidor: " + d.ServerRecordSummary : string.Empty;
        ServerLabel.IsVisible = d.RequiresReview;
        LocalIdLabel.Text = "ID local: " + d.LocalIdDisplay;
        ServerIdLabel.Text = "ID servidor: " + d.ServerIdDisplay;
        CreatedLabel.Text = "Creada: " + d.CreatedAtUtc.ToLocalTime().ToString("g");
        AttemptsLabel.Text = d.LastAttemptAtUtc is { } at
            ? $"Intentos: {d.AttemptCount} · Último: {at.ToLocalTime():g}"
            : $"Intentos: {d.AttemptCount}";
        ErrorLabel.Text = d.ErrorFriendly ?? string.Empty;
        ErrorLabel.IsVisible = !string.IsNullOrWhiteSpace(d.ErrorFriendly);

        ConflictStampLabel.IsVisible = !string.IsNullOrWhiteSpace(d.ConflictStampDisplay);
        ConflictStampLabel.Text = string.IsNullOrWhiteSpace(d.ConflictStampDisplay)
            ? string.Empty
            : "Marca de concurrencia (servidor): " + d.ConflictStampDisplay;

        TechClientOpLabel.Text = "ClientOperationId: " + d.ClientOperationId;
        TechCodeLabel.Text = string.IsNullOrWhiteSpace(d.LastServerErrorCode)
            ? "Código servidor: —"
            : "Código servidor: " + d.LastServerErrorCode;
        TechCaptureLabel.Text = string.IsNullOrWhiteSpace(d.CaptureSessionId)
            ? "CaptureSessionId: —"
            : "CaptureSessionId: " + d.CaptureSessionId;

        ActionHintLabel.Text = d.ActionHint;
        RetryButton.IsVisible = d.PrimaryAction == OfflineOperationUxAction.RetrySync;
        SyncNowButton.IsVisible = d.PrimaryAction == OfflineOperationUxAction.SyncNow;
        ReloginButton.IsVisible = d.PrimaryAction == OfflineOperationUxAction.Relogin;
        ReviewButton.IsVisible = d.PrimaryAction == OfflineOperationUxAction.RequiresReview;
        MessageLabel.Text = string.Empty;

        _ = RefreshEditButtonAsync(d.LocalNepRecordId);
    }

    private async Task RefreshEditButtonAsync(Guid? localNepRecordId)
    {
        EditRecordButton.IsVisible = false;
        if (localNepRecordId is null || localNepRecordId == Guid.Empty)
        {
            return;
        }

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var capture = scope.ServiceProvider.GetRequiredService<OfflineCaptureService>();
            var elig = await capture.GetEditEligibilityAsync(localNepRecordId.Value);
            EditRecordButton.IsVisible = elig.CanEdit;
            EditRecordButton.CommandParameter = localNepRecordId.Value;
        }
        catch
        {
            EditRecordButton.IsVisible = false;
        }
    }

    private async void OnEditRecordClicked(object? sender, EventArgs e)
    {
        if (_detail?.LocalNepRecordId is not Guid localId || localId == Guid.Empty)
        {
            return;
        }

        var services = Handler?.MauiContext?.Services
                       ?? Application.Current?.Handler?.MauiContext?.Services;
        if (services is null)
        {
            return;
        }

        var page = services.GetRequiredService<OfflineEditRecordPage>();
        page.Initialize(localId);
        await Navigation.PushAsync(page);
    }

    private async void OnRetryClicked(object? sender, EventArgs e)
    {
        await RunSyncWithOptionalPrepareAsync(prepareRetry: true);
    }

    private async void OnSyncNowClicked(object? sender, EventArgs e)
    {
        await RunSyncWithOptionalPrepareAsync(prepareRetry: false);
    }

    private async Task RunSyncWithOptionalPrepareAsync(bool prepareRetry)
    {
        if (_syncGate.IsBusy)
        {
            MessageLabel.Text = "Ya hay una sincronización en curso.";
            return;
        }

        RetryButton.IsEnabled = false;
        SyncNowButton.IsEnabled = false;
        MessageLabel.Text = "Sincronizando…";

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var opsUx = scope.ServiceProvider.GetRequiredService<OfflineOperationsUxService>();
            var syncUx = scope.ServiceProvider.GetRequiredService<OfflineSyncUxService>();
            var engine = scope.ServiceProvider.GetRequiredService<ISyncEngine>();

            if (prepareRetry)
            {
                var (prepared, prepareMsg) = await opsUx.TryPrepareRetryAsync(_userId, _operationId);
                if (!prepared)
                {
                    MessageLabel.Text = prepareMsg;
                    return;
                }
            }

            var runner = new ManualSyncRunner(engine, _syncGate);
            var (invoked, result) = await runner.TrySyncAsync();
            if (!invoked || result is null)
            {
                MessageLabel.Text = "Ya hay una sincronización en curso.";
                return;
            }

            var counters = await syncUx.GetCountersAsync(_userId);
            var feedback = OfflineSyncUxService.MapRunResult(result, counters);
            MessageLabel.Text = $"{feedback.Title}: {feedback.Message}";
            MessageLabel.TextColor = feedback.RequiresLogin
                ? Color.FromArgb("#92400E")
                : Color.FromArgb("#14532D");

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageLabel.Text = OfflineSyncUxService.SanitizeError(ex.Message)
                                ?? SyncResultUserMessages.Transient;
        }
        finally
        {
            RetryButton.IsEnabled = true;
            SyncNowButton.IsEnabled = true;
        }
    }

    private async void OnReloginClicked(object? sender, EventArgs e)
    {
        // Volver a MainPage (raíz) y abrir WebView login.
        while (Navigation.NavigationStack.Count > 1)
        {
            await Navigation.PopAsync();
        }

        if (Application.Current?.Windows.FirstOrDefault()?.Page is NavigationPage nav
            && nav.RootPage is MainPage main)
        {
            await main.ReturnToOnlineLoginAsync();
        }
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        if (Navigation.NavigationStack.Count > 1)
        {
            await Navigation.PopAsync();
        }
    }
}
