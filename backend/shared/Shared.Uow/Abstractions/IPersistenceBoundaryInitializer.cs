namespace Shared.Uow.Abstractions;

/// <summary>
/// Initializes persistence resources after an owning unit of work becomes ambient.
/// Implementations must not create or complete the unit of work themselves.
/// </summary>
public interface IPersistenceBoundaryInitializer
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
