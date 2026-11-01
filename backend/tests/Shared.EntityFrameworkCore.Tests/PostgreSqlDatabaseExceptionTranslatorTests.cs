using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Shared;
using Shared.AuditLogs.Auditing;
using Shared.EntityFrameworkCore;
using Shared.EntityFrameworkCore.ExceptionTranslation;
using Xunit;

namespace Shared.EntityFrameworkCore.Tests;

public sealed class PostgreSqlDatabaseExceptionTranslatorTests
{
    [Fact]
    public void TryTranslate_Uses_Registered_Domain_Descriptor_For_A_Known_Constraint()
    {
        var options = new SharedDatabaseExceptionTranslationOptions();
        options.Configure<TestDbContext>(
            "UX_Test_OneActivePerTenant",
            new SharedDatabaseConstraintDescriptor(
                "active_record_exists",
                "Close the active record before creating another one."));
        var exception = CreateDbUpdateException(
            PostgresErrorCodes.UniqueViolation,
            "UX_Test_OneActivePerTenant");

        var translated = PostgreSqlDatabaseExceptionTranslator.TryTranslate(
            exception,
            typeof(TestDbContext),
            options,
            out var translatedException);

        var conflict = Assert.IsType<SharedConflictException>(translatedException);
        Assert.True(translated);
        Assert.Equal("active_record_exists", conflict.Code);
        Assert.Equal("Close the active record before creating another one.", conflict.Message);
        Assert.Same(exception, conflict.InnerException);
    }

    [Theory]
    [InlineData(PostgresErrorCodes.UniqueViolation, SharedErrorCodes.UniqueConstraintViolation)]
    [InlineData(PostgresErrorCodes.ForeignKeyViolation, SharedErrorCodes.ForeignKeyConflict)]
    [InlineData(PostgresErrorCodes.SerializationFailure, SharedErrorCodes.TransactionConflict)]
    [InlineData(PostgresErrorCodes.DeadlockDetected, SharedErrorCodes.TransactionConflict)]
    public void TryTranslate_Maps_Expected_PostgreSql_Conflicts_To_SharedConflicts(
        string sqlState,
        string expectedCode)
    {
        var translated = PostgreSqlDatabaseExceptionTranslator.TryTranslate(
            CreateDbUpdateException(sqlState),
            typeof(TestDbContext),
            new SharedDatabaseExceptionTranslationOptions(),
            out var translatedException);

        var conflict = Assert.IsType<SharedConflictException>(translatedException);
        Assert.True(translated);
        Assert.Equal(expectedCode, conflict.Code);
        Assert.DoesNotContain("internal database message", conflict.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryTranslate_Maps_Check_Violations_To_SharedValidation()
    {
        var translated = PostgreSqlDatabaseExceptionTranslator.TryTranslate(
            CreateDbUpdateException(PostgresErrorCodes.CheckViolation),
            typeof(TestDbContext),
            new SharedDatabaseExceptionTranslationOptions(),
            out var translatedException);

        var validation = Assert.IsType<SharedValidationException>(translatedException);
        Assert.True(translated);
        Assert.Equal(SharedErrorCodes.CheckConstraintViolation, validation.Code);
    }

    [Fact]
    public void TryTranslate_Does_Not_Guess_Why_PostgreSql_Cancelled_A_Query()
    {
        var translated = PostgreSqlDatabaseExceptionTranslator.TryTranslate(
            CreateDbUpdateException(PostgresErrorCodes.QueryCanceled),
            typeof(TestDbContext),
            new SharedDatabaseExceptionTranslationOptions(),
            out var translatedException);

        Assert.False(translated);
        Assert.Null(translatedException);
    }

    [Fact]
    public void TryTranslate_Leaves_Unknown_PostgreSql_Errors_Unchanged()
    {
        var translated = PostgreSqlDatabaseExceptionTranslator.TryTranslate(
            CreateDbUpdateException("XX000"),
            typeof(TestDbContext),
            new SharedDatabaseExceptionTranslationOptions(),
            out var translatedException);

        Assert.False(translated);
        Assert.Null(translatedException);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedDbContext_Save_Paths_Translate_Provider_Errors(bool useRawSavePath)
    {
        var interceptor = new ThrowingSaveChangesInterceptor(
            CreateDbUpdateException(PostgresErrorCodes.UniqueViolation, "UX_TranslationRecords_Name"));
        await using var harness = await TranslationHarness.CreateAsync(interceptor);

        var exception = useRawSavePath
            ? await Assert.ThrowsAsync<SharedConflictException>(
                async () => await harness.DbContext.SaveChangesOnDbContextAsync(acceptAllChangesOnSuccess: true))
            : await Assert.ThrowsAsync<SharedConflictException>(
                async () => await harness.DbContext.SaveChangesAsync());

        Assert.Equal("translation_record_name_conflict", exception.Code);
        Assert.Equal("A translation record with this name already exists.", exception.Message);
    }

    [Fact]
    public void TryTranslate_Maps_Unregistered_Unique_Constraints_To_A_Safe_Generic_Conflict()
    {
        var translated = PostgreSqlDatabaseExceptionTranslator.TryTranslate(
            CreateDbUpdateException(PostgresErrorCodes.UniqueViolation, "UX_Unregistered_Constraint"),
            typeof(TestDbContext),
            new SharedDatabaseExceptionTranslationOptions(),
            out var translatedException);

        var conflict = Assert.IsType<SharedConflictException>(translatedException);
        Assert.True(translated);
        Assert.Equal(SharedErrorCodes.UniqueConstraintViolation, conflict.Code);
        Assert.DoesNotContain("UX_Unregistered_Constraint", conflict.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("internal database message", conflict.Message, StringComparison.Ordinal);
    }

    // --- The EF Core transient wrapper ---
    //
    // Npgsql treats 40001 and 40P01 as transient. With the default non-retrying execution strategy, EF Core wraps
    // such a failure in an InvalidOperationException, so the DbUpdateException never surfaces and the direct
    // translation path above is never reached. Without the wrapper path, a lost concurrency race reads as a
    // server fault instead of a conflict.

    [Theory]
    [InlineData(PostgresErrorCodes.SerializationFailure)]
    [InlineData(PostgresErrorCodes.DeadlockDetected)]
    public void TryTranslateTransientWrapper_Maps_Wrapped_Transient_Failures_To_A_Transaction_Conflict(
        string sqlState)
    {
        var wrapper = CreateTransientWrapper(sqlState);

        var translated = PostgreSqlDatabaseExceptionTranslator.TryTranslateTransientWrapper(
            wrapper,
            out var translatedException);

        var conflict = Assert.IsType<SharedConflictException>(translatedException);
        Assert.True(translated);
        Assert.Equal(SharedErrorCodes.TransactionConflict, conflict.Code);
        Assert.Same(wrapper, conflict.InnerException);
        Assert.DoesNotContain("internal database message", conflict.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The wrapper path must not become a catch-all for <see cref="InvalidOperationException"/>. Nothing about an
    /// arbitrary invalid-operation fault says it was a database conflict.
    /// </summary>
    [Fact]
    public void TryTranslateTransientWrapper_Leaves_An_Unrelated_InvalidOperationException_Unchanged()
    {
        var translated = PostgreSqlDatabaseExceptionTranslator.TryTranslateTransientWrapper(
            new InvalidOperationException("The source sequence is empty."),
            out var translatedException);

        Assert.False(translated);
        Assert.Null(translatedException);
    }

    [Fact]
    public void TryTranslateTransientWrapper_Leaves_A_Wrapper_Carrying_An_Unknown_State_Unchanged()
    {
        var translated = PostgreSqlDatabaseExceptionTranslator.TryTranslateTransientWrapper(
            CreateTransientWrapper("XX000"),
            out var translatedException);

        Assert.False(translated);
        Assert.Null(translatedException);
    }

    /// <summary>
    /// A unique violation never arrives wrapped, because Npgsql does not classify it as transient. If one somehow
    /// did, the wrapper path stays out of it rather than guessing — the direct path owns that translation.
    /// </summary>
    [Fact]
    public void TryTranslateTransientWrapper_Does_Not_Claim_Non_Transient_States()
    {
        var translated = PostgreSqlDatabaseExceptionTranslator.TryTranslateTransientWrapper(
            CreateTransientWrapper(PostgresErrorCodes.UniqueViolation),
            out var translatedException);

        Assert.False(translated);
        Assert.Null(translatedException);
    }

    [Theory]
    [InlineData(false, PostgresErrorCodes.DeadlockDetected)]
    [InlineData(true, PostgresErrorCodes.DeadlockDetected)]
    [InlineData(false, PostgresErrorCodes.SerializationFailure)]
    [InlineData(true, PostgresErrorCodes.SerializationFailure)]
    public async Task SharedDbContext_Save_Paths_Report_A_Wrapped_Transient_Failure_As_A_Conflict(
        bool useRawSavePath,
        string sqlState)
    {
        var interceptor = new ThrowingSaveChangesInterceptor(CreateTransientWrapper(sqlState));
        await using var harness = await TranslationHarness.CreateAsync(interceptor);

        var exception = useRawSavePath
            ? await Assert.ThrowsAsync<SharedConflictException>(
                async () => await harness.DbContext.SaveChangesOnDbContextAsync(acceptAllChangesOnSuccess: true))
            : await Assert.ThrowsAsync<SharedConflictException>(
                async () => await harness.DbContext.SaveChangesAsync());

        Assert.Equal(SharedErrorCodes.TransactionConflict, exception.Code);

        // The write is reported as a conflict, not reattempted. Adding retry semantics to non-idempotent writes
        // would be a different decision entirely.
        Assert.Equal(1, interceptor.AttemptCount);
    }

    /// <summary>
    /// When translation does not apply, the caller must receive the original exception — same instance, same inner
    /// chain — rather than a swallowed or re-created one.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedDbContext_Save_Paths_Rethrow_An_Untranslatable_InvalidOperationException(
        bool useRawSavePath)
    {
        var inner = new TimeoutException("The operation timed out.");
        var original = new InvalidOperationException("Something else went wrong.", inner);
        var interceptor = new ThrowingSaveChangesInterceptor(original);
        await using var harness = await TranslationHarness.CreateAsync(interceptor);

        var thrown = useRawSavePath
            ? await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await harness.DbContext.SaveChangesOnDbContextAsync(acceptAllChangesOnSuccess: true))
            : await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await harness.DbContext.SaveChangesAsync());

        Assert.Same(original, thrown);
        Assert.Same(inner, thrown.InnerException);
    }

    /// <summary>
    /// A wrapped state the translator does not recognize is not a conflict, and must not be reshaped into one.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedDbContext_Save_Paths_Rethrow_A_Wrapper_Carrying_An_Unknown_State(bool useRawSavePath)
    {
        var original = CreateTransientWrapper("XX000");
        var interceptor = new ThrowingSaveChangesInterceptor(original);
        await using var harness = await TranslationHarness.CreateAsync(interceptor);

        var thrown = useRawSavePath
            ? await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await harness.DbContext.SaveChangesOnDbContextAsync(acceptAllChangesOnSuccess: true))
            : await Assert.ThrowsAsync<InvalidOperationException>(
                async () => await harness.DbContext.SaveChangesAsync());

        Assert.Same(original, thrown);
        Assert.IsType<DbUpdateException>(thrown.InnerException);
    }

    /// <summary>
    /// Reproduces the shape EF Core produces: <see cref="InvalidOperationException"/> wrapping the
    /// <see cref="DbUpdateException"/> wrapping the <see cref="PostgresException"/>.
    /// </summary>
    private static InvalidOperationException CreateTransientWrapper(string sqlState) =>
        new(
            "An exception has been raised that is likely due to a transient failure.",
            CreateDbUpdateException(sqlState));

    private static DbUpdateException CreateDbUpdateException(string sqlState, string? constraintName = null)
    {
        var postgresException = new PostgresException(
            "internal database message",
            "ERROR",
            "ERROR",
            sqlState,
            detail: null,
            hint: null,
            position: 0,
            internalPosition: 0,
            internalQuery: null,
            where: null,
            schemaName: null,
            tableName: null,
            columnName: null,
            dataTypeName: null,
            constraintName: constraintName,
            file: null,
            line: null,
            routine: null);

        return new DbUpdateException("Database update failed.", postgresException);
    }

    private sealed class TestDbContext : DbContext
    {
    }

    private sealed class TranslationDbContext : SharedDbContext<TranslationDbContext>
    {
        public TranslationDbContext(DbContextOptions<TranslationDbContext> options)
            : base(options)
        {
        }

        public DbSet<TranslationRecord> Records => Set<TranslationRecord>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<TranslationRecord>(builder =>
            {
                builder.HasKey(record => record.Id);
                builder.Property(record => record.Name).IsRequired();
            });
        }
    }

    private sealed class TranslationRecord
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// A real <see cref="SharedDbContext{TDbContext}"/> over in-memory SQLite whose save always fails with a
    /// supplied exception. Using the actual context, rather than calling the translator directly, is what proves
    /// the catch sites in <c>SaveChangesAsync</c> and <c>SaveChangesOnDbContextAsync</c> are reached.
    /// </summary>
    private sealed class TranslationHarness : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;
        private readonly ServiceProvider _serviceProvider;

        private TranslationHarness(
            SqliteConnection connection,
            ServiceProvider serviceProvider,
            TranslationDbContext dbContext)
        {
            _connection = connection;
            _serviceProvider = serviceProvider;
            DbContext = dbContext;
        }

        public TranslationDbContext DbContext { get; }

        public static async Task<TranslationHarness> CreateAsync(ThrowingSaveChangesInterceptor interceptor)
        {
            var connection = new SqliteConnection("DataSource=:memory:");
            connection.Open();

            var services = new ServiceCollection();
            services.AddSingleton<IAuditPropertySetter, NoopAuditPropertySetter>();
            services.AddSingleton<IAuditingManager, NoopAuditingManager>();
            services.AddOptions<SharedDatabaseExceptionTranslationOptions>()
                .Configure(options => options.Configure<TranslationDbContext>(
                    "UX_TranslationRecords_Name",
                    new SharedDatabaseConstraintDescriptor(
                        "translation_record_name_conflict",
                        "A translation record with this name already exists.")));
            var serviceProvider = services.BuildServiceProvider();

            var options = new DbContextOptionsBuilder<TranslationDbContext>()
                .UseSqlite(connection)
                .AddInterceptors(interceptor)
                .Options;
            var dbContext = TestDbContextInitialization.Initialize(
                new TranslationDbContext(options) { ServiceProvider = serviceProvider });
            await dbContext.Database.EnsureCreatedAsync();
            dbContext.Records.Add(new TranslationRecord { Id = 1, Name = "duplicate" });

            return new TranslationHarness(connection, serviceProvider, dbContext);
        }

        public async ValueTask DisposeAsync()
        {
            await DbContext.DisposeAsync();
            await _serviceProvider.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }

    private sealed class NoopAuditPropertySetter : IAuditPropertySetter
    {
        public void SetCreationProperties(object entity) { }
        public void SetModificationProperties(object entity) { }
        public void SetDeletionProperties(object entity) { }
        public void IncrementEntityVersionProperty(object entity) { }
    }

    /// <summary>
    /// Fails every save with a supplied exception, and counts attempts so a test can prove nothing was retried.
    /// </summary>
    private sealed class ThrowingSaveChangesInterceptor(Exception exception) : SaveChangesInterceptor
    {
        public int AttemptCount { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            AttemptCount++;
            return ValueTask.FromException<InterceptionResult<int>>(exception);
        }
    }

    private sealed class NoopAuditingManager : IAuditingManager
    {
        public IAuditLogScope? Current => null;

        public IAuditLogSaveHandle BeginScope()
        {
            throw new NotSupportedException();
        }
    }
}
