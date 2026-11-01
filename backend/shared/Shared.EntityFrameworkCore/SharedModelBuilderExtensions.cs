using Microsoft.EntityFrameworkCore;
using Shared.EntityFrameworkCore;
using Shared.MultiTenancy;

namespace Microsoft.EntityFrameworkCore;

public static class SharedModelBuilderExtensions
{
    private const string ModelDatabaseProviderAnnotationKey = "_Shared_DatabaseProvider";
    private const string ModelMultiTenancySideAnnotationKey = "_Shared_MultiTenancySide";

    public static void SetMultiTenancySide(
        this ModelBuilder modelBuilder,
        MultiTenancySides side)
    {
        modelBuilder.Model.SetAnnotation(ModelMultiTenancySideAnnotationKey, side);
    }

    public static MultiTenancySides GetMultiTenancySide(this ModelBuilder modelBuilder)
    {
        var value = modelBuilder.Model[ModelMultiTenancySideAnnotationKey];
        if (value == null)
        {
            return MultiTenancySides.Both;
        }

        return (MultiTenancySides)value;
    }

    public static void SetDatabaseProvider(
        this ModelBuilder modelBuilder,
        EfCoreDatabaseProvider databaseProvider)
    {
        modelBuilder.Model.SetAnnotation(ModelDatabaseProviderAnnotationKey, databaseProvider);
    }

    public static EfCoreDatabaseProvider? GetDatabaseProvider(
        this ModelBuilder modelBuilder
    )
    {
        return (EfCoreDatabaseProvider?)modelBuilder.Model[ModelDatabaseProviderAnnotationKey];
    }
}