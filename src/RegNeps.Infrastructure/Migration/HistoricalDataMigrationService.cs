using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RegNeps.Application.Auth;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;
using RegNeps.Infrastructure.Persistence;

namespace RegNeps.Infrastructure.Migration;

/// <summary>
/// Importa el JSON exportado por scripts/export_firestore_history.js hacia SQL/SQLite.
/// Idempotente: reimportar actualiza por id de origen (Firestore).
/// </summary>
public sealed class HistoricalDataMigrationService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowReadingFromString
    };

    private readonly RegNepsDbContext _db;

    public HistoricalDataMigrationService(RegNepsDbContext db) => _db = db;

    public async Task<MigrationResult> ImportFromJsonAsync(
        Stream jsonStream,
        string? defaultTempPassword = null,
        CancellationToken ct = default)
    {
        using var doc = await JsonDocument.ParseAsync(jsonStream, cancellationToken: ct);
        var root = doc.RootElement;
        var payload = JsonSerializer.Deserialize<FirestoreExportPayload>(root.GetRawText(), JsonOptions)
                      ?? throw new InvalidOperationException("JSON de exportación inválido.");

        return await ImportPayloadAsync(payload, defaultTempPassword, ct);
    }

    public async Task<MigrationResult> ImportFromFileAsync(
        string path,
        string? defaultTempPassword = null,
        CancellationToken ct = default)
    {
        await using var fs = File.OpenRead(path);
        return await ImportFromJsonAsync(fs, defaultTempPassword, ct);
    }

    public async Task<MigrationResult> ImportPayloadAsync(
        FirestoreExportPayload payload,
        string? defaultTempPassword = null,
        CancellationToken ct = default)
    {
        var result = new MigrationResult
        {
            SourceExportedAt = payload.ExportedAt,
            WorkspaceId = payload.WorkspaceId
        };

        var tempPassword = string.IsNullOrWhiteSpace(defaultTempPassword)
            ? "Migracion123!"
            : defaultTempPassword;

        // 1) Config de alertas
        if (payload.AlertConfig is { ValueKind: JsonValueKind.Object } alertCfg)
        {
            var cfg = await _db.AlertConfigs.FindAsync([1], ct) ?? new AlertConfig { Id = 1 };
            if (alertCfg.TryGetProperty("limiteNormalMax", out var n) && n.TryGetInt32(out var ln))
            {
                cfg.LimiteNormalMax = ln;
            }

            if (alertCfg.TryGetProperty("limiteAdvertenciaMax", out var a) && a.TryGetInt32(out var la))
            {
                cfg.LimiteAdvertenciaMax = la;
            }

            if (alertCfg.TryGetProperty("cantidadReincidenciasCriticas", out var c) && c.TryGetInt32(out var cr))
            {
                cfg.CantidadReincidenciasCriticas = cr;
            }

            if (alertCfg.TryGetProperty("diasParaReincidencia", out var d) && d.TryGetInt32(out var dr))
            {
                cfg.DiasParaReincidencia = dr;
            }

            if (alertCfg.TryGetProperty("alertasActivas", out var aa))
            {
                cfg.AlertasActivas = aa.ValueKind switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.String => bool.TryParse(aa.GetString(), out var b) && b,
                    _ => cfg.AlertasActivas
                };
            }

            if (_db.Entry(cfg).State == EntityState.Detached)
            {
                _db.AlertConfigs.Add(cfg);
            }

            result.AlertConfigUpdated = true;
        }

        // 2) Telas
        var fabricNames = payload.Fabrics?
            .Select(f => f?.Trim())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

        var existingFabrics = await _db.Fabrics.ToListAsync(ct);
        foreach (var name in fabricNames)
        {
            if (existingFabrics.Any(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase)))
            {
                result.FabricsSkipped++;
                continue;
            }

            _db.Fabrics.Add(new Fabric { Name = name!, IsActive = true });
            result.FabricsInserted++;
        }

        // 3) Usuarios (sin password de Firebase → temporal)
        var existingUsers = await _db.Users.ToListAsync(ct);
        foreach (var u in payload.Users ?? [])
        {
            try
            {
                var firebaseUid = u.Uid ?? u.Id;
                if (string.IsNullOrWhiteSpace(firebaseUid) && string.IsNullOrWhiteSpace(u.Username))
                {
                    result.UsersSkipped++;
                    result.Warnings.Add("Usuario sin uid/username omitido.");
                    continue;
                }

                var username = (u.Username ?? DeriveUsername(u.RealEmail ?? u.InternalEmail ?? firebaseUid!))
                    .Trim()
                    .ToLowerInvariant();

                var existing = existingUsers.FirstOrDefault(x =>
                    string.Equals(x.Username, username, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(u.RealEmail) &&
                     string.Equals(x.Email, u.RealEmail, StringComparison.OrdinalIgnoreCase)));

                var role = ParseRole(u.Role, u.IsSuperAdmin == true);
                if (existing is null)
                {
                    var user = new AppUser
                    {
                        Username = username,
                        DisplayName = string.IsNullOrWhiteSpace(u.DisplayName) ? username : u.DisplayName!,
                        Email = u.RealEmail ?? u.InternalEmail,
                        Role = role,
                        IsSuperAdmin = u.IsSuperAdmin == true || role == AppUserRole.SuperAdmin,
                        IsActive = u.IsActive != false && u.DeletedAt is null,
                        PasswordHash = AuthService.HashPassword(tempPassword),
                        CreatedAt = ParseDate(u.CreatedAt) ?? DateTime.UtcNow,
                        DeletedAt = ParseDate(u.DeletedAt),
                        ExternalUserId = firebaseUid
                    };
                    _db.Users.Add(user);
                    existingUsers.Add(user);
                    result.UsersInserted++;
                }
                else
                {
                    existing.DisplayName = string.IsNullOrWhiteSpace(u.DisplayName)
                        ? existing.DisplayName
                        : u.DisplayName!;
                    existing.Email = u.RealEmail ?? u.InternalEmail ?? existing.Email;
                    existing.Role = role;
                    existing.IsSuperAdmin = u.IsSuperAdmin == true || role == AppUserRole.SuperAdmin;
                    existing.IsActive = u.IsActive != false && u.DeletedAt is null;
                    existing.DeletedAt = ParseDate(u.DeletedAt);
                    existing.ExternalUserId = firebaseUid ?? existing.ExternalUserId;
                    existing.UpdatedAt = DateTime.UtcNow;
                    result.UsersUpdated++;
                }
            }
            catch (Exception ex)
            {
                result.UsersSkipped++;
                result.Warnings.Add($"Usuario omitido: {ex.Message}");
            }
        }

        // 4) Registros
        var existingIds = await _db.NepRecords.AsNoTracking().Select(r => r.Id).ToListAsync(ct);
        var existingSet = existingIds.ToHashSet();

        foreach (var raw in payload.Records ?? [])
        {
            try
            {
                var sourceId = raw.Id ?? Guid.NewGuid().ToString("N");
                var id = ToDeterministicGuid(sourceId);
                var entity = MapRecord(raw, id, sourceId);

                if (existingSet.Contains(id))
                {
                    var tracked = await _db.NepRecords
                        .Include(r => r.HistorialAcciones)
                        .FirstAsync(r => r.Id == id, ct);
                    tracked.Telar = entity.Telar;
                    tracked.Neps = entity.Neps;
                    tracked.Tela = entity.Tela;
                    tracked.LoteTrama = entity.LoteTrama;
                    tracked.CreatedAt = entity.CreatedAt;
                    tracked.Turno = entity.Turno;
                    tracked.Operario = entity.Operario;
                    tracked.LineaProduccion = entity.LineaProduccion;
                    tracked.Observacion = entity.Observacion;
                    tracked.RevisadoPorSupervisor = entity.RevisadoPorSupervisor;
                    tracked.AccionCorrectiva = entity.AccionCorrectiva;
                    tracked.ResponsableRevision = entity.ResponsableRevision;
                    tracked.FechaRevision = entity.FechaRevision;
                    tracked.CreatedByUserId = entity.CreatedByUserId;
                    tracked.CreatedByEmail = entity.CreatedByEmail;
                    tracked.CreatedByRole = entity.CreatedByRole;
                    tracked.UpdatedAt = DateTime.UtcNow;

                    _db.CorrectiveActions.RemoveRange(tracked.HistorialAcciones);
                    tracked.HistorialAcciones = entity.HistorialAcciones;
                    foreach (var h in tracked.HistorialAcciones)
                    {
                        h.NepRecordId = tracked.Id;
                    }

                    result.RecordsUpdated++;
                }
                else
                {
                    _db.NepRecords.Add(entity);
                    existingSet.Add(id);
                    result.RecordsInserted++;
                }
            }
            catch (Exception ex)
            {
                result.RecordsSkipped++;
                result.Warnings.Add($"Registro omitido: {ex.Message}");
            }
        }

        // 5) Informes guardados (metadatos; no rehidrata snapshot completo de registros)
        var existingReports = await _db.SavedReports.ToListAsync(ct);
        foreach (var r in payload.Reports ?? [])
        {
            try
            {
                var sourceId = r.Id ?? Guid.NewGuid().ToString("N");
                var id = ToDeterministicGuid("report:" + sourceId);
                var existing = existingReports.FirstOrDefault(x => x.Id == id);
                var filtersJson = r.AppliedFilters.HasValue
                    ? r.AppliedFilters.Value.GetRawText()
                    : "{}";
                var count = r.Records?.Count ?? 0;
                if (existing is null)
                {
                    _db.SavedReports.Add(new SavedReport
                    {
                        Id = id,
                        Name = string.IsNullOrWhiteSpace(r.Name) ? $"Informe {sourceId}" : r.Name!,
                        CreatedAt = ParseDate(r.CreatedAt) ?? DateTime.UtcNow,
                        FiltersJson = filtersJson,
                        RecordCount = count,
                        SummaryText = $"Migrado desde Firestore ({count} registros en snapshot)."
                    });
                    result.ReportsInserted++;
                }
                else
                {
                    existing.Name = string.IsNullOrWhiteSpace(r.Name) ? existing.Name : r.Name!;
                    existing.FiltersJson = filtersJson;
                    existing.RecordCount = count;
                    existing.SummaryText = $"Migrado desde Firestore ({count} registros en snapshot).";
                    result.ReportsUpdated++;
                }
            }
            catch (Exception ex)
            {
                result.ReportsSkipped++;
                result.Warnings.Add($"Informe omitido: {ex.Message}");
            }
        }

        await _db.SaveChangesAsync(ct);

        // Enlaza CreatedByUserId (uid Firebase) → Id SQL para visibilidad de operarios.
        result.RecordsOwnershipRepaired = await RepairRecordOwnershipAsync(ct);

        result.TempPasswordNote =
            "Los usuarios migrados desde Firebase no traen contraseña. " +
            $"Se asignó contraseña temporal '{tempPassword}' (cámbiela desde Usuarios).";
        result.Success = true;
        return result;
    }

    /// <summary>
    /// Reescribe CreatedByUserId de registros que aún tienen UID Firebase,
    /// usando AppUser.ExternalUserId → AppUser.Id.
    /// </summary>
    public async Task<int> RepairRecordOwnershipAsync(CancellationToken ct = default)
    {
        var map = await _db.Users.AsNoTracking()
            .Where(u => u.ExternalUserId != null && u.ExternalUserId != "")
            .Select(u => new { u.ExternalUserId, u.Id })
            .ToListAsync(ct);

        if (map.Count == 0)
        {
            return 0;
        }

        var byExternal = map
            .GroupBy(x => x.ExternalUserId!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id.ToString(), StringComparer.Ordinal);

        var records = await _db.NepRecords
            .Where(r => r.CreatedByUserId != null && r.CreatedByUserId != "")
            .ToListAsync(ct);

        var changed = 0;
        foreach (var record in records)
        {
            var current = record.CreatedByUserId!;
            if (Guid.TryParse(current, out _))
            {
                continue; // Ya es GUID SQL.
            }

            if (byExternal.TryGetValue(current, out var sqlId) &&
                !string.Equals(current, sqlId, StringComparison.Ordinal))
            {
                record.CreatedByUserId = sqlId;
                record.UpdatedAt = DateTime.UtcNow;
                changed++;
            }
        }

        if (changed > 0)
        {
            await _db.SaveChangesAsync(ct);
        }

        return changed;
    }

    private static NepRecord MapRecord(FirestoreRecordDto raw, Guid id, string sourceId)
    {
        var historial = new List<CorrectiveActionEntry>();
        if (raw.HistorialAcciones is { Count: > 0 })
        {
            foreach (var h in raw.HistorialAcciones)
            {
                historial.Add(new CorrectiveActionEntry
                {
                    Id = Guid.NewGuid(),
                    NepRecordId = id,
                    Accion = h.Accion ?? "",
                    Responsable = h.Responsable ?? "",
                    Fecha = ParseDate(h.Fecha) ?? DateTime.UtcNow
                });
            }
        }

        return new NepRecord
        {
            Id = id,
            Telar = raw.Telar?.Trim() ?? "",
            Neps = raw.Neps ?? 0,
            Tela = raw.Tela?.Trim() ?? "",
            LoteTrama = raw.LoteTrama?.Trim() ?? "",
            CreatedAt = ParseDate(raw.CreatedAt) ?? DateTime.UtcNow,
            Turno = raw.Turno?.Trim() ?? "",
            Operario = raw.Operario?.Trim() ?? "",
            LineaProduccion = raw.LineaProduccion?.Trim() ?? "",
            Observacion = raw.Observacion?.Trim() ?? "",
            RevisadoPorSupervisor = raw.RevisadoPorSupervisor == true,
            AccionCorrectiva = raw.AccionCorrectiva?.Trim() ?? "",
            ResponsableRevision = raw.ResponsableRevision?.Trim() ?? "",
            FechaRevision = ParseDate(raw.FechaRevision),
            CreatedByUserId = raw.CreatedByUid ?? raw.CreatedByUserId,
            CreatedByEmail = raw.CreatedByEmail,
            CreatedByRole = raw.CreatedByRole,
            HistorialAcciones = historial
        };
    }

    public static Guid ToDeterministicGuid(string sourceId)
    {
        if (Guid.TryParse(sourceId, out var guid))
        {
            return guid;
        }

        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("regneps:" + sourceId));
        return new Guid(bytes);
    }

    private static AppUserRole ParseRole(string? role, bool isSuperAdmin)
    {
        if (isSuperAdmin)
        {
            return AppUserRole.SuperAdmin;
        }

        return (role ?? "").Trim().ToLowerInvariant() switch
        {
            "super_admin" or "superadmin" => AppUserRole.SuperAdmin,
            "admin" => AppUserRole.Admin,
            "supervisor" => AppUserRole.Supervisor,
            "gerencia" => AppUserRole.Gerencia,
            "operario" => AppUserRole.Operario,
            _ => AppUserRole.Operario
        };
    }

    private static string DeriveUsername(string seed)
    {
        var local = seed.Contains('@') ? seed.Split('@')[0] : seed;
        var sanitized = new string(local.ToLowerInvariant().Where(ch =>
            char.IsLetterOrDigit(ch) || ch is '.' or '_' or '-').ToArray());
        return string.IsNullOrWhiteSpace(sanitized) ? $"user{DateTime.UtcNow.Ticks}" : sanitized;
    }

    private static DateTime? ParseDate(object? value)
    {
        if (value is null)
        {
            return null;
        }

        if (value is DateTime dt)
        {
            return DateTime.SpecifyKind(dt, DateTimeKind.Utc);
        }

        if (value is JsonElement je)
        {
            if (je.ValueKind == JsonValueKind.String)
            {
                return ParseDate(je.GetString());
            }

            if (je.ValueKind == JsonValueKind.Object)
            {
                // Timestamp map { _seconds, _nanoseconds }
                if (je.TryGetProperty("_seconds", out var s) || je.TryGetProperty("seconds", out s))
                {
                    var seconds = s.TryGetInt64(out var sec) ? sec : 0;
                    return DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime;
                }
            }
        }

        var text = value.ToString();
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (DateTime.TryParse(text, null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
        {
            return parsed.Kind == DateTimeKind.Unspecified
                ? DateTime.SpecifyKind(parsed, DateTimeKind.Utc)
                : parsed.ToUniversalTime();
        }

        return null;
    }
}

public sealed class FirestoreExportPayload
{
    public string? ExportedAt { get; set; }
    public string? ProjectId { get; set; }
    public string? WorkspaceId { get; set; }
    public List<FirestoreRecordDto>? Records { get; set; }
    public List<FirestoreUserDto>? Users { get; set; }
    public List<FirestoreReportDto>? Reports { get; set; }
    public List<string>? Fabrics { get; set; }
    public JsonElement? AlertConfig { get; set; }
}

public sealed class FirestoreRecordDto
{
    public string? Id { get; set; }
    public string? Telar { get; set; }
    public double? Neps { get; set; }
    public string? Tela { get; set; }
    public string? LoteTrama { get; set; }
    public object? CreatedAt { get; set; }
    public string? Turno { get; set; }
    public string? Operario { get; set; }
    public string? LineaProduccion { get; set; }
    public string? Observacion { get; set; }
    public bool? RevisadoPorSupervisor { get; set; }
    public string? AccionCorrectiva { get; set; }
    public string? ResponsableRevision { get; set; }
    public object? FechaRevision { get; set; }
    public string? CreatedByUid { get; set; }
    public string? CreatedByUserId { get; set; }
    public string? CreatedByEmail { get; set; }
    public string? CreatedByRole { get; set; }
    public List<FirestoreCorrectiveDto>? HistorialAcciones { get; set; }
}

public sealed class FirestoreCorrectiveDto
{
    public object? Fecha { get; set; }
    public string? Responsable { get; set; }
    public string? Accion { get; set; }
}

public sealed class FirestoreUserDto
{
    public string? Id { get; set; }
    public string? Uid { get; set; }
    public string? Username { get; set; }
    public string? DisplayName { get; set; }
    public string? RealEmail { get; set; }
    public string? InternalEmail { get; set; }
    public string? Role { get; set; }
    public bool? IsActive { get; set; }
    public bool? IsSuperAdmin { get; set; }
    public object? CreatedAt { get; set; }
    public object? DeletedAt { get; set; }
}

public sealed class FirestoreReportDto
{
    public string? Id { get; set; }
    public string? Name { get; set; }
    public object? CreatedAt { get; set; }
    public JsonElement? AppliedFilters { get; set; }
    public List<FirestoreRecordDto>? Records { get; set; }
}

public sealed class MigrationResult
{
    public bool Success { get; set; }
    public string? SourceExportedAt { get; set; }
    public string? WorkspaceId { get; set; }
    public int RecordsInserted { get; set; }
    public int RecordsUpdated { get; set; }
    public int RecordsSkipped { get; set; }
    public int UsersInserted { get; set; }
    public int UsersUpdated { get; set; }
    public int UsersSkipped { get; set; }
    public int FabricsInserted { get; set; }
    public int FabricsSkipped { get; set; }
    public int ReportsInserted { get; set; }
    public int ReportsUpdated { get; set; }
    public int ReportsSkipped { get; set; }
    public bool AlertConfigUpdated { get; set; }
    public int RecordsOwnershipRepaired { get; set; }
    public string? TempPasswordNote { get; set; }
    public List<string> Warnings { get; set; } = [];
}
