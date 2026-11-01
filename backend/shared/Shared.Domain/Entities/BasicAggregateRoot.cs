using System;
namespace Shared.Domain.Entities;

[Serializable]
public abstract class BasicAggregateRoot : Entity,
    IAggregateRoot
{
}

[Serializable]
public abstract class BasicAggregateRoot<TKey> : Entity<TKey>,
    IAggregateRoot<TKey>
{
    protected BasicAggregateRoot()
    {

    }

    protected BasicAggregateRoot(TKey id)
        : base(id)
    {

    }

}
