using System;

namespace Shared.Domain.Entities.Auditing;

[Serializable]
public abstract class CreationAuditedEntity : Entity, ICreationAuditedObject
{
    public virtual DateTime CreationTime { get; set; }
    public virtual Guid? CreatorId { get; set; }
}

[Serializable]
public abstract class CreationAuditedEntity<TKey> : Entity<TKey>, ICreationAuditedObject
{
    public virtual DateTime CreationTime { get; set; }
    public virtual Guid? CreatorId { get; set; }

    protected CreationAuditedEntity() { }
    protected CreationAuditedEntity(TKey id) : base(id) { }
}

[Serializable]
public abstract class CreationAuditedEntity<TKey, TUser> : CreationAuditedEntity<TKey>, ICreationAuditedObject<TUser>
{
    public virtual TUser Creator { get; set; } = default!;

    protected CreationAuditedEntity() { }
    protected CreationAuditedEntity(TKey id) : base(id) { }
}

[Serializable]
public abstract class AuditedEntity : CreationAuditedEntity, IAuditedObject
{
    public virtual DateTime? LastModificationTime { get; set; }
    public virtual Guid? LastModifierId { get; set; }
}

[Serializable]
public abstract class AuditedEntity<TKey> : CreationAuditedEntity<TKey>, IAuditedObject
{
    public virtual DateTime? LastModificationTime { get; set; }
    public virtual Guid? LastModifierId { get; set; }

    protected AuditedEntity() { }
    protected AuditedEntity(TKey id) : base(id) { }
}

[Serializable]
public abstract class AuditedEntity<TKey, TUser> : AuditedEntity<TKey>, IAuditedObject<TUser>
{
    public virtual TUser Creator { get; set; } = default!;
    public virtual TUser LastModifier { get; set; } = default!;

    protected AuditedEntity() { }
    protected AuditedEntity(TKey id) : base(id) { }
}

[Serializable]
public abstract class FullAuditedEntity : AuditedEntity, IFullAuditedObject
{
    public virtual bool IsDeleted { get; set; }
    public virtual Guid? DeleterId { get; set; }
    public virtual DateTime? DeletionTime { get; set; }
}

[Serializable]
public abstract class FullAuditedEntity<TKey> : AuditedEntity<TKey>, IFullAuditedObject
{
    public virtual bool IsDeleted { get; set; }
    public virtual Guid? DeleterId { get; set; }
    public virtual DateTime? DeletionTime { get; set; }

    protected FullAuditedEntity() { }
    protected FullAuditedEntity(TKey id) : base(id) { }
}

[Serializable]
public abstract class FullAuditedEntity<TKey, TUser> : FullAuditedEntity<TKey>, IFullAuditedObject<TUser>
{
    public virtual TUser Creator { get; set; } = default!;
    public virtual TUser LastModifier { get; set; } = default!;
    public virtual TUser Deleter { get; set; } = default!;

    protected FullAuditedEntity() { }
    protected FullAuditedEntity(TKey id) : base(id) { }
}
