namespace Shared.EntityFrameworkCore;

internal enum EntityChangeKind : byte
{
    Created = 0,
    Updated = 1,
    Deleted = 2
}
