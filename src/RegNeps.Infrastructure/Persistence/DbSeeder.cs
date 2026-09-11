using Microsoft.EntityFrameworkCore;
using RegNeps.Domain.Entities;
using RegNeps.Domain.Enums;

namespace RegNeps.Infrastructure.Persistence;

public static class DbSeeder
{
    public static async Task SeedAsync(RegNepsDbContext db)
    {
        if (!await db.AlertConfigs.AnyAsync())
        {
            db.AlertConfigs.Add(new AlertConfig
            {
                Id = 1,
                LimiteNormalMax = 30,
                LimiteAdvertenciaMax = 60,
                CantidadReincidenciasCriticas = 3,
                DiasParaReincidencia = 1,
                AlertasActivas = true
            });
        }

        if (!await db.Fabrics.AnyAsync())
        {
            db.Fabrics.AddRange(
                new Fabric { Name = "Tela estándar A", Code = "A" },
                new Fabric { Name = "Tela estándar B", Code = "B" });
        }

        if (!await db.LoteTramaItems.AnyAsync())
        {
            foreach (var code in LoteTramaDefaults.Codes)
            {
                db.LoteTramaItems.Add(new LoteTramaItem
                {
                    Code = code,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            }
        }

        // Solo crea admin seed si no existe ningún superadmin activo.
        // No reactiva usuarios desactivados/eliminados (evita anular soft-delete).
        var hasActiveSuper = await db.Users.AnyAsync(u =>
            u.DeletedAt == null &&
            u.IsActive &&
            (u.IsSuperAdmin || u.Role == AppUserRole.SuperAdmin));

        if (!hasActiveSuper)
        {
            var admin = await db.Users.FirstOrDefaultAsync(u => u.Username == "admin");
            if (admin is null)
            {
                db.Users.Add(new AppUser
                {
                    Username = "admin",
                    DisplayName = "Administrador",
                    PasswordHash = BCrypt.Net.BCrypt.HashPassword("Admin123!"),
                    Role = AppUserRole.SuperAdmin,
                    IsSuperAdmin = true,
                    IsActive = true,
                    CreatedAt = DateTime.UtcNow
                });
            }
            // Si existe "admin" pero fue desactivado a propósito y no queda otro superadmin,
            // no lo resucitamos automáticamente: el operador debe restaurarlo manualmente.
        }

        await db.SaveChangesAsync();
    }
}
