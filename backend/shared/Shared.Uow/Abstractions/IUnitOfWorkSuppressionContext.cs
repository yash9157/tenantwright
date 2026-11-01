namespace Shared.Uow.Abstractions;

/// <summary>Describes a bounded scope whose transaction is owned by external infrastructure.</summary>
public interface IUnitOfWorkSuppressionContext
{
    bool IsSuppressed { get; }

    IDisposable Suppress();
}
