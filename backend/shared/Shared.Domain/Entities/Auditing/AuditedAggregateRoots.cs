using System;

namespace Shared.Domain.Entities.Auditing;

[Serializable]
public abstract class CreationAuditedAggregateRoot : AggregateRoot, ICreationAuditedObject
{
    public virtual DateTime CreationTime { get; set; }
    public virtual Guid? CreatorId { get; set; }
}

[Serializable]
public abstract class CreationAuditedAggregateRoot<TKey> : AggregateRoot<TKey>, ICreationAuditedObject
{
    public virtual DateTime CreationTime { get; set; }
    public virtual Guid? CreatorId { get; set; }

    protected CreationAuditedAggregateRoot() { }
    protected CreationAuditedAggregateRoot(TKey id) : base(id) { }
}

[Serializable]
public abstract class CreationAuditedAggregateRoot<TKey, TUser> : CreationAuditedAggregateRoot<TKey>, ICreationAuditedObject<TUser>
{
    public virtual TUser Creator { get; set; } = default!;

    protected CreationAuditedAggregateRoot() { }
    protected CreationAuditedAggregateRoot(TKey id) : base(id) { }
}

[Serializable]
public abstract class AuditedAggregateRoot : CreationAuditedAggregateRoot, IAuditedObject
{
    public virtual DateTime? LastModificationTime { get; set; }
    public virtual Guid? LastModifierId { get; set; }
}

[Serializable]
public abstract class AuditedAggregateRoot<TKey> : CreationAuditedAggregateRoot<TKey>, IAuditedObject
{
    public virtual DateTime? LastModificationTime { get; set; }
    public virtual Guid? LastModifierId { get; set; }

    protected AuditedAggregateRoot() { }
    protected AuditedAggregateRoot(TKey id) : base(id) { }
}

[Serializable]
public abstract class AuditedAggregateRoot<TKey, TUser> : AuditedAggregateRoot<TKey>, IAuditedObject<TUser>
{
    public virtual TUser Creator { get; set; } = default!;
    public virtual TUser LastModifier { get; set; } = default!;

    protected AuditedAggregateRoot() { }
    protected AuditedAggregateRoot(TKey id) : base(id) { }
}

[Serializable]
public abstract class FullAuditedAggregateRoot : AuditedAggregateRoot, IFullAuditedObject
{
    public virtual bool IsDeleted { get; set; }
    public virtual Guid? DeleterId { get; set; }
    public virtual DateTime? DeletionTime { get; set; }
}

[Serializable]
public abstract class FullAuditedAggregateRoot<TKey> : AuditedAggregateRoot<TKey>, IFullAuditedObject
{
    public virtual bool IsDeleted { get; set; }
    public virtual Guid? DeleterId { get; set; }
    public virtual DateTime? DeletionTime { get; set; }

    protected FullAuditedAggregateRoot() { }
    protected FullAuditedAggregateRoot(TKey id) : base(id) { }
}

[Serializable]
public abstract class FullAuditedAggregateRoot<TKey, TUser> : FullAuditedAggregateRoot<TKey>, IFullAuditedObject<TUser>
{
    public virtual TUser Creator { get; set; } = default!;
    public virtual TUser LastModifier { get; set; } = default!;
    public virtual TUser Deleter { get; set; } = default!;

    protected FullAuditedAggregateRoot() { }
    protected FullAuditedAggregateRoot(TKey id) : base(id) { }
}
