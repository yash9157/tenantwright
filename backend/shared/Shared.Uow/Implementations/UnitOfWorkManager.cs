using System;
using Microsoft.Extensions.DependencyInjection;
using Shared.Uow.Abstractions;
using Shared.Uow.Options;
using Shared.Modularity.DependencyInjection;

namespace Shared.Uow.Implementations;

public class UnitOfWorkManager : IUnitOfWorkManager, ISingletonDependency
{
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly IAmbientUnitOfWork _ambientUnitOfWork;

    public IUnitOfWork? Current => _ambientUnitOfWork.GetCurrentByChecking();

    public UnitOfWorkManager(
        IAmbientUnitOfWork ambientUnitOfWork,
        IServiceScopeFactory serviceScopeFactory)
    {
        _ambientUnitOfWork = ambientUnitOfWork;
        _serviceScopeFactory = serviceScopeFactory;
    }

    public IUnitOfWork Begin(UnitOfWorkOptions options, bool requiresNew = false)
    {
        ArgumentNullException.ThrowIfNull(options);

        var currentUow = Current;
        if (currentUow != null && !requiresNew)
        {
            return new ChildUnitOfWork(currentUow);
        }

        return CreateNewUnitOfWork(unitOfWork => unitOfWork.Initialize(options));
    }

    public IUnitOfWork Begin(bool requiresNew = false)
    {
        return Begin(new UnitOfWorkOptions(), requiresNew);
    }

    public IUnitOfWork Reserve(string reservationName, bool requiresNew = false)
    {
        if (string.IsNullOrWhiteSpace(reservationName))
        {
            throw new ArgumentException("Reservation name cannot be null or whitespace.", nameof(reservationName));
        }

        var ambient = _ambientUnitOfWork.UnitOfWork;
        if (!requiresNew &&
            ambient != null &&
            ambient.IsReserved &&
            string.Equals(ambient.ReservationName, reservationName, StringComparison.Ordinal))
        {
            return new ChildUnitOfWork(ambient);
        }

        return CreateNewUnitOfWork(unitOfWork => unitOfWork.Reserve(reservationName));
    }

    public void BeginReserved(string reservationName, UnitOfWorkOptions options)
    {
        if (!TryBeginReserved(reservationName, options))
        {
            throw new InvalidOperationException($"Could not find a reserved unit of work with reservation name: {reservationName}");
        }
    }

    public bool TryBeginReserved(string reservationName, UnitOfWorkOptions options)
    {
        if (string.IsNullOrWhiteSpace(reservationName))
        {
            throw new ArgumentException("Reservation name cannot be null or whitespace.", nameof(reservationName));
        }

        var uow = _ambientUnitOfWork.UnitOfWork;
        while (uow != null && (!uow.IsReserved || !string.Equals(uow.ReservationName, reservationName, StringComparison.Ordinal)))
        {
            uow = uow.Outer;
        }

        if (uow == null || uow.IsDisposed || uow.IsCompleted)
        {
            return false;
        }

        uow.Initialize(options);
        return true;
    }

    private IUnitOfWork CreateNewUnitOfWork(Action<IUnitOfWork> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);

        var outerUow = _ambientUnitOfWork.UnitOfWork;
        var scope = _serviceScopeFactory.CreateAsyncScope();
        IUnitOfWork? unitOfWork = null;
        var scopeLifetimeTransferred = false;

        try
        {
            unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            if (unitOfWork is not IUnitOfWorkScopeOwner scopeOwner)
            {
                throw new InvalidOperationException(
                    $"The registered {nameof(IUnitOfWork)} implementation must implement the internal scope lifetime contract.");
            }

            unitOfWork.SetOuter(outerUow);
            configure(unitOfWork);
            scopeOwner.SetScopeLifetime(
                () => _ambientUnitOfWork.SetUnitOfWork(outerUow),
                () => scope.DisposeAsync());
            scopeLifetimeTransferred = true;
            _ambientUnitOfWork.SetUnitOfWork(unitOfWork);

            return unitOfWork;
        }
        catch (Exception creationException)
        {
            _ambientUnitOfWork.SetUnitOfWork(outerUow);
            try
            {
                if (scopeLifetimeTransferred && unitOfWork is not null)
                {
                    unitOfWork.Dispose();
                }
                else
                {
                    scope.DisposeAsync().AsTask().GetAwaiter().GetResult();
                }
            }
            catch (Exception disposalException)
            {
                throw new AggregateException(
                    "Unit of work scope creation and cleanup both failed.",
                    creationException,
                    disposalException);
            }

            throw;
        }
    }
}
