using System;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using Shared.Domain.Values;
using Shared.MultiTenancy;

namespace Shared.Domain.Entities;

public static class EntityHelper
{
    public static bool IsMultiTenant<TEntity>()
        where TEntity : IEntity
    {
        return IsMultiTenant(typeof(TEntity));
    }

    public static bool IsMultiTenant(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return typeof(IMultiTenant).IsAssignableFrom(type);
    }

    public static bool EntityEquals(IEntity? entity1, IEntity? entity2)
    {
        if (entity1 == null || entity2 == null)
        {
            return false;
        }

        if (ReferenceEquals(entity1, entity2))
        {
            return true;
        }

        var typeOfEntity1 = entity1.GetType();
        var typeOfEntity2 = entity2.GetType();
        if (!typeOfEntity1.IsAssignableFrom(typeOfEntity2) && !typeOfEntity2.IsAssignableFrom(typeOfEntity1))
        {
            return false;
        }

        if (entity1 is IMultiTenant && entity2 is IMultiTenant)
        {
            var tenant1Id = ((IMultiTenant)entity1).TenantId;
            var tenant2Id = ((IMultiTenant)entity2).TenantId;

            if (tenant1Id != tenant2Id)
            {
                if (tenant1Id == null || tenant2Id == null)
                {
                    return false;
                }

                if (!tenant1Id.Equals(tenant2Id))
                {
                    return false;
                }
            }
        }

        // Two transient entities should never be considered equal.
        if (HasDefaultKeys(entity1) && HasDefaultKeys(entity2))
        {
            return false;
        }

        var entity1Keys = entity1.GetKeys();
        var entity2Keys = entity2.GetKeys();

        if (entity1Keys.Length != entity2Keys.Length)
        {
            return false;
        }

        for (var i = 0; i < entity1Keys.Length; i++)
        {
            var entity1Key = entity1Keys[i];
            var entity2Key = entity2Keys[i];

            if (entity1Key == null)
            {
                if (entity2Key == null)
                {
                    continue;
                }

                return false;
            }

            if (entity2Key == null)
            {
                return false;
            }

            if (IsDefaultKeyValue(entity1Key) && IsDefaultKeyValue(entity2Key))
            {
                return false;
            }

            if (!entity1Key.Equals(entity2Key))
            {
                return false;
            }
        }

        return true;
    }

    public static bool IsEntity(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return typeof(IEntity).IsAssignableFrom(type);
    }

    public static bool IsValueObject(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        return typeof(ValueObject).IsAssignableFrom(type);
    }

    public static bool IsValueObject(object? obj)
    {
        return obj != null && IsValueObject(obj.GetType());
    }

    public static void CheckEntity(Type type)
    {
        if (!IsEntity(type))
        {
            throw new SharedException($"Given type is not an entity: {type.AssemblyQualifiedName}. It must implement {typeof(IEntity).AssemblyQualifiedName}.");
        }
    }

    public static bool IsEntityWithId(Type type)
    {
        foreach (var interfaceType in type.GetInterfaces())
        {
            if (interfaceType.GetTypeInfo().IsGenericType &&
                interfaceType.GetGenericTypeDefinition() == typeof(IEntity<>))
            {
                return true;
            }
        }

        return false;
    }

    public static bool HasDefaultId<TKey>(IEntity<TKey> entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (EqualityComparer<TKey>.Default.Equals(entity.Id, default!))
        {
            return true;
        }

        // EF Core can assign <= 0 sentinel values on attach.
        if (typeof(TKey) == typeof(int))
        {
            return Convert.ToInt32(entity.Id, System.Globalization.CultureInfo.InvariantCulture) <= 0;
        }

        if (typeof(TKey) == typeof(long))
        {
            return Convert.ToInt64(entity.Id, System.Globalization.CultureInfo.InvariantCulture) <= 0;
        }

        return false;
    }

    public static bool HasDefaultKeys(IEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        foreach (var key in entity.GetKeys())
        {
            if (!IsDefaultKeyValue(key))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsDefaultKeyValue(object? value)
    {
        if (value == null)
        {
            return true;
        }

        var type = value.GetType();

        // EF Core can assign <= 0 sentinel values on attach.
        if (type == typeof(int))
        {
            return Convert.ToInt32(value, System.Globalization.CultureInfo.InvariantCulture) <= 0;
        }

        if (type == typeof(long))
        {
            return Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture) <= 0;
        }

        return IsDefaultValue(value);
    }

    private static bool IsDefaultValue(object value)
    {
        var type = value.GetType();
        if (!type.IsValueType)
        {
            return false;
        }

        return value.Equals(Activator.CreateInstance(type)!);
    }

    public static Type? FindPrimaryKeyType(Type entityType)
    {
        if (!typeof(IEntity).IsAssignableFrom(entityType))
        {
            throw new SharedException($"Given type is not an entity. It should implement {typeof(IEntity).AssemblyQualifiedName}!");
        }

        foreach (var interfaceType in entityType.GetTypeInfo().GetInterfaces())
        {
            if (interfaceType.GetTypeInfo().IsGenericType &&
                interfaceType.GetGenericTypeDefinition() == typeof(IEntity<>))
            {
                return interfaceType.GenericTypeArguments[0];
            }
        }

        return null;
    }

    public static Type? FindPrimaryKeyType<TEntity>()
        where TEntity : IEntity
    {
        return FindPrimaryKeyType(typeof(TEntity));
    }

    public static Expression<Func<TEntity, bool>> CreateEqualityExpressionForId<TEntity, TKey>(TKey id)
        where TEntity : IEntity<TKey>
    {
        var lambdaParam = Expression.Parameter(typeof(TEntity));
        var leftExpression = Expression.PropertyOrField(lambdaParam, "Id");
        var idValue = Convert.ChangeType(id, typeof(TKey), System.Globalization.CultureInfo.InvariantCulture);
        Expression<Func<object?>> closure = () => idValue;
        var rightExpression = Expression.Convert(closure.Body, leftExpression.Type);
        var lambdaBody = Expression.Equal(leftExpression, rightExpression);
        return Expression.Lambda<Func<TEntity, bool>>(lambdaBody, lambdaParam);
    }

    public static void TrySetId<TKey>(
        IEntity<TKey> entity,
        Func<TKey> idFactory,
        bool checkForDisableIdGenerationAttribute = false)
    {
        ArgumentNullException.ThrowIfNull(entity);
        ArgumentNullException.ThrowIfNull(idFactory);

        if (checkForDisableIdGenerationAttribute)
        {
            var entityType = entity.GetType();
            var idProperty = entityType.GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (entityType.IsDefined(typeof(DisableIdGenerationAttribute), inherit: true) ||
                (idProperty?.IsDefined(typeof(DisableIdGenerationAttribute), inherit: true) ?? false))
            {
                return;
            }
        }

        if (!HasDefaultId(entity))
        {
            return;
        }

        var idPropertyInfo = entity.GetType().GetProperty("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (idPropertyInfo == null)
        {
            return;
        }

        var setter = idPropertyInfo.SetMethod;
        if (setter == null)
        {
            return;
        }

        setter.Invoke(entity, [idFactory()]);
    }

}
