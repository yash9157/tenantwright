using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Metadata.Conventions;
using Shared.Domain;

namespace Shared.EntityFrameworkCore.Modeling;

internal sealed class TenantQueryFilterFinalizingConvention : IModelFinalizingConvention
{
    internal const string ConfiguredAnnotationName = "Shared:GlobalFiltersConfigured";
    private const string LateMappedFilterName = "Shared:TenantAndSoftDelete";

    private readonly object _dbContext;
    private readonly PropertyInfo _currentTenantIdProperty;
    private readonly PropertyInfo _multiTenantFilterEnabledProperty;
    private readonly PropertyInfo _softDeleteFilterEnabledProperty;

    public TenantQueryFilterFinalizingConvention(object dbContext)
    {
        _dbContext = dbContext;
        var contextType = dbContext.GetType();
        _currentTenantIdProperty = FindContextProperty(contextType, "CurrentTenantId");
        _multiTenantFilterEnabledProperty = FindContextProperty(contextType, "IsMultiTenantFilterEnabled");
        _softDeleteFilterEnabledProperty = FindContextProperty(contextType, "IsSoftDeleteFilterEnabled");
    }

    public void ProcessModelFinalizing(
        IConventionModelBuilder modelBuilder,
        IConventionContext<IConventionModelBuilder> context)
    {
        foreach (var entityType in modelBuilder.Metadata.GetEntityTypes()
                     .Where(entityType => !entityType.IsOwned() && entityType.BaseType is null)
                     .Where(entityType => entityType.FindAnnotation(ConfiguredAnnotationName) is null))
        {
            var filter = BuildSharedFilter(entityType.ClrType);
            if (filter is null)
            {
                continue;
            }

            entityType.Builder.HasQueryFilter(
                LateMappedFilterName,
                filter,
                fromDataAnnotation: false);
            entityType.Builder.HasAnnotation(
                ConfiguredAnnotationName,
                true,
                fromDataAnnotation: false);
        }

        var unfilteredTenantTypes = modelBuilder.Metadata.GetEntityTypes()
            .Where(entityType => !entityType.IsOwned())
            .Where(entityType => typeof(IMultiTenant).IsAssignableFrom(entityType.ClrType))
            .Where(entityType => entityType.GetRootType().GetDeclaredQueryFilters().Count == 0)
            .Select(entityType => entityType.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (unfilteredTenantTypes.Length > 0)
        {
            throw new InvalidOperationException(
                $"Tenant-scoped EF Core entity types must have a global query filter: {string.Join(", ", unfilteredTenantTypes)}.");
        }
    }

    private LambdaExpression? BuildSharedFilter(Type entityType)
    {
        var entity = Expression.Parameter(entityType, "entity");
        Expression? body = null;
        var context = Expression.Constant(_dbContext);

        if (typeof(ISoftDelete).IsAssignableFrom(entityType))
        {
            var filterDisabled = Expression.Not(Expression.Property(context, _softDeleteFilterEnabledProperty));
            var isDeleted = Expression.Property(
                Expression.Convert(entity, typeof(ISoftDelete)),
                nameof(ISoftDelete.IsDeleted));
            body = Expression.OrElse(filterDisabled, Expression.Not(isDeleted));
        }

        if (typeof(IMultiTenant).IsAssignableFrom(entityType))
        {
            var filterDisabled = Expression.Not(Expression.Property(context, _multiTenantFilterEnabledProperty));
            var entityTenantId = Expression.Property(
                Expression.Convert(entity, typeof(IMultiTenant)),
                nameof(IMultiTenant.TenantId));
            var currentTenantId = Expression.Property(context, _currentTenantIdProperty);
            var tenantBody = Expression.OrElse(filterDisabled, Expression.Equal(entityTenantId, currentTenantId));
            body = body is null ? tenantBody : Expression.AndAlso(body, tenantBody);
        }

        return body is null ? null : Expression.Lambda(body, entity);
    }

    private static PropertyInfo FindContextProperty(Type contextType, string propertyName)
    {
        for (var type = contextType; type is not null; type = type.BaseType)
        {
            var property = type.GetProperty(
                propertyName,
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            if (property is not null)
            {
                return property;
            }
        }

        throw new InvalidOperationException(
            $"DbContext '{contextType.FullName}' does not expose the required filter property '{propertyName}'.");
    }
}
