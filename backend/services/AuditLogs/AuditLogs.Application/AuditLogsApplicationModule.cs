using AuditLogs.Application.Contracts;
using AuditLogs.Application.Auditing;
using AuditLogs.Domain;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Shared.BackgroundTasks;
using Shared.BackgroundTasks.DependencyInjection;
using Shared.BlobStoring;
using Shared.BlobStoring.Providers.Aws;
using Shared.BlobStoring.Providers.Azure;
using Shared.BlobStoring.Providers.FileSystem;
using Shared.Caching;
using Shared.DistributedLocking;
using Shared.Modularity.Context;
using Shared.Modularity.Modules;

namespace AuditLogs.Application;

[DependsOn(typeof(AuditLogsDomainModule))]
[DependsOn(typeof(AuditLogsApplicationContractsModule))]
[DependsOn(typeof(BlobStoringModule))]
[DependsOn(typeof(CachingModule))]
[DependsOn(typeof(SharedBackgroundTasksModule))]
[DependsOn(typeof(DistributedLockingModule))]
public class AuditLogsApplicationModule : BaseModule
{
    public override void ConfigureServices(ServiceConfigurationContext context)
    {
        context.Services.Configure<AuditLogSecurityOptions>(
            context.Configuration.GetSection(AuditLogSecurityOptions.SectionName));
        context.Services.PostConfigure<AuditLogSecurityOptions>(AuditLogSecurityOptionsPostConfigure.Normalize);
        context.Services.Configure<AuditLogArchiveOptions>(
            context.Configuration.GetSection(AuditLogArchiveOptions.SectionName));
        context.Services.PostConfigure<AuditLogArchiveOptions>(AuditLogArchiveOptionsPostConfigure.Normalize);
        context.Services.PostConfigure<BlobStoringOptions>(options =>
        {
            var archiveOptions = context.Configuration
                .GetSection(AuditLogArchiveOptions.SectionName)
                .Get<AuditLogArchiveOptions>() ?? new AuditLogArchiveOptions();
            AuditLogArchiveOptionsPostConfigure.Normalize(archiveOptions);

            ConfigureBlobContainer(options.Containers.GetConfiguration<DefaultContainer>(), archiveOptions, fallbackContainerName: "auditlogs-default");
            ConfigureBlobContainer(options.Containers.GetConfiguration<AuditLogArchiveBlobContainer>(), archiveOptions, fallbackContainerName: AuditLogArchiveBlobContainer.Name);
        });

        context.Services.AddBackgroundJob<AuditLogExportBackgroundJob, AuditLogExportJobArgs>();
        context.Services.AddBackgroundJob<AuditLogArchiveMaintenanceBackgroundJob, AuditLogArchiveMaintenanceJobArgs>();
        context.Services.AddBackgroundJob<AuditLogSealVerificationBackgroundJob, AuditLogSealVerificationJobArgs>();
    }

    private static void ConfigureBlobContainer(
        BlobContainerConfiguration container,
        AuditLogArchiveOptions archiveOptions,
        string fallbackContainerName)
    {
        container.IsMultiTenant = false;

        var provider = archiveOptions.Blob.Provider.Trim().ToLowerInvariant();
        switch (provider)
        {
            case "azure":
                container.UseAzure(azure =>
                {
                    azure.ConnectionString = string.IsNullOrWhiteSpace(archiveOptions.Blob.Azure.ConnectionString)
                        ? throw new BlobConfigurationException("AuditLogging:Archive:Blob:Azure:ConnectionString is required for Azure provider.")
                        : archiveOptions.Blob.Azure.ConnectionString.Trim();
                    azure.ContainerName = NormalizeContainerName(
                        archiveOptions.Blob.Azure.ContainerName,
                        archiveOptions.Blob.ContainerName,
                        fallbackContainerName);
                    azure.CreateContainerIfNotExists = archiveOptions.Blob.CreateContainerIfNotExists;
                });
                return;
            case "aws":
            case "s3":
                container.UseAws(aws =>
                {
                    aws.Region = string.IsNullOrWhiteSpace(archiveOptions.Blob.Aws.Region)
                        ? throw new BlobConfigurationException("AuditLogging:Archive:Blob:Aws:Region is required for AWS provider.")
                        : archiveOptions.Blob.Aws.Region.Trim();
                    aws.AccessKeyId = NormalizeOptional(archiveOptions.Blob.Aws.AccessKeyId);
                    aws.SecretAccessKey = NormalizeOptional(archiveOptions.Blob.Aws.SecretAccessKey);
                    aws.ContainerName = NormalizeContainerName(
                        archiveOptions.Blob.Aws.ContainerName,
                        archiveOptions.Blob.ContainerName,
                        fallbackContainerName);
                    aws.CreateContainerIfNotExists = archiveOptions.Blob.CreateContainerIfNotExists;
                });
                return;
            case "filesystem":
            default:
                container.UseFileSystem(fileSystem =>
                {
                    fileSystem.BasePath = string.IsNullOrWhiteSpace(archiveOptions.Blob.FileSystem.BasePath)
                        ? Path.Combine(AppContext.BaseDirectory, "blobs", "audit-archive")
                        : archiveOptions.Blob.FileSystem.BasePath!;
                    fileSystem.AppendContainerNameToBasePath = archiveOptions.Blob.FileSystem.AppendContainerNameToBasePath;
                    fileSystem.RetryCount = archiveOptions.Blob.FileSystem.RetryCount;
                });
                return;
        }
    }

    private static string? NormalizeOptional(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string NormalizeContainerName(string? providerSpecific, string? general, string fallback)
    {
        if (!string.IsNullOrWhiteSpace(providerSpecific))
        {
            return providerSpecific.Trim();
        }

        if (!string.IsNullOrWhiteSpace(general))
        {
            return general.Trim();
        }

        return fallback;
    }
}
