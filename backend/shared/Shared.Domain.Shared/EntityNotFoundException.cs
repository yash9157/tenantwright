using System;
using System.Runtime.Serialization;

namespace Shared.Domain.Entities;

[Serializable]
public class EntityNotFoundException : SharedException
{
    private const string DefaultMessage = "The requested resource was not found.";

    public Type EntityType { get; set; } = default!;

    public object? Id { get; set; }

    public EntityNotFoundException()
        : base(SharedErrorCodes.ResourceNotFound, DefaultMessage, DefaultMessage, null)
    {
    }

    public EntityNotFoundException(Type entityType)
        : this(entityType, null, null)
    {
    }

    public EntityNotFoundException(Type entityType, object? id)
        : this(entityType, id, null)
    {
    }

    public EntityNotFoundException(Type entityType, object? id, Exception? innerException)
        : base(SharedErrorCodes.ResourceNotFound, DefaultMessage, DefaultMessage, innerException)
    {
        EntityType = entityType;
        Id = id;
    }

    public EntityNotFoundException(string message)
        : base(SharedErrorCodes.ResourceNotFound, message, DefaultMessage, null)
    {
    }

    public EntityNotFoundException(string code, string message)
        : base(code, message, DefaultMessage, null)
    {
    }

    public EntityNotFoundException(string message, Exception innerException)
        : base(SharedErrorCodes.ResourceNotFound, message, DefaultMessage, innerException)
    {
    }

    public EntityNotFoundException(string code, string message, Exception innerException)
        : base(code, message, DefaultMessage, innerException)
    {
    }

    [Obsolete("Formatter-based serialization is obsolete.")]
    public EntityNotFoundException(SerializationInfo serializationInfo, StreamingContext context)
        : base(serializationInfo, context)
    {
    }
}

[Serializable]
public class EntityNotFoundException<TEntity> : EntityNotFoundException
{
    public EntityNotFoundException()
        : base(typeof(TEntity))
    {
    }

    public EntityNotFoundException(object? id)
        : base(typeof(TEntity), id)
    {
    }

    public EntityNotFoundException(object? id, Exception? innerException)
        : base(typeof(TEntity), id, innerException)
    {
    }
}
