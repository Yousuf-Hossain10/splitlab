using Microsoft.EntityFrameworkCore;
using Splitlab.Api;
using Splitlab.Infrastructure.Persistence;

// The real Splitlab API (same endpoints, auth, event pipeline, rollups and statistics) over a throwaway SQLite file,
// so the dashboard's end-to-end tests can run anywhere without a database server.
//
//   dotnet run --project tests/Splitlab.E2E.Host -- --db <file> --urls http://localhost:5080
const string SigningKey = "e2e-only-signing-key-e2e-only-signing-key-0123456789";

string databasePath = ValueOf("--db") ?? Path.Combine(Path.GetTempPath(), "splitlab-e2e.db");
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
foreach (string suffix in new[] { "", "-wal", "-shm" })
{
    File.Delete(databasePath + suffix);
}

WebApplication app = SplitlabApp.Create(
    // Development enables the /dev/token sign-in the dashboard uses.
    [.. args, "--environment=Development"],
    options => options.UseSqlite($"Data Source={databasePath};Default Timeout=30"),
    builder => builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Auth:Local:SigningKey"] = SigningKey,
        // Flush quickly so a test does not wait a second for events it has just sent.
        ["EventPipeline:FlushInterval"] = "00:00:00.200",
    }));

using (IServiceScope scope = app.Services.CreateScope())
{
    SplitlabDbContext db = scope.ServiceProvider.GetRequiredService<SplitlabDbContext>();
    db.Database.EnsureCreated();
    // Write-ahead logging lets requests and the background event writer use the file at the same time.
    db.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
}

app.Run();

string? ValueOf(string name)
{
    int index = Array.IndexOf(args, name);
    return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
}
