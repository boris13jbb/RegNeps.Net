using Microsoft.EntityFrameworkCore;
using RegNeps.Infrastructure.Migration;
using RegNeps.Infrastructure.Persistence;

// Uso:
//   dotnet run --project tools/RegNeps.Migrate -- --file ..\..\FTS\firestore_export_sample.json --db migrate_test.db
//   dotnet run --project tools/RegNeps.Migrate -- --file FTS/firestore_export.json --db src/RegNeps.Web/regneps_v2.db

static string Arg(string[] args, string name, string fallback)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return fallback;
}

var file = Arg(args, "--file", "");
var dbPath = Arg(args, "--db", "regneps_migrated.db");
var tempPassword = Arg(args, "--password", "Migracion123!");

if (string.IsNullOrWhiteSpace(file) || !File.Exists(file))
{
    Console.Error.WriteLine(
        "Uso: RegNeps.Migrate --file <firestore_export.json> [--db regneps.db] [--password Migracion123!]");
    return 1;
}

var options = new DbContextOptionsBuilder<RegNepsDbContext>()
    .UseSqlite($"Data Source={dbPath}")
    .Options;

await using var db = new RegNepsDbContext(options);
await db.Database.EnsureCreatedAsync();

var migration = new HistoricalDataMigrationService(db);
Console.WriteLine($"Importando {file} → {dbPath} ...");
var result = await migration.ImportFromFileAsync(file, tempPassword);

Console.WriteLine(result.Success ? "OK" : "FALLÓ");
Console.WriteLine($"Registros +{result.RecordsInserted} / act {result.RecordsUpdated} / omit {result.RecordsSkipped}");
Console.WriteLine($"Usuarios  +{result.UsersInserted} / act {result.UsersUpdated} / omit {result.UsersSkipped}");
Console.WriteLine($"Telas     +{result.FabricsInserted} / omit {result.FabricsSkipped}");
Console.WriteLine($"Informes  +{result.ReportsInserted} / act {result.ReportsUpdated} / omit {result.ReportsSkipped}");
Console.WriteLine($"Config alertas: {(result.AlertConfigUpdated ? "sí" : "no")}");
Console.WriteLine($"Ownership reparado: {result.RecordsOwnershipRepaired}");
if (!string.IsNullOrEmpty(result.TempPasswordNote))
{
    Console.WriteLine(result.TempPasswordNote);
}

foreach (var w in result.Warnings.Take(20))
{
    Console.WriteLine($"AVISO: {w}");
}

return result.Success ? 0 : 2;
