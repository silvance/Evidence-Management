using Emc.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Emc.Application.Tests.SqlServer;

/// <summary>
/// The SQL Server release-validation lane. OPT-IN: runs only when
/// <c>EMC_SQLSERVER_TEST_CONNECTION</c> names a SQL Server the tests may create and drop
/// databases on; otherwise every test in the lane is SKIPPED with that reason, and the ordinary
/// suites (domain, SQLite) are unaffected.
///
/// The lane exists because SQLite cannot exercise SQL Server-specific controls: the append-only
/// triggers, filtered indexes as deployed, SQL Server constraint and concurrency behaviour, and
/// the migrations themselves. It runs completely offline against an approved local instance
/// (Developer/Express/enterprise); nothing here downloads anything. See
/// docs/air-gapped-build-and-maintenance.md, "Release validation".
///
/// The connection string is read from the environment and never committed. A test database
/// named EmcTest_&lt;guid&gt; is created from it and dropped afterwards. Fixture data is the same
/// fictitious data the SQLite harness seeds; no real evidence data is ever placed here.
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string ConnectionVariable = "EMC_SQLSERVER_TEST_CONNECTION";

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ConnectionVariable)))
        {
            Skip = $"SQL Server release-validation lane: set {ConnectionVariable} to run.";
        }
    }
}

/// <summary>A disposable database on the configured server, schema applied by the committed migrations.</summary>
public sealed class SqlServerTestDatabase : IDisposable
{
    private readonly string _masterConnectionString;

    public SqlServerTestDatabase()
    {
        var configured = Environment.GetEnvironmentVariable(SqlServerFactAttribute.ConnectionVariable)
            ?? throw new InvalidOperationException($"{SqlServerFactAttribute.ConnectionVariable} is not set.");

        DatabaseName = $"EmcTest_{Guid.NewGuid():N}";

        var builder = new SqlConnectionStringBuilder(configured) { InitialCatalog = "master" };
        _masterConnectionString = builder.ConnectionString;

        Execute(_masterConnectionString, $"CREATE DATABASE [{DatabaseName}];");

        builder.InitialCatalog = DatabaseName;
        ConnectionString = builder.ConnectionString;

        Options = new DbContextOptionsBuilder<EmcDbContext>()
            .UseSqlServer(ConnectionString)
            .Options;
    }

    public string DatabaseName { get; }
    public string ConnectionString { get; }
    public DbContextOptions<EmcDbContext> Options { get; }

    public void Dispose()
    {
        Execute(
            _masterConnectionString,
            $"IF DB_ID(N'{DatabaseName}') IS NOT NULL BEGIN ALTER DATABASE [{DatabaseName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{DatabaseName}]; END");
    }

    private static void Execute(string connectionString, string sql)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>The slice harness over a real SQL Server database built by the migrations.</summary>
public sealed class SqlServerHarness : SliceTestHarness
{
    private readonly SqlServerTestDatabase _database;

    private SqlServerHarness(SqlServerTestDatabase database)
        : base(database.Options, useMigrations: true)
    {
        _database = database;
    }

    public static SqlServerHarness Create() => new(new SqlServerTestDatabase());

    public string ConnectionString => _database.ConnectionString;

    /// <summary>Runs a statement outside EF, as an out-of-band actor would. Returns the SQL error number, or 0.</summary>
    public int TryExecuteOutOfBand(string sql, params SqlParameter[] parameters)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);

        try
        {
            command.ExecuteNonQuery();
            return 0;
        }
        catch (SqlException ex)
        {
            return ex.Number;
        }
    }

    public T Scalar<T>(string sql)
    {
        using var connection = new SqlConnection(ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return (T)command.ExecuteScalar()!;
    }

    public override void Dispose()
    {
        base.Dispose();
        _database.Dispose();
    }
}
