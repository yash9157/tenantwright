using Shared.EntityFrameworkCore.TenantDatabases;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

/// <summary>
/// Target normalization only ever makes more spellings of one database collide: credentials and options never matter,
/// and host case, a trailing dot, loopback spellings, an omitted default port and database case cannot disguise a
/// target. Different servers, ports or databases stay distinct, and nothing is resolved through DNS.
/// </summary>
public sealed class PostgreSqlDatabaseTargetTests
{
    [Theory]
    [InlineData("Host=localhost;Database=app", "Host=127.0.0.1;Port=5432;Database=app;Username=x;Password=y")]
    [InlineData("Host=localhost;Database=app", "Host=127.1.2.3;Database=app")]
    [InlineData("Host=localhost;Database=app", "Host=::1;Database=app")]
    [InlineData("Host=localhost;Database=app", "Host=[::1]:5432;Database=app")]
    [InlineData("Host=localhost;Database=app", "Host=::ffff:127.0.0.1;Database=app")]
    [InlineData("Host=localhost;Database=app", "Host=LOCALHOST.;Database=app")]
    [InlineData("Host=localhost;Database=app", "Host=db.localhost;Database=app")]
    [InlineData("Host=Db.Example.COM;Database=App", "Host=db.example.com.;Port=5432;Database=app;Pooling=false")]
    [InlineData("Host=db;Port=6432;Database=app", "Host=DB:6432;Database=app")]
    [InlineData("Host=10.0.0.5;Database=app", "Host=10.000.0.5;Database=app")]
    [InlineData("Host=a,b:5433;Database=app", "Host=B:5433, A;Database=app")]
    [InlineData("Username=app;Host=db", "Host=db;Database=APP")]
    public void Spellings_of_one_target_share_a_fingerprint(string first, string second)
    {
        Assert.Equal(PostgreSqlDatabaseTarget.FingerprintOf(first), PostgreSqlDatabaseTarget.FingerprintOf(second));
    }

    [Theory]
    [InlineData("Host=localhost;Database=app", "Host=localhost;Port=5433;Database=app")]
    [InlineData("Host=localhost;Database=app", "Host=localhost;Database=app2")]
    [InlineData("Host=localhost;Database=app", "Host=10.0.0.5;Database=app")]
    [InlineData("Host=db-a;Database=app", "Host=db-b;Database=app")]
    [InlineData("Host=a,b;Database=app", "Host=a;Database=app")]
    public void Different_targets_keep_different_fingerprints(string first, string second)
    {
        Assert.NotEqual(PostgreSqlDatabaseTarget.FingerprintOf(first), PostgreSqlDatabaseTarget.FingerprintOf(second));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Database=app")]
    [InlineData("Host=db")]
    [InlineData("Host=db:notaport;Database=app")]
    [InlineData("Host=[::1;Database=app")]
    [InlineData("not a connection string")]
    public void Values_that_name_no_complete_target_do_not_parse(string connectionString)
    {
        Assert.False(PostgreSqlDatabaseTarget.TryParse(connectionString, out _));
    }

    [Fact]
    public void Target_parts_from_configuration_normalize_like_a_connection_string()
    {
        Assert.True(PostgreSqlDatabaseTarget.TryCreate("127.0.0.1", port: null, "TenantWright_AuditLogs", out var parts));
        Assert.Equal(PostgreSqlDatabaseTarget.FingerprintOf("Host=localhost;Database=tenantwright_auditlogs"), parts.Fingerprint);
        Assert.Equal("localhost:5432", parts.Host);
        Assert.Equal("tenantwright_auditlogs", parts.Database);

        Assert.False(PostgreSqlDatabaseTarget.TryCreate("db", 0, "app", out _));
        Assert.False(PostgreSqlDatabaseTarget.TryCreate(" ", 5432, "app", out _));
        Assert.False(PostgreSqlDatabaseTarget.TryCreate("db", 5432, " ", out _));
    }
}
