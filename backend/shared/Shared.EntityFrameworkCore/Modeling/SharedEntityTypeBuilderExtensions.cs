using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Shared.Domain;
using Shared.Domain.Entities;
using Shared.EntityFrameworkCore.ValueComparers;
using Shared.EntityFrameworkCore.ValueConverters;
using Shared.MultiTenancy;

namespace Shared.EntityFrameworkCore.Modeling;

public static class SharedEntityTypeBuilderExtensions
{
    public static void ConfigureByConvention(this EntityTypeBuilder b)
    {
        b.TryConfigureConcurrencyStamp();
        b.TryConfigureExtraProperties();
        b.TryConfigureSoftDelete();
        b.TryConfigureCreationTime();
        b.TryConfigureLastModificationTime();
        b.TryConfigureDeletionTime();
        b.TryConfigureMultiTenant();
    }

    public static void ConfigureConcurrencyStamp<T>(this EntityTypeBuilder<T> b)
        where T : class, IHasConcurrencyStamp
    {
        b.TryConfigureConcurrencyStamp();
    }

    public static void TryConfigureConcurrencyStamp(this EntityTypeBuilder b)
    {
        if (typeof(IHasConcurrencyStamp).IsAssignableFrom(b.Metadata.ClrType))
        {
            b.Property(nameof(IHasConcurrencyStamp.ConcurrencyStamp))
                .IsConcurrencyToken()
                .HasMaxLength(ConcurrencyStampConsts.MaxLength)
                .HasColumnName(nameof(IHasConcurrencyStamp.ConcurrencyStamp));
        }
    }

    public static void TryConfigureExtraProperties(this EntityTypeBuilder b)
    {
        if (typeof(IHasExtraProperties).IsAssignableFrom(b.Metadata.ClrType))
        {
            var property = b.Property<ExtraPropertyDictionary>(nameof(IHasExtraProperties.ExtraProperties))
                .HasConversion<ExtraPropertyDictionaryValueConverter>()
                .HasColumnName(nameof(IHasExtraProperties.ExtraProperties));

            property.Metadata.SetValueComparer(new ExtraPropertyDictionaryValueComparer());
        }
    }

    public static void ConfigureSoftDelete<T>(this EntityTypeBuilder<T> b)
        where T : class, ISoftDelete
    {
        b.TryConfigureSoftDelete();
    }

    public static void TryConfigureSoftDelete(this EntityTypeBuilder b)
    {
        if (typeof(ISoftDelete).IsAssignableFrom(b.Metadata.ClrType))
        {
            b.Property(nameof(ISoftDelete.IsDeleted))
                .IsRequired()
                .HasDefaultValue(false)
                .HasColumnName(nameof(ISoftDelete.IsDeleted));
        }
    }

    public static void ConfigureDeletionTime<T>(this EntityTypeBuilder<T> b)
        where T : class, IHasDeletionTime
    {
        b.TryConfigureDeletionTime();
    }

    public static void TryConfigureDeletionTime(this EntityTypeBuilder b)
    {
        if (typeof(IHasDeletionTime).IsAssignableFrom(b.Metadata.ClrType))
        {
            b.TryConfigureSoftDelete();

            b.Property(nameof(IHasDeletionTime.DeletionTime))
                .IsRequired(false)
                .HasColumnName(nameof(IHasDeletionTime.DeletionTime));
        }
    }

    public static void ConfigureCreationTime<T>(this EntityTypeBuilder<T> b)
        where T : class, IHasCreationTime
    {
        b.TryConfigureCreationTime();
    }

    public static void TryConfigureCreationTime(this EntityTypeBuilder b)
    {
        if (typeof(IHasCreationTime).IsAssignableFrom(b.Metadata.ClrType))
        {
            b.Property(nameof(IHasCreationTime.CreationTime))
                .IsRequired()
                .HasColumnName(nameof(IHasCreationTime.CreationTime));
        }
    }

    public static void ConfigureLastModificationTime<T>(this EntityTypeBuilder<T> b)
        where T : class, IHasModificationTime
    {
        b.TryConfigureLastModificationTime();
    }

    public static void TryConfigureLastModificationTime(this EntityTypeBuilder b)
    {
        if (typeof(IHasModificationTime).IsAssignableFrom(b.Metadata.ClrType))
        {
            b.Property(nameof(IHasModificationTime.LastModificationTime))
                .IsRequired(false)
                .HasColumnName(nameof(IHasModificationTime.LastModificationTime));
        }
    }

    public static void ConfigureMultiTenant<T>(this EntityTypeBuilder<T> b)
        where T : class, IMultiTenant
    {
        b.TryConfigureMultiTenant();
    }

    public static void TryConfigureMultiTenant(this EntityTypeBuilder b)
    {
        if (typeof(IMultiTenant).IsAssignableFrom(b.Metadata.ClrType))
        {
            b.Property(nameof(IMultiTenant.TenantId))
                .IsRequired(false)
                .HasColumnName(nameof(IMultiTenant.TenantId));
        }
    }
}
