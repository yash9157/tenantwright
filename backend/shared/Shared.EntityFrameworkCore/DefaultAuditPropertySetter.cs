using System;
using System.Reflection;
using Shared.CurrentUser;
using Shared.Domain;
using Shared.Modularity.DependencyInjection;

namespace Shared.EntityFrameworkCore;

public class DefaultAuditPropertySetter : IAuditPropertySetter, ITransientDependency
{
    private readonly ICurrentUser _currentUser;

    public DefaultAuditPropertySetter(ICurrentUser currentUser)
    {
        _currentUser = currentUser;
    }

    public void SetCreationProperties(object entity)
    {
        var now = DateTime.UtcNow;

        if (entity is IHasCreationTime hasCreationTime && hasCreationTime.CreationTime == default)
        {
            hasCreationTime.CreationTime = now;
        }

        if (entity is ICreationAuditedObject creationAudited && creationAudited.CreatorId == null)
        {
            creationAudited.CreatorId = _currentUser.Id;
        }
    }

    public void SetModificationProperties(object entity)
    {
        var now = DateTime.UtcNow;

        if (entity is IHasModificationTime hasModificationTime)
        {
            hasModificationTime.LastModificationTime = now;
        }

        if (entity is IModificationAuditedObject modificationAudited)
        {
            modificationAudited.LastModifierId = _currentUser.Id;
        }
    }

    public void SetDeletionProperties(object entity)
    {
        var now = DateTime.UtcNow;

        if (entity is IHasDeletionTime hasDeletionTime)
        {
            hasDeletionTime.DeletionTime = now;
        }

        if (entity is IDeletionAuditedObject deletionAudited)
        {
            deletionAudited.DeleterId = _currentUser.Id;
        }
    }

    public void IncrementEntityVersionProperty(object entity)
    {
        var property =
            entity.GetType().GetProperty("EntityVersion", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? entity.GetType().GetProperty("Version", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        if (property == null || !property.CanRead || !property.CanWrite)
        {
            return;
        }

        var propertyType = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        if (propertyType != typeof(int) && propertyType != typeof(long))
        {
            return;
        }

        var currentValue = property.GetValue(entity);
        var nextValue = currentValue == null
            ? (propertyType == typeof(int) ? 1 : 1L)
            : (propertyType == typeof(int)
                ? (object)(((int)currentValue) + 1)
                : (object)(((long)currentValue) + 1));

        property.SetValue(entity, nextValue);
    }
}
