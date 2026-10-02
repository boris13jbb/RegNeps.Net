using Microsoft.Extensions.DependencyInjection;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync.Ux;

namespace RegNeps.Mobile;

public partial class OfflineOperationsPage : ContentPage
{
    private readonly IServiceScopeFactory _scopeFactory;
    private OfflineOperationListFilter _filter = OfflineOperationListFilter.All;
    private string? _userId;

    public OfflineOperationsPage(IServiceScopeFactory scopeFactory)
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
            var opsUx = scope.ServiceProvider.GetRequiredService<OfflineOperationsUxService>();

            var session = await sessions.GetValidSessionAsync();
            if (session is null)
            {
                _userId = null;
                SessionHintLabel.Text = "Sin sesión local. Inicia sesión online para ver tus operaciones.";
                OperationsList.ItemsSource = null;
                return;
            }

            _userId = session.UserId;
            SessionHintLabel.Text =
                $"Usuario: {session.Username} · Solo tus operaciones (máx. {OfflineOperationStatusLabels.DefaultListLimit}).";

            var items = await opsUx.ListAsync(session.UserId, _filter);
            OperationsList.ItemsSource = items.Select(i => new OpRow(i)).ToList();
            HighlightFilter();
            MessageLabel.Text = string.Empty;
        }
        catch (Exception ex)
        {
            MessageLabel.Text = OfflineSyncUxService.SanitizeError(ex.Message) ?? "No se pudo cargar el inventario.";
        }
    }

    private void HighlightFilter()
    {
        void Style(Button b, bool on)
        {
            b.BackgroundColor = on ? Color.FromArgb("#1E3A5F") : Color.FromArgb("#E5E7EB");
            b.TextColor = on ? Colors.White : Color.FromArgb("#111111");
        }

        Style(FilterAllButton, _filter == OfflineOperationListFilter.All);
        Style(FilterPendingButton, _filter == OfflineOperationListFilter.Pending);
        Style(FilterSyncedButton, _filter == OfflineOperationListFilter.Synced);
        Style(FilterErrorButton, _filter == OfflineOperationListFilter.SyncError);
        Style(FilterConflictButton, _filter == OfflineOperationListFilter.Conflict);
    }

    private async void OnFilterClicked(object? sender, EventArgs e)
    {
        if (sender is not Button { CommandParameter: string key })
        {
            return;
        }

        _filter = key switch
        {
            "Pending" => OfflineOperationListFilter.Pending,
            "Synced" => OfflineOperationListFilter.Synced,
            "SyncError" => OfflineOperationListFilter.SyncError,
            "Conflict" => OfflineOperationListFilter.Conflict,
            _ => OfflineOperationListFilter.All
        };
        await RefreshAsync();
    }

    private async void OnSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (e.CurrentSelection.FirstOrDefault() is not OpRow row || string.IsNullOrWhiteSpace(_userId))
        {
            return;
        }

        OperationsList.SelectedItem = null;
        var services = Handler?.MauiContext?.Services
                       ?? Application.Current?.Handler?.MauiContext?.Services;
        if (services is null)
        {
            return;
        }

        var page = services.GetRequiredService<OfflineOperationDetailPage>();
        page.Initialize(row.OperationId, _userId!);
        await Navigation.PushAsync(page);
    }

    private async void OnBackClicked(object? sender, EventArgs e)
    {
        if (Navigation.NavigationStack.Count > 1)
        {
            await Navigation.PopAsync();
        }
    }

    private sealed class OpRow
    {
        public OpRow(OfflineOperationListItem item)
        {
            OperationId = item.OperationId;
            OperationTypeLabel = item.OperationTypeLabel;
            StatusLabel = item.StatusLabel;
            RecordSummary = item.RecordSummary;
            SyncResultLabel = item.SyncResultLabel;
            CreatedLocalText = item.CreatedAtUtc.ToLocalTime().ToString("g");
            AttemptsText = item.AttemptCount > 0
                ? $"Intentos: {item.AttemptCount}"
                : (item.RequiresReview ? "Requiere revisión" : string.Empty);
        }

        public Guid OperationId { get; }
        public string OperationTypeLabel { get; }
        public string StatusLabel { get; }
        public string RecordSummary { get; }
        public string SyncResultLabel { get; }
        public string CreatedLocalText { get; }
        public string AttemptsText { get; }
    }
}
