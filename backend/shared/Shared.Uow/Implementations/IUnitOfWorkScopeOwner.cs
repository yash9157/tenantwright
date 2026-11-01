namespace Shared.Uow.Implementations;

internal interface IUnitOfWorkScopeOwner
{
    void SetScopeLifetime(Action restoreAmbient, Func<ValueTask> disposeScopeAsync);
}
