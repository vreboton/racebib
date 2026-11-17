using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace RaceTiming.Infrastructure.Persistence;

/// <summary>
/// Creates the MySQL-backed context for EF tooling without starting the API or opening a connection.
/// A connection string must be provided explicitly through ConnectionStrings__DefaultConnection.
/// </summary>
public sealed class RaceTimingDbContextFactory : IDesignTimeDbContextFactory<RaceTimingDbContext>
{
    public const string ConnectionStringEnvironmentVariable = "ConnectionStrings__DefaultConnection";

    public RaceTimingDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable(ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException(
                $"Set {ConnectionStringEnvironmentVariable} to a safe MySQL design-time connection string. EF tooling does not open a database connection for model checks.");

        var options = new DbContextOptionsBuilder<RaceTimingDbContext>()
            .UseMySql(connectionString, new MySqlServerVersion(new Version(8, 4, 0)))
            .Options;

        return new RaceTimingDbContext(options);
    }
}
