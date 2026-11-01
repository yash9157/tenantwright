using System;
using System.Collections.Generic;

namespace Shared.EntityFrameworkCore.Migrations;

public sealed class SharedEfCoreAutoMigrationOptions
{
    public List<Type> DbContextTypes { get; } = new();
}
