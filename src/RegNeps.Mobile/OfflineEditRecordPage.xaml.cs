using Microsoft.Extensions.DependencyInjection;
using RegNeps.Domain.Enums;
using RegNeps.Domain.Services;
using RegNeps.OfflineStore.Models;
using RegNeps.OfflineStore.Services;
using RegNeps.OfflineStore.Sync.Ux;

namespace RegNeps.Mobile;

public partial class OfflineEditRecordPage : ContentPage
{
    private readonly IServiceScopeFactory _scopeFactory;
    private Guid _localRecordId;
    private bool _saving;

    public OfflineEditRecordPage(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
        InitializeComponent();
    }

    public void Initialize(Guid localRecordId) => _localRecordId = localRecordId;

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var capture = scope.ServiceProvider.GetRequiredService<OfflineCaptureService>();
            var elig = await capture.GetEditEligibilityAsync(_localRecordId);
            if (!elig.CanEdit)
            {
                BannerLabel.Text = elig.Message;
                BannerLabel.BackgroundColor = Color.FromArgb("#FEF3C7");
                BannerLabel.TextColor = Color.FromArgb("#92400E");
                SaveButton.IsEnabled = false;
                MessageLabel.Text = elig.Message;
                return;
            }

            var record = await capture.GetLocalRecordAsync(_localRecordId);
            if (record is null)
            {
                MessageLabel.Text = "Registro no encontrado.";
                SaveButton.IsEnabled = false;
                return;
            }

            TelarEntry.Text = record.Telar;
            NepsEntry.Text = record.Neps.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
            TelaEntry.Text = record.Tela;
            LoteEntry.Text = record.LoteTrama;
            TurnoEntry.Text = record.Turno;
            OperarioEntry.Text = record.Operario;
            LineaEntry.Text = record.LineaProduccion;
            ObservacionEditor.Text = record.Observacion;
            QualityLabel.Text = $"Calidad: {record.GetQualityLabel()}";
            IdsLabel.Text =
                $"ID local: {record.Id:N} · Servidor: {record.ServerRecordId:N}";
            SaveButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            MessageLabel.Text = OfflineSyncUxService.SanitizeError(ex.Message) ?? ex.Message;
            SaveButton.IsEnabled = false;
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

        QualityLabel.Text = $"Calidad: {AlertEvaluator.GetLevel(neps).ToDisplayLabel()}";
    }

    private async void OnSaveClicked(object? sender, EventArgs e)
    {
        if (_saving)
        {
            return;
        }

        if (!double.TryParse(NepsEntry.Text?.Replace(',', '.'),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var neps))
        {
            MessageLabel.Text = "Neps inválido.";
            return;
        }

        _saving = true;
        SaveButton.IsEnabled = false;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var capture = scope.ServiceProvider.GetRequiredService<OfflineCaptureService>();
            var result = await capture.UpdateRecordAsync(new OfflineUpdateRecordRequest
            {
                LocalRecordId = _localRecordId,
                Telar = TelarEntry.Text ?? string.Empty,
                Neps = neps,
                Tela = TelaEntry.Text ?? string.Empty,
                LoteTrama = LoteEntry.Text ?? string.Empty,
                Turno = TurnoEntry.Text ?? string.Empty,
                Operario = OperarioEntry.Text ?? string.Empty,
                LineaProduccion = LineaEntry.Text ?? string.Empty,
                Observacion = ObservacionEditor.Text ?? string.Empty
            });

            MessageLabel.Text =
                $"Cambios guardados offline ({result.QualityLabel}). Pendiente de sincronización.";
            MessageLabel.TextColor = Color.FromArgb("#14532D");

            await Task.Delay(400);
            if (Navigation.NavigationStack.Count > 1)
            {
                await Navigation.PopAsync();
            }
        }
        catch (Exception ex)
        {
            MessageLabel.Text = OfflineSyncUxService.SanitizeError(ex.Message) ?? ex.Message;
            MessageLabel.TextColor = Color.FromArgb("#7C2D12");
            SaveButton.IsEnabled = true;
        }
        finally
        {
            _saving = false;
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
