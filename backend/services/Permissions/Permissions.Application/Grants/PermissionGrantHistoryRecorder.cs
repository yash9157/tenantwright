using Permissions.Domain.Grants;
using Permissions.Domain.Shared.Enums;
using Shared.CurrentUser;
using Shared.Domain.Repositories;
using Shared.Modularity.DependencyInjection;

namespace Permissions.Application.Grants;

/// <summary>
/// Appends rows to the append-only permission grant history (gap claim 8). Called from every
/// permission mutation path so the history reflects grants, revokes, effect changes, and the
/// approval lifecycle. Writes with <c>autoSave: false</c> so each entry participates in the same
/// unit of work / transaction as the change it records; the caller saves.
/// </summary>
public sealed class PermissionGrantHistoryRecorder : ITransientDependency
{
    private readonly IRepository<PermissionGrantHistoryEntry, Guid> _historyRepository;
    private readonly ICurrentUser _currentUser;

    public PermissionGrantHistoryRecorder(
        IRepository<PermissionGrantHistoryEntry, Guid> historyRepository,
        ICurrentUser currentUser)
    {
        _historyRepository = historyRepository;
        _currentUser = currentUser;
    }

    public Task RecordAsync(
        Guid? tenantId,
        string providerName,
        string providerKey,
        string permissionName,
        PermissionGrantHistoryChangeType changeType,
        PermissionGrantEffect? oldEffect,
        PermissionGrantEffect? newEffect,
        DateTime? expiresAtUtc,
        string? reason,
        string? ticketId,
        CancellationToken cancellationToken = default)
    {
        var entry = new PermissionGrantHistoryEntry(
            id: Guid.CreateVersion7(),
            tenantId: tenantId,
            providerName: providerName,
            providerKey: providerKey,
            permissionName: permissionName,
            resourceName: null,
            resourceKey: null,
            resourceOwnerRevision: null,
            changeType: changeType,
            oldEffect: oldEffect,
            newEffect: newEffect,
            expiresAtUtc: expiresAtUtc,
            reason: reason,
            ticketId: ticketId,
            actorUserId: _currentUser.Id,
            occurredAtUtc: DateTime.UtcNow);

        return _historyRepository.InsertAsync(entry, autoSave: false, cancellationToken: cancellationToken);
    }
}
