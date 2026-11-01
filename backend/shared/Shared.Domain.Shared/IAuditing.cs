using System;

namespace Shared.Domain;

public interface IHasCreationTime
{
    DateTime CreationTime { get; set; }
}

public interface ICreationAuditedObject : IHasCreationTime
{
    Guid? CreatorId { get; set; }
}

public interface ICreationAuditedObject<TUser> : ICreationAuditedObject
{
    TUser Creator { get; set; }
}

public interface IHasModificationTime
{
    DateTime? LastModificationTime { get; set; }
}

public interface IModificationAuditedObject : IHasModificationTime
{
    Guid? LastModifierId { get; set; }
}

public interface IModificationAuditedObject<TUser> : IModificationAuditedObject
{
    TUser LastModifier { get; set; }
}

public interface IHasDeletionTime : ISoftDelete
{
    DateTime? DeletionTime { get; set; }
}

public interface IDeletionAuditedObject : IHasDeletionTime
{
    Guid? DeleterId { get; set; }
}

public interface IDeletionAuditedObject<TUser> : IDeletionAuditedObject
{
    TUser Deleter { get; set; }
}

public interface IAuditedObject : ICreationAuditedObject, IModificationAuditedObject
{
}

public interface IAuditedObject<TUser> : IAuditedObject, ICreationAuditedObject<TUser>, IModificationAuditedObject<TUser>
{
}

public interface IFullAuditedObject : IAuditedObject, IDeletionAuditedObject
{
}

public interface IFullAuditedObject<TUser> : IFullAuditedObject, IAuditedObject<TUser>, IDeletionAuditedObject<TUser>
{
}
