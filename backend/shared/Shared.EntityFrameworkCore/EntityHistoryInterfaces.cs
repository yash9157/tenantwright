using Microsoft.EntityFrameworkCore.ChangeTracking;
using Shared.AuditLogs.Auditing;

namespace Shared.EntityFrameworkCore;

public interface IAuditPropertySetter
{
    void SetCreationProperties(object entity);
    void SetModificationProperties(object entity);
    void SetDeletionProperties(object entity);
    void IncrementEntityVersionProperty(object entity);
}

public interface IEntityHistoryHelper
{
    void InitializeNavigationHelper(object navigationHelper);
    List<EntityChangeInfo> CreateChangeList(List<EntityEntry> entries);
    void UpdateChangeList(List<EntityChangeInfo> entityChangeList);
}

public sealed class NullEntityHistoryHelper : IEntityHistoryHelper
{
    public static NullEntityHistoryHelper Instance { get; } = new();

    public void InitializeNavigationHelper(object navigationHelper)
    {
    }

    public List<EntityChangeInfo> CreateChangeList(List<EntityEntry> entries)
    {
        return [];
    }

    public void UpdateChangeList(List<EntityChangeInfo> entityChangeList)
    {
    }
}
