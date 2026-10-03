using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Maui.Networking;
using Microsoft.Maui.Storage;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;
using RegNeps.Mobile.Local;
using RegNeps.OfflineStore;
using RegNeps.OfflineStore.Enums;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync;
using RegNeps.OfflineStore.Sync.Ux;

namespace RegNeps.Mobile;

public partial class OfflineCapturePage : ContentPage
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ManualSyncGate _syncGate;
    private bool _requiresLogin;
    private bool _saving;
    private string? _serverBaseUrl;

    public OfflineCapturePage(IServiceScopeFactory scopeFactory, ManualSyncGate syncGate)
    {
        _scopeFactory = scopeFactory;
        _syncGate = syncGate;
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
            var ux = scope.ServiceProvider.GetRequiredService<OfflineSyncUxService>();
            var cookies = scope.ServiceProvider.GetRequiredService<ISyncAuthCookieProvider>();
            var db = scope.ServiceProvider.GetRequiredService<LocalSyncDbContext>();

            var raw = await sessions.GetRawSessionAsync();
            var session = await sessions.GetValidSessionAsync();
            var hasRaw = raw is not null;
            var expired = hasRaw && session is null;
            _serverBaseUrl = session?.ServerBaseUrl ?? raw?.ServerBaseUrl;

            var hasCookie = false;
            if (!string.IsNullOrWhiteSpace(_serverBaseUrl))
            {
                var cookie = await cookies.GetCookieHeaderAsync(_serverBaseUrl!);
                hasCookie = !string.IsNullOrWhiteSpace(cookie);
            }

            var access = Connectivity.Current.NetworkAccess;
            var noNetwork = access is NetworkAccess.None or NetworkAccess.Unknown;
            var serverUnreachable = false;
            if (!noNetwork && !string.IsNullOrWhiteSpace(_serverBaseUrl) && hasCookie && !_requiresLogin)
            {
                var probe = await ServerAvailabilityProbe.ProbeAsync(_serverBaseUrl);
                serverUnreachable = probe.Kind != ServerAvailabilityKind.Online;
            }

            var connectivity = OfflineSyncUxService.ResolveConnectivity(
                hasLocalSession: session is not null,
                localSessionExpired: expired,
                hasAuthCookie: hasCookie,
                noNetwork: noNetwork,
                serverUnreachable: serverUnreachable,
                lastRunRequiresLogin: _requiresLogin);

            ApplyConnectivityBanner(connectivity);

            // Aviso si LocalSession.ServerBaseUrl diverge del Entry (cookie por host).
            var entryUrl = Preferences.Default.Get("regneps_server_url", string.Empty);
            var mismatch = OfflineSyncUxService.ServerBaseUrlMismatchHint(_serverBaseUrl, entryUrl);
            if (!string.IsNullOrWhiteSpace(mismatch)
                && connectivity is SyncConnectivityUxKind.LocalSessionWithoutOnlineAuth
                    or SyncConnectivityUxKind.RequiresLogin
                    or SyncConnectivityUxKind.OnlineReady)
            {
                StatusBanner.Text = StatusBanner.Text + "\n" + mismatch;
            }

            ReloginButton.IsVisible = connectivity is SyncConnectivityUxKind.RequiresLogin
                or SyncConnectivityUxKind.LocalSessionWithoutOnlineAuth
                or SyncConnectivityUxKind.LocalSessionExpired
                or SyncConnectivityUxKind.NoLocalSession;

            SyncNowButton.IsEnabled = !_syncGate.IsBusy
                                      && session is not null
                                      && hasCookie
                                      && !_requiresLogin
                                      && !noNetwork;

            if (session is null)
            {
                SessionLabel.Text = expired
                    ? "Sesión local expirada. Inicia sesión online para continuar."
                    : "Sin sesión offline. Debe iniciar sesión online al menos una vez.";
                ClearCounters();
                ConflictsList.ItemsSource = null;
                ConflictsEmptyLabel.IsVisible = true;
                ErrorsList.ItemsSource = null;
                ErrorsEmptyLabel.IsVisible = true;
            }
            else
            {
                SessionLabel.Text =
                    $"Usuario: {session.Username} · Expira: {session.ExpiresAtUtc.ToLocalTime():g}";

                var summary = await ux.GetSummaryAsync(session.UserId);
                ApplyCounters(summary);

                var conflicts = await ux.GetConflictsAsync(session.UserId);
                ConflictsList.ItemsSource = conflicts;
                ConflictsEmptyLabel.IsVisible = conflicts.Count == 0;

                var errorOps = await db.PendingOperations.AsNoTracking()
                    .Where(o => o.UserId == session.UserId && o.Status == PendingOperationStatus.SyncError)
                    .OrderByDescending(o => o.LastAttemptAtUtc ?? o.CreatedAtUtc)
                    .Take(20)
                    .ToListAsync();

                var errorRows = errorOps.Select(o => new ErrorRow(
                    Title: o.OperationType switch
                    {
                        OfflineOperationType.CreateRecord => "Crear registro",
                        OfflineOperationType.UpdateRecord => "Actualizar",
                        OfflineOperationType.DeleteRecord => "Eliminar",
                        _ => o.OperationType.ToString()
                    },
                    Detail: OfflineSyncUxService.DescribeSyncErrorKind(o))).ToList();

                ErrorsList.ItemsSource = errorRows;
                ErrorsEmptyLabel.IsVisible = errorRows.Count == 0;
            }

            var mutableRows = new List<EditableRow>();
            if (session is not null)
            {
                var editableIds = new HashSet<Guid>();
                foreach (var r in await capture.ListEditableAsync(20))
                {
                    editableIds.Add(r.Id);
                    mutableRows.Add(new EditableRow(
                        r.Id,
                        $"Telar {r.Telar} · NEPS {r.Neps:0.##}",
                        $"{r.GetQualityLabel()} · Servidor {r.ServerRecordId:N}",
                        "Editar"));
                }

                foreach (var r in await capture.ListDeletableAsync(20))
                {
                    if (editableIds.Contains(r.Id))
                    {
                        var idx = mutableRows.FindIndex(x => x.LocalRecordId == r.Id);
                        if (idx >= 0)
                        {
                            mutableRows[idx] = mutableRows[idx] with { ActionHint = "Editar/Eliminar" };
                        }

                        continue;
                    }

                    mutableRows.Add(new EditableRow(
                        r.Id,
                        $"Telar {r.Telar} · NEPS {r.Neps:0.##}",
                        $"{r.GetQualityLabel()} · Servidor {r.ServerRecordId:N}",
                        "Eliminar"));
                }
            }

            EditableList.ItemsSource = mutableRows;
            EditableEmptyLabel.IsVisible = mutableRows.Count == 0;

            var recent = await capture.ListRecentAsync(30);
            RecentList.ItemsSource = recent.Select(r => new RecentRow(
                r.Telar,
                r.Neps.ToString("0.##"),
                FormatSyncLabel(r.SyncStatus, r.GetQualityLabel())))
                .ToList();

            await LoadCatalogPickersAsync(scope);
        }
        catch (Exception ex)
        {
            MessageLabel.Text = OfflineSyncUxService.SanitizeError(ex.Message)
                                ?? "No se pudo actualizar el estado.";
        }
    }

    private async void OnEditableSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not EditableRow row)
        {
            return;
        }

        EditableList.SelectedItem = null;
        var services = Handler?.MauiContext?.Services
                       ?? Application.Current?.Handler?.MauiContext?.Services;
        if (services is null)
        {
            return;
        }

        var page = services.GetRequiredService<OfflineEditRecordPage>();
        page.Initialize(row.LocalRecordId);
        await Navigation.PushAsync(page);
    }

    private void ApplyConnectivityBanner(SyncConnectivityUxKind kind)
    {
        StatusBanner.Text = OfflineSyncUxService.ConnectivityBanner(kind);
        var (bg, fg) = OfflineSyncUxService.BannerColorHex(kind);
        StatusBanner.BackgroundColor = Color.FromArgb(bg);
        StatusBanner.TextColor = Color.FromArgb(fg);
    }

    private void ApplyCounters(OfflineSyncSummary summary)
    {
        var c = summary.Counters;
        CounterPendingLabel.Text = $"Pendientes: {c.Pending}";
        CounterSyncedLabel.Text = $"Sincronizadas: {c.Synced}";
        CounterErrorLabel.Text = $"Con error: {c.SyncError}";
        CounterConflictLabel.Text = $"En conflicto: {c.Conflict}";

        LastSyncLabel.Text = summary.LastSyncAtUtc is { } at
            ? $"Última sincronización: {at.ToLocalTime():g}"
            : "Última sincronización: —";

        var stateParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(summary.LastConnectivityStatus))
        {
            stateParts.Add($"Estado: {summary.LastConnectivityStatus}");
        }

        if (!string.IsNullOrWhiteSpace(summary.LastSyncErrorSummary))
        {
            stateParts.Add(summary.LastSyncErrorSummary!);
        }

        SyncStateLabel.Text = string.Join(" · ", stateParts);
    }

    private void ClearCounters()
    {
        CounterPendingLabel.Text = "Pendientes: —";
        CounterSyncedLabel.Text = "Sincronizadas: —";
        CounterErrorLabel.Text = "Con error: —";
        CounterConflictLabel.Text = "En conflicto: —";
        LastSyncLabel.Text = "Última sincronización: —";
        SyncStateLabel.Text = string.Empty;
    }

    private static string FormatSyncLabel(LocalSyncStatus status, string quality) => status switch
    {
        LocalSyncStatus.PendingSync => quality + " · Pendiente",
        LocalSyncStatus.Synced => quality + " · Sincronizado",
        LocalSyncStatus.Conflict => quality + " · Conflicto",
        _ => quality
    };

    private async void OnSyncClicked(object? sender, EventArgs e)
    {
        if (_syncGate.IsBusy)
        {
            MessageLabel.Text = "Ya hay una sincronización en curso.";
            return;
        }

        MessageLabel.Text = "Sincronizando…";
        SyncNowButton.IsEnabled = false;
        SyncNowButton.Text = "Sincronizando…";

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var engine = scope.ServiceProvider.GetRequiredService<ISyncEngine>();
            var ux = scope.ServiceProvider.GetRequiredService<OfflineSyncUxService>();
            var sessions = scope.ServiceProvider.GetRequiredService<OfflineSessionService>();

            var runner = new ManualSyncRunner(engine, _syncGate);
            var (invoked, result) = await runner.TrySyncAsync();
            if (!invoked || result is null)
            {
                MessageLabel.Text = "Ya hay una sincronización en curso.";
                return;
            }

            _requiresLogin = result.AuthRequired;

            OfflineOutboxCounters? counters = null;
            var session = await sessions.GetValidSessionAsync();
            if (session is not null)
            {
                counters = await ux.GetCountersAsync(session.UserId);
            }

            var feedback = OfflineSyncUxService.MapRunResult(result, counters);
            MessageLabel.Text = $"{feedback.Title}: {feedback.Message}";
            MessageLabel.TextColor = feedback.RequiresLogin
                ? Color.FromArgb("#92400E")
                : feedback.Success && !feedback.Partial
                    ? Color.FromArgb("#14532D")
                    : Color.FromArgb("#7C2D12");

            await RefreshAsync();
        }
        catch (Exception ex)
        {
            // Error de red / transport: no elimina pendientes (el motor no los marca Synced).
            MessageLabel.Text = OfflineSyncUxService.SanitizeError(ex.Message)
                                ?? SyncResultUserMessages.Transient;
            await RefreshAsync();
        }
        finally
        {
            SyncNowButton.Text = "Sincronizar ahora";
            // RefreshAsync ajusta IsEnabled según cookie/sesión.
        }
    }

    private async void OnReloginClicked(object? sender, EventArgs e)
    {
        _requiresLogin = false;
        if (Navigation.NavigationStack.Count > 1)
        {
            await Navigation.PopAsync();
        }

        if (ShellOrMain() is MainPage main)
        {
            await main.ReturnToOnlineLoginAsync();
        }
    }

    private async void OnLogoutClicked(object? sender, EventArgs e)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var sessions = scope.ServiceProvider.GetRequiredService<OfflineSessionService>();
            var result = await sessions.ClearUxSnapshotAsync();
            _requiresLogin = false;
            MessageLabel.Text =
                $"{SyncResultUserMessages.LogoutKeepsWork} Pendientes conservados: {result.PendingOperationsRetained}.";
            MessageLabel.TextColor = Color.FromArgb("#7C2D12");
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageLabel.Text = OfflineSyncUxService.SanitizeError(ex.Message) ?? "No se pudo cerrar la sesión local.";
        }
    }

    private static Page? ShellOrMain()
    {
        var root = Application.Current?.Windows.FirstOrDefault()?.Page;
        if (root is NavigationPage nav)
        {
            return nav.RootPage;
        }

        return root;
    }

    private async Task LoadCatalogPickersAsync(IServiceScope scope)
    {
        var catalogs = scope.ServiceProvider.GetRequiredService<OfflineCatalogService>();
        var availability = await catalogs.GetAvailabilityAsync();
        CatalogStatusLabel.Text = availability.StatusMessage;
        CatalogStatusLabel.TextColor = availability.HasAnyCatalog
            ? Color.FromArgb("#14532D")
            : Color.FromArgb("#92400E");

        var fabrics = await catalogs.ListActiveAsync(LocalCatalogKind.Fabric);
        var lotes = await catalogs.ListActiveAsync(LocalCatalogKind.Lote);

        var telaItems = new List<string> { "— texto libre —" };
        telaItems.AddRange(fabrics.Select(f =>
            string.IsNullOrWhiteSpace(f.Name) ? f.Code : f.Name));
        TelaPicker.ItemsSource = telaItems;
        TelaPicker.SelectedIndex = 0;

        var loteItems = new List<string> { "— texto libre —" };
        loteItems.AddRange(lotes.Select(l => l.Code));
        LotePicker.ItemsSource = loteItems;
        LotePicker.SelectedIndex = 0;
    }

    private void OnTelaPickerChanged(object? sender, EventArgs e)
    {
        if (TelaPicker.SelectedIndex <= 0)
        {
            return;
        }

        TelaEntry.Text = TelaPicker.SelectedItem?.ToString() ?? string.Empty;
    }

    private void OnLotePickerChanged(object? sender, EventArgs e)
    {
        if (LotePicker.SelectedIndex <= 0)
        {
            return;
        }

        LoteEntry.Text = LotePicker.SelectedItem?.ToString() ?? string.Empty;
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
        if (_saving)
        {
            return;
        }

        MessageLabel.Text = string.Empty;
        if (!double.TryParse(NepsEntry.Text?.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var neps))
        {
            MessageLabel.Text = "Neps inválido.";
            return;
        }

        _saving = true;
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
                $"Guardado offline: {result.QualityLabel} · pendiente de sincronización.";
            MessageLabel.TextColor = Color.FromArgb("#14532D");
            NepsEntry.Text = string.Empty;
            QualityLabel.Text = "Calidad: —";
            await RefreshAsync();
        }
        catch (Exception ex)
        {
            MessageLabel.Text = OfflineSyncUxService.SanitizeError(ex.Message) ?? ex.Message;
            MessageLabel.TextColor = Color.FromArgb("#7C2D12");
        }
        finally
        {
            _saving = false;
        }
    }

    private async void OnOperationsClicked(object? sender, EventArgs e)
    {
        var services = Handler?.MauiContext?.Services
                       ?? Application.Current?.Handler?.MauiContext?.Services;
        if (services is null)
        {
            return;
        }

        if (Navigation.NavigationStack.LastOrDefault() is OfflineOperationsPage)
        {
            return;
        }

        var page = services.GetRequiredService<OfflineOperationsPage>();
        await Navigation.PushAsync(page);
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        if (Navigation.NavigationStack.Count > 1)
        {
            await Navigation.PopAsync();
        }
    }

    private sealed record RecentRow(string Telar, string NepsText, string Quality);

    private sealed record EditableRow(Guid LocalRecordId, string Title, string Subtitle, string ActionHint);

    private sealed record ErrorRow(string Title, string Detail);
}
