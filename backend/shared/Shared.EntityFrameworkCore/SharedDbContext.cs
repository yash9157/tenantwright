using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.Globalization;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Shared.AuditLogs.Auditing;
using Shared.Data.Exceptions;
using Shared.Domain;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore.ChangeTrackers;
using Shared.EntityFrameworkCore.GlobalFilters;
using Shared.EntityFrameworkCore.Modeling;
using Shared.EntityFrameworkCore.ValueConverters;
using Shared.EntityFrameworkCore.ExceptionTranslation;
using Shared.MultiTenancy;
using Shared.Uow.Abstractions;
using Shared.Modularity.Extensions;
using Shared.Data.Filtering;
using Microsoft.EntityFrameworkCore.Diagnostics;

using Shared.Guids;

namespace Shared.EntityFrameworkCore;

public abstract class SharedDbContext<TDbContext> : DbContext, ISharedEfCoreDbContext, ISharedEfCoreDbFunctionContext
    where TDbContext : DbContext
{
    private IServiceProvider? _resolvedServiceProvider;
    private SharedDbContextOptions? _sharedDbContextOptions;
    private SharedDatabaseExceptionTranslationOptions? _databaseExceptionTranslationOptions;
    private SharedEfCoreGlobalFilterOptions? _sharedEfCoreGlobalFilterOptions;
    private SharedEfCoreNavigationHelper? _sharedEfCoreNavigationHelper;
    private bool _isInitializedByDbContextProvider;

    public IServiceProvider ServiceProvider
    {
        get => GetOrResolveServiceProvider();
        set => _resolvedServiceProvider = value ?? throw new ArgumentNullException(nameof(value));
    }

    public IGuidGenerator GuidGenerator => ServiceProvider.GetService<IGuidGenerator>() ?? UuidV7GuidGenerator.Instance;

    protected virtual Guid? CurrentTenantId => CurrentTenant?.Id;

    protected virtual Guid CurrentTenantIdOrHost => CurrentTenantId ?? Guid.Empty;

    protected virtual bool IsMultiTenantFilterEnabled => DataFilter?.IsEnabled<IMultiTenant>() ?? false;

    protected virtual bool IsSoftDeleteFilterEnabled => DataFilter?.IsEnabled<ISoftDelete>() ?? false;

    public ICurrentTenant CurrentTenant => ServiceProvider.GetRequiredService<ICurrentTenant>();

    public IDataFilter DataFilter => ServiceProvider.GetRequiredService<IDataFilter>();

    public IAuditPropertySetter AuditPropertySetter => ServiceProvider.GetRequiredService<IAuditPropertySetter>();

    public IEntityHistoryHelper EntityHistoryHelper => ServiceProvider.GetService<IEntityHistoryHelper>() ?? NullEntityHistoryHelper.Instance;

    public IAuditingManager AuditingManager => ServiceProvider.GetRequiredService<IAuditingManager>();

    public IOptions<SharedEntityChangeOptions> EntityChangeOptions =>
        ServiceProvider.GetService<IOptions<SharedEntityChangeOptions>>()
        ?? Microsoft.Extensions.Options.Options.Create(new SharedEntityChangeOptions());

    public IUnitOfWorkManager UnitOfWorkManager => ServiceProvider.GetRequiredService<IUnitOfWorkManager>();

    public ILogger<SharedDbContext<TDbContext>> Logger =>
        ServiceProvider.GetService<ILogger<SharedDbContext<TDbContext>>>()
        ?? NullLogger<SharedDbContext<TDbContext>>.Instance;

    public SharedEfCoreNavigationHelper SharedEfCoreNavigationHelper =>
        _sharedEfCoreNavigationHelper ??= ServiceProvider.GetService<SharedEfCoreNavigationHelper>()
            ?? new SharedEfCoreNavigationHelper();

    public SharedDbContextOptions Options => _sharedDbContextOptions ??=
        ServiceProvider.GetService<IOptions<SharedDbContextOptions>>()?.Value
        ?? new SharedDbContextOptions();

    public SharedEfCoreGlobalFilterOptions GlobalFilterOptions => _sharedEfCoreGlobalFilterOptions ??=
        ServiceProvider.GetService<IOptions<SharedEfCoreGlobalFilterOptions>>()?.Value
        ?? new SharedEfCoreGlobalFilterOptions();

    protected virtual SharedDatabaseExceptionTranslationOptions DatabaseExceptionTranslationOptions =>
        _databaseExceptionTranslationOptions ??=
            ServiceProvider.GetService<IOptions<SharedDatabaseExceptionTranslationOptions>>()?.Value
            ?? new SharedDatabaseExceptionTranslationOptions();

    private static readonly MethodInfo ConfigureBasePropertiesMethodInfo
        = typeof(SharedDbContext<TDbContext>)
            .GetMethod(
                nameof(ConfigureBaseProperties),
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;

    private static readonly MethodInfo ConfigureValueConverterMethodInfo
        = typeof(SharedDbContext<TDbContext>)
            .GetMethod(
                nameof(ConfigureValueConverter),
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;

    private static readonly MethodInfo ConfigureValueGeneratedMethodInfo
        = typeof(SharedDbContext<TDbContext>)
            .GetMethod(
                nameof(ConfigureValueGenerated),
                BindingFlags.Instance | BindingFlags.NonPublic
            )!;

    protected DbContextOptions DbContextOptions { get; }

    protected SharedDbContext(DbContextOptions<TDbContext> options)
        : base(options)
    {
        DbContextOptions = options;
    }

    private IServiceProvider GetOrResolveServiceProvider()
    {
        if (_resolvedServiceProvider != null)
        {
            return _resolvedServiceProvider;
        }

        var applicationServiceProvider = DbContextOptions
            .FindExtension<CoreOptionsExtension>()
            ?.ApplicationServiceProvider;

        _resolvedServiceProvider = applicationServiceProvider ?? ((IInfrastructure<IServiceProvider>)this).Instance;
        return _resolvedServiceProvider;
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ExtraPropertyDictionary is a value-object property type (IHasExtraProperties), not an EF entity.
        modelBuilder.Ignore<ExtraPropertyDictionary>();

        TrySetDatabaseProvider(modelBuilder);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            ConfigureBasePropertiesMethodInfo
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, new object[] { modelBuilder, entityType });

            ConfigureValueConverterMethodInfo
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, new object[] { modelBuilder, entityType });

            ConfigureValueGeneratedMethodInfo
                .MakeGenericMethod(entityType.ClrType)
                .Invoke(this, new object[] { modelBuilder, entityType });
        }

        Options.DefaultOnModelCreatingAction?.Invoke(this, modelBuilder);
        foreach (var onModelCreatingAction in Options.OnModelCreatingActions.GetOrDefault(typeof(TDbContext)) ?? new List<object>())
        {
            (onModelCreatingAction as Action<DbContext, ModelBuilder>)?.Invoke(this, modelBuilder);
        }
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        base.ConfigureConventions(configurationBuilder);

        configurationBuilder.Conventions.Add(_ => new TenantQueryFilterFinalizingConvention(this));

        Options.DefaultConventionAction?.Invoke(this, configurationBuilder);
        foreach (var conventionAction in Options.ConventionActions.GetOrDefault(typeof(TDbContext)) ?? new List<object>())
        {
            (conventionAction as Action<DbContext, ModelConfigurationBuilder>)?.Invoke(this, configurationBuilder);
        }
    }

    protected virtual void TrySetDatabaseProvider(ModelBuilder modelBuilder)
    {
        var provider = GetDatabaseProviderOrNull(modelBuilder);
        if (provider != null)
        {
            modelBuilder.SetDatabaseProvider(provider.Value);
        }
    }

    protected virtual EfCoreDatabaseProvider? GetDatabaseProviderOrNull(ModelBuilder modelBuilder)
    {
        switch (Database.ProviderName)
        {
            case "Microsoft.EntityFrameworkCore.SqlServer":
                return EfCoreDatabaseProvider.SqlServer;
            case "Npgsql.EntityFrameworkCore.PostgreSQL":
                return EfCoreDatabaseProvider.PostgreSql;
            case "Pomelo.EntityFrameworkCore.MySql":
                return EfCoreDatabaseProvider.MySql;
            case "Oracle.EntityFrameworkCore":
            case "Devart.Data.Oracle.Entity.EFCore":
                return EfCoreDatabaseProvider.Oracle;
            case "Microsoft.EntityFrameworkCore.Sqlite":
                return EfCoreDatabaseProvider.Sqlite;
            case "Microsoft.EntityFrameworkCore.InMemory":
                return EfCoreDatabaseProvider.InMemory;
            case "FirebirdSql.EntityFrameworkCore.Firebird":
                return EfCoreDatabaseProvider.Firebird;
            case "Microsoft.EntityFrameworkCore.Cosmos":
                return EfCoreDatabaseProvider.Cosmos;
            default:
                return null;
        }
    }

    public virtual async Task<int> SaveChangesOnDbContextAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureInitializedForPersistenceBoundary();
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw CreateSharedDbConcurrencyException(exception);
        }
        catch (DbUpdateException exception)
        {
            if (TryTranslateDatabaseException(exception, out var translatedException))
            {
                throw translatedException;
            }

            throw;
        }
        catch (InvalidOperationException exception)
        {
            if (TryTranslateTransientDatabaseException(exception, out var translatedException))
            {
                throw translatedException;
            }

            throw;
        }
    }

    public async override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        var isEmptyUninitializedSave = EnsureInitializedForPersistenceBoundary();
        try
        {
            if (isEmptyUninitializedSave)
            {
                // Nothing is pending, so the shared business pipeline has nothing to apply and must not resolve
                // request or unit-of-work services for a context no persistence boundary initialized.
                return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
            }

            ApplySharedConceptsForChangedNavigationEntries();

            var auditLog = AuditingManager?.Current?.Log;
            List<EntityChangeInfo>? entityChangeList = null;
            if (auditLog != null)
            {
                EntityHistoryHelper.InitializeNavigationHelper(SharedEfCoreNavigationHelper);
                entityChangeList = EntityHistoryHelper.CreateChangeList(ChangeTracker.Entries().ToList());
            }

            HandlePropertiesBeforeSave();

            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);

            if (entityChangeList != null)
            {
                EntityHistoryHelper.UpdateChangeList(entityChangeList);
                auditLog!.EntityChanges.AddRange(entityChangeList);
                if (entityChangeList.Count > 0)
                {
                    Logger.LogDebug("Added {EntityChangeCount} entity changes to the current audit log", entityChangeList.Count);
                }
            }

            return result;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            throw CreateSharedDbConcurrencyException(exception);
        }
        catch (DbUpdateException exception)
        {
            if (TryTranslateDatabaseException(exception, out var translatedException))
            {
                throw translatedException;
            }

            throw;
        }
        catch (InvalidOperationException exception)
        {
            if (TryTranslateTransientDatabaseException(exception, out var translatedException))
            {
                throw translatedException;
            }

            throw;
        }
        finally
        {
            ChangeTracker.AutoDetectChangesEnabled = true;
            SharedEfCoreNavigationHelper.RemoveChangedEntityEntries();
        }
    }

    private SharedDbConcurrencyException CreateSharedDbConcurrencyException(DbUpdateConcurrencyException exception)
    {
        if (exception.Entries.Count > 0)
        {
            var sb = new StringBuilder();
            sb.AppendLine(exception.Entries.Count > 1
                ? "There are some entries which are not saved due to concurrency exception:"
                : "There is an entry which is not saved due to concurrency exception:");
            foreach (var entry in exception.Entries)
            {
                sb.AppendLine(entry.ToString());
            }

            Logger.LogWarning("Database concurrency conflict details: {ConcurrencyConflictDetails}", sb.ToString());
        }

        return new SharedDbConcurrencyException(exception.Message, exception);
    }

    private bool TryTranslateDatabaseException(
        DbUpdateException exception,
        out global::Shared.SharedException translatedException)
    {
        return PostgreSqlDatabaseExceptionTranslator.TryTranslate(
            exception,
            typeof(TDbContext),
            DatabaseExceptionTranslationOptions,
            out translatedException);
    }

    /// <summary>
    /// Recognizes the wrapper EF Core raises for a transient database failure, so a deadlock or serialization
    /// failure reaches the caller as a conflict rather than as an untranslated server fault.
    /// </summary>
    /// <remarks>
    /// Only a wrapper carrying a recognized transient PostgreSQL state is translated. Anything else — an
    /// unrelated <see cref="InvalidOperationException"/>, or one carrying an unrecognized state — returns false
    /// so the caller rethrows it with its original stack and inner chain intact. Nothing here retries the write.
    /// </remarks>
    private static bool TryTranslateTransientDatabaseException(
        InvalidOperationException exception,
        out global::Shared.SharedException translatedException)
    {
        return PostgreSqlDatabaseExceptionTranslator.TryTranslateTransientWrapper(exception, out translatedException);
    }

    protected virtual void ApplySharedConceptsForChangedNavigationEntries()
    {
        if (!EntityChangeOptions.Value.TrackNavigationChanges ||
            !EntityChangeOptions.Value.UpdateAggregateRootWhenNavigationChanges)
        {
            return;
        }

        foreach (var entityEntry in SharedEfCoreNavigationHelper.GetChangedEntityEntries())
        {
            var ignoredEntity = EntityChangeOptions.Value.IgnoredNavigationEntitySelectors.Any(
                selector => selector.Predicate(entityEntry.Entity.GetType()));
            var onlyForeignKeyModifiedEntity =
                entityEntry.State == EntityState.Modified && IsOnlyForeignKeysModified(entityEntry);
            if (onlyForeignKeyModifiedEntity ||
                (entityEntry.State == EntityState.Unchanged && ignoredEntity))
            {
                continue;
            }

            if (EntityChangeOptions.Value.IgnoredUpdateAggregateRootSelectors.All(
                    selector => !selector.Predicate(entityEntry.Entity.GetType())) &&
                entityEntry.State == EntityState.Unchanged)
            {
                ApplySharedConceptsForModifiedEntity(entityEntry, forceApply: true);
            }
        }
    }

    protected virtual void HandlePropertiesBeforeSave()
    {
        var entries = ChangeTracker.Entries().ToList();
        foreach (var entry in entries)
        {
            HandleExtraPropertiesOnSave(entry);

            if (entry.State == EntityState.Modified || entry.State == EntityState.Deleted)
            {
                UpdateConcurrencyStamp(entry);
            }
        }

        if (EntityChangeOptions.Value.TrackNavigationChanges &&
            EntityChangeOptions.Value.UpdateAggregateRootWhenNavigationChanges)
        {
            foreach (var entry in SharedEfCoreNavigationHelper.GetChangedEntityEntries()
                         .Where(x => x.State == EntityState.Unchanged)
                         .Where(x => EntityChangeOptions.Value.IgnoredNavigationEntitySelectors.All(selector => !selector.Predicate(x.Entity.GetType())))
                         .Where(x => EntityChangeOptions.Value.IgnoredUpdateAggregateRootSelectors.All(selector => !selector.Predicate(x.Entity.GetType()))))
            {
                UpdateConcurrencyStamp(entry);
            }
        }
    }

    protected virtual void HandleExtraPropertiesOnSave(EntityEntry entry)
    {
        if (entry.State == EntityState.Deleted || entry.State == EntityState.Unchanged)
        {
            return;
        }

        if (entry.Entity is not IHasExtraProperties hasExtraProperties)
        {
            return;
        }

        if (hasExtraProperties.ExtraProperties != null)
        {
            return;
        }

        TrySetExtraProperties(entry.Entity, new ExtraPropertyDictionary());
    }

    protected virtual void ApplySharedConceptsForAddedEntity(EntityEntry entry)
    {
        CheckAndSetId(entry);
        CheckAndSetTenantId(entry);
        SetConcurrencyStampIfNull(entry);
        SetCreationAuditProperties(entry);
    }

    protected virtual void ApplySharedConceptsForModifiedEntity(EntityEntry entry, bool forceApply = false)
    {
        CheckMultiTenantMatch(entry);

        if (forceApply ||
            GetAllPropertyEntries(entry).Any(x => x.IsModified && (x.Metadata.ValueGenerated == ValueGenerated.Never || x.Metadata.ValueGenerated == ValueGenerated.OnAdd)))
        {
            IncrementEntityVersionProperty(entry);
            SetModificationAuditProperties(entry);

            if (entry.Entity is ISoftDelete softDelete && softDelete.IsDeleted)
            {
                SetDeletionAuditProperties(entry);
            }
        }
    }

    protected virtual void ApplySharedConceptsForDeletedEntity(EntityEntry entry)
    {
        if (!(entry.Entity is ISoftDelete softDelete))
        {
            return;
        }

        ExtraPropertyDictionary? originalExtraProperties = null;
        if (entry.Entity is IHasExtraProperties hasExtraProperties)
        {
            originalExtraProperties = new ExtraPropertyDictionary(hasExtraProperties.ExtraProperties);
        }

        entry.Reload();

        if (originalExtraProperties != null)
        {
            TrySetExtraProperties(entry.Entity, originalExtraProperties);
        }

        softDelete.IsDeleted = true;
        entry.State = EntityState.Modified;
        CheckMultiTenantMatch(entry);
        SetDeletionAuditProperties(entry);
    }

    protected virtual void CheckAndSetTenantId(EntityEntry entry)
    {
        if (!typeof(IMultiTenant).IsAssignableFrom(entry.Entity.GetType()))
        {
            return;
        }

        var tenantProperty = entry.Metadata.FindProperty(nameof(IMultiTenant.TenantId));
        if (tenantProperty == null)
        {
            return;
        }

        var expectedTenantId = CurrentTenantId;
        var currentTenantId = NormalizeTenantId(entry.Property(tenantProperty.Name).CurrentValue);
        if (!expectedTenantId.HasValue)
        {
            // Host operations may create a host-owned row or explicitly select a tenant-owned row.
            // The entity's TenantId is the stated target; tenant-context writes remain strict below.
            return;
        }

        if (expectedTenantId.HasValue && !currentTenantId.HasValue)
        {
            entry.Property(tenantProperty.Name).CurrentValue = expectedTenantId.Value;
            return;
        }

        if (currentTenantId != expectedTenantId)
        {
            throw CreateCrossTenantWriteException(entry, expectedTenantId, currentTenantId);
        }
    }

    protected virtual void CheckMultiTenantMatch(EntityEntry entry)
    {
        if (!typeof(IMultiTenant).IsAssignableFrom(entry.Entity.GetType()))
        {
            return;
        }

        var tenantProperty = entry.Metadata.FindProperty(nameof(IMultiTenant.TenantId));
        if (tenantProperty == null)
        {
            return;
        }

        var expectedTenantId = CurrentTenantId;
        var currentTenantId = NormalizeTenantId(entry.Property(tenantProperty.Name).CurrentValue);
        if (!expectedTenantId.HasValue)
        {
            return;
        }

        if (currentTenantId != expectedTenantId)
        {
            throw CreateCrossTenantWriteException(entry, expectedTenantId, currentTenantId);
        }
    }

    private static Guid? NormalizeTenantId(object? value)
        => value switch
        {
            Guid tenantId when tenantId != Guid.Empty => tenantId,
            _ => null
        };

    private static InvalidOperationException CreateCrossTenantWriteException(
        EntityEntry entry,
        Guid? expectedTenantId,
        Guid? entityTenantId)
        => new(
            $"Cross-tenant write detected for entity '{entry.Metadata.ClrType.FullName}'. " +
            $"CurrentTenantId='{expectedTenantId?.ToString() ?? "host"}', " +
            $"EntityTenantId='{entityTenantId?.ToString() ?? "host"}'.");

    private static void TrySetExtraProperties(object entity, ExtraPropertyDictionary extraProperties)
    {
        var property = entity.GetType().GetProperty(
            nameof(IHasExtraProperties.ExtraProperties),
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic
        );

        if (property == null || !property.CanWrite)
        {
            return;
        }

        property.SetValue(entity, extraProperties);
    }

    protected virtual void UpdateConcurrencyStamp(EntityEntry entry)
    {
        var entity = entry.Entity as IHasConcurrencyStamp;
        if (entity == null)
        {
            return;
        }

        entry.Property(nameof(IHasConcurrencyStamp.ConcurrencyStamp)).OriginalValue = entity.ConcurrencyStamp;
        entity.ConcurrencyStamp = Guid.NewGuid().ToString("N");
    }

    protected virtual void SetConcurrencyStampIfNull(EntityEntry entry)
    {
        var entity = entry.Entity as IHasConcurrencyStamp;
        if (entity == null)
        {
            return;
        }

        if (entity.ConcurrencyStamp != null)
        {
            return;
        }

        entity.ConcurrencyStamp = Guid.NewGuid().ToString("N");
    }

    protected virtual void CheckAndSetId(EntityEntry entry)
    {
        if (entry.Entity is IEntity<Guid> entityWithGuidId)
        {
            EntityHelper.TrySetId(entityWithGuidId, GuidGenerator.Create, checkForDisableIdGenerationAttribute: true);
        }
    }

    protected virtual void SetCreationAuditProperties(EntityEntry entry)
    {
        AuditPropertySetter?.SetCreationProperties(entry.Entity);
    }

    protected virtual void SetModificationAuditProperties(EntityEntry entry)
    {
        AuditPropertySetter?.SetModificationProperties(entry.Entity);
    }

    protected virtual void SetDeletionAuditProperties(EntityEntry entry)
    {
        AuditPropertySetter?.SetDeletionProperties(entry.Entity);
    }

    protected virtual void IncrementEntityVersionProperty(EntityEntry entry)
    {
        AuditPropertySetter?.IncrementEntityVersionProperty(entry.Entity);
    }

    protected virtual void ConfigureBaseProperties<TEntity>(ModelBuilder modelBuilder, IMutableEntityType mutableEntityType)
        where TEntity : class
    {
        if (mutableEntityType.IsOwned())
        {
            return;
        }

        if (typeof(IEntity).IsAssignableFrom(typeof(TEntity)))
        {
            CreateEntityTypeBuilderFromMutableEntityType<TEntity>(modelBuilder, mutableEntityType)
                .ConfigureByConvention();
        }

        ConfigureGlobalFilters<TEntity>(modelBuilder, mutableEntityType);
        mutableEntityType.SetAnnotation(
            TenantQueryFilterFinalizingConvention.ConfiguredAnnotationName,
            true);
    }

    protected virtual void ConfigureGlobalFilters<TEntity>(ModelBuilder modelBuilder, IMutableEntityType mutableEntityType)
        where TEntity : class
    {
        if (mutableEntityType.BaseType == null && ShouldFilterEntity<TEntity>(mutableEntityType))
        {
            var filterExpression = CreateFilterExpression<TEntity>(modelBuilder);
            if (filterExpression != null)
            {
                CreateEntityTypeBuilderFromMutableEntityType<TEntity>(modelBuilder, mutableEntityType).HasSharedQueryFilter(filterExpression);
            }
        }
    }

    protected virtual EntityTypeBuilder<TEntity> CreateEntityTypeBuilderFromMutableEntityType<TEntity>(
        ModelBuilder modelBuilder,
        IMutableEntityType mutableEntityType)
        where TEntity : class
    {
        return mutableEntityType.HasSharedClrType
            ? modelBuilder.SharedTypeEntity<TEntity>(mutableEntityType.Name)
            : modelBuilder.Entity<TEntity>();
    }

    protected virtual void ConfigureValueConverter<TEntity>(ModelBuilder modelBuilder, IMutableEntityType mutableEntityType)
        where TEntity : class
    {
        if (mutableEntityType.BaseType != null || mutableEntityType.IsOwned())
        {
            return;
        }

        var clock = ServiceProvider.GetService<IClock>();
        if (clock == null)
        {
            return;
        }

        var entityTypeBuilder = CreateEntityTypeBuilderFromMutableEntityType<TEntity>(modelBuilder, mutableEntityType);
        foreach (var property in mutableEntityType.GetProperties().Where(property =>
                     property.PropertyInfo != null &&
                     property.PropertyInfo.CanWrite &&
                     (property.PropertyInfo.PropertyType == typeof(DateTime) ||
                      property.PropertyInfo.PropertyType == typeof(DateTime?))))
        {
            entityTypeBuilder
                .Property(property.Name)
                .HasConversion(property.ClrType == typeof(DateTime)
                    ? new SharedDateTimeValueConverter(clock)
                    : new SharedNullableDateTimeValueConverter(clock));
        }
    }

    protected virtual void ConfigureValueGenerated<TEntity>(ModelBuilder modelBuilder, IMutableEntityType mutableEntityType)
        where TEntity : class
    {
        if (!typeof(IEntity<Guid>).IsAssignableFrom(typeof(TEntity)))
        {
            return;
        }

        var idPropertyBuilder = CreateEntityTypeBuilderFromMutableEntityType<TEntity>(modelBuilder, mutableEntityType).Property("Id");
        idPropertyBuilder.ValueGeneratedNever();
    }

    protected virtual bool ShouldFilterEntity<TEntity>(IMutableEntityType entityType) where TEntity : class
    {
        if (typeof(IMultiTenant).IsAssignableFrom(typeof(TEntity)))
        {
            return true;
        }

        if (typeof(ISoftDelete).IsAssignableFrom(typeof(TEntity)))
        {
            return true;
        }

        return false;
    }

    protected virtual Expression<Func<TEntity, bool>>? CreateFilterExpression<TEntity>(ModelBuilder modelBuilder)
        where TEntity : class
    {
        Expression<Func<TEntity, bool>>? expression = null;

        if (typeof(ISoftDelete).IsAssignableFrom(typeof(TEntity)))
        {
            expression = e => !IsSoftDeleteFilterEnabled || !((ISoftDelete)e).IsDeleted;
        }

        if (typeof(IMultiTenant).IsAssignableFrom(typeof(TEntity)))
        {
            var tenantProperty = modelBuilder.Model.FindEntityType(typeof(TEntity))?
                .FindProperty(nameof(IMultiTenant.TenantId));
            Expression<Func<TEntity, bool>> multiTenantFilter = tenantProperty?.ClrType == typeof(Guid)
                ? e => !IsMultiTenantFilterEnabled || EF.Property<Guid>(e, nameof(IMultiTenant.TenantId)) == CurrentTenantIdOrHost
                : e => !IsMultiTenantFilterEnabled || ((IMultiTenant)e).TenantId == CurrentTenantId;
            expression = expression == null ? multiTenantFilter : QueryFilterExpressionHelper.CombineExpressions(expression, multiTenantFilter);
        }

        return expression;
    }

    protected virtual void ChangeTrackerTracked(object? sender, EntityTrackedEventArgs e)
    {
        SharedEfCoreNavigationHelper.ChangeTrackerTracked(sender, e);
        PublishEventsForTrackedEntity(e.Entry);
    }

    protected virtual void ChangeTrackerStateChanged(object? sender, EntityStateChangedEventArgs e)
    {
        SharedEfCoreNavigationHelper.ChangeTrackerStateChanged(sender, e);
        PublishEventsForTrackedEntity(e.Entry);
    }

    protected virtual void PublishEventsForTrackedEntity(EntityEntry entry)
    {
        switch (entry.State)
        {
            case EntityState.Added:
                ApplySharedConceptsForAddedEntity(entry);
                break;
            case EntityState.Modified:
                var modifiedProperties = GetAllPropertyEntries(entry)
                    .Where(x => x.IsModified && (x.Metadata.ValueGenerated == ValueGenerated.Never || x.Metadata.ValueGenerated == ValueGenerated.OnAdd))
                    .ToList();

                if (modifiedProperties.Count > 0)
                {
                    if (IsOnlyForeignKeysModified(entry, modifiedProperties))
                    {
                        break;
                    }

                    var disableAuditingAttributes = modifiedProperties
                        .Select(x => x.Metadata.PropertyInfo?.GetCustomAttribute<DisableAuditingAttribute>(inherit: true))
                        .ToList();

                    if (disableAuditingAttributes.Any(x => x == null || x.UpdateModificationProps))
                    {
                        ApplySharedConceptsForModifiedEntity(entry);
                    }

                }
                else if (EntityChangeOptions.Value.TrackNavigationChanges &&
                         EntityChangeOptions.Value.IgnoredNavigationEntitySelectors.All(selector => !selector.Predicate(entry.Entity.GetType())) &&
                         SharedEfCoreNavigationHelper.IsNavigationEntryModified(entry) &&
                         !IsOnlyForeignKeysModified(entry))
                {
                    if (EntityChangeOptions.Value.UpdateAggregateRootWhenNavigationChanges &&
                        EntityChangeOptions.Value.IgnoredUpdateAggregateRootSelectors.All(selector => !selector.Predicate(entry.Entity.GetType())))
                    {
                        ApplySharedConceptsForModifiedEntity(entry, forceApply: true);
                    }

                }
                break;
            case EntityState.Deleted:
                ApplySharedConceptsForDeletedEntity(entry);
                break;
        }
    }

    protected virtual IEnumerable<PropertyEntry> GetAllPropertyEntries(EntityEntry entry)
    {
        return entry.Properties.Concat(GetAllComplexPropertyEntries(entry.ComplexProperties));
    }

    protected virtual IEnumerable<PropertyEntry> GetAllComplexPropertyEntries(IEnumerable<ComplexPropertyEntry> complexPropertyEntries)
    {
        foreach (var complexPropertyEntry in complexPropertyEntries)
        {
            var complexPropertyInfo = complexPropertyEntry.Metadata.PropertyInfo;
            if (complexPropertyInfo != null && complexPropertyInfo.IsDefined(typeof(DisableAuditingAttribute), true))
            {
                continue;
            }

            foreach (var propertyEntry in complexPropertyEntry.Properties)
            {
                yield return propertyEntry;
            }

            foreach (var nestedPropertyEntry in GetAllComplexPropertyEntries(complexPropertyEntry.ComplexProperties))
            {
                yield return nestedPropertyEntry;
            }
        }
    }

    protected virtual bool IsOnlyForeignKeysModified(EntityEntry entry, IReadOnlyCollection<PropertyEntry>? modifiedProperties = null)
    {
        var properties = (modifiedProperties?.Count > 0
            ? modifiedProperties
            : GetAllPropertyEntries(entry).Where(x => x.IsModified).ToList()).ToList();

        if (properties.Count == 0)
        {
            return false;
        }

        return properties.All(x =>
            x.Metadata.IsForeignKey());
    }

    public virtual void Initialize(EfCoreDbContextInitializationContext initializationContext)
    {
        if (_isInitializedByDbContextProvider)
        {
            throw new InvalidOperationException($"DbContext '{GetType().FullName}' was initialized more than once by IDbContextProvider.");
        }

        if (initializationContext.TimeoutMilliseconds.HasValue &&
            Database.IsRelational() &&
            !Database.GetCommandTimeout().HasValue)
        {
            Database.SetCommandTimeout(TimeSpan.FromMilliseconds(initializationContext.TimeoutMilliseconds.Value));
        }

        ChangeTracker.CascadeDeleteTiming = CascadeTiming.OnSaveChanges;

        ChangeTracker.Tracked += ChangeTrackerTracked;
        ChangeTracker.StateChanged += ChangeTrackerStateChanged;
        _isInitializedByDbContextProvider = true;
    }

    private bool EnsureInitializedForPersistenceBoundary()
    {
        if (_isInitializedByDbContextProvider)
        {
            return false;
        }

        if (!ChangeTracker.Entries().Any(entry =>
                entry.State is EntityState.Added or EntityState.Modified or EntityState.Deleted))
        {
            // A zero-entry SaveChanges on an uninitialized context persists nothing and is harmless. Any pending entity
            // in an uninitialized context fails closed.
            return true;
        }

        throw new InvalidOperationException(
            $"DbContext '{GetType().FullName}' was not initialized by IDbContextProvider for an active persistence boundary.");
    }

    public virtual string GetCompiledQueryCacheKey()
    {
        return $"{CurrentTenantId?.ToString() ?? "Null"}:{IsSoftDeleteFilterEnabled}:{IsMultiTenantFilterEnabled}";
    }
}
