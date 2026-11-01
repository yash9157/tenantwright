using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Sas;
using Microsoft.Extensions.Logging;
using System.IO;

namespace Shared.BlobStoring.Providers.Azure;

public sealed class AzureBlobProvider : IBlobProvider
{
    private readonly IAzureBlobServiceClientFactory _blobServiceClientFactory;
    private readonly IBlobNormalizeNamingService _blobNormalizeNamingService;
    private readonly ILogger<AzureBlobProvider> _logger;

    public AzureBlobProvider(
        IAzureBlobServiceClientFactory blobServiceClientFactory,
        IBlobNormalizeNamingService blobNormalizeNamingService,
        ILogger<AzureBlobProvider> logger)
    {
        _blobServiceClientFactory = blobServiceClientFactory;
        _blobNormalizeNamingService = blobNormalizeNamingService;
        _logger = logger;
    }

    public async Task SaveAsync(BlobProviderSaveArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var containerClient = await GetBlobContainerClientAsync(args, ensureExists: true);
        var blobClient = containerClient.GetBlobClient(args.BlobName);

        try
        {
            if (args.OverrideExisting)
            {
                await blobClient.UploadAsync(args.BlobStream, overwrite: true, cancellationToken: args.CancellationToken);
                return;
            }

            await blobClient.UploadAsync(
                args.BlobStream,
                new BlobUploadOptions
                {
                    Conditions = new BlobRequestConditions
                    {
                        IfNoneMatch = ETag.All
                    }
                },
                args.CancellationToken);
        }
        catch (RequestFailedException ex) when (ex.Status == 409 || ex.Status == 412)
        {
            throw new BlobAlreadyExistsException(
                $"Blob '{args.BlobName}' already exists in container '{args.ContainerName}'.");
        }
    }

    public async Task<bool> DeleteAsync(BlobProviderDeleteArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var containerClient = await GetBlobContainerClientAsync(args, ensureExists: false);
        var blobClient = containerClient.GetBlobClient(args.BlobName);
        var response = await blobClient.DeleteIfExistsAsync(cancellationToken: args.CancellationToken);
        return response.Value;
    }

    public async Task<bool> ExistsAsync(BlobProviderExistsArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var containerClient = await GetBlobContainerClientAsync(args, ensureExists: false);
        var blobClient = containerClient.GetBlobClient(args.BlobName);
        var response = await blobClient.ExistsAsync(args.CancellationToken);
        return response.Value;
    }

    public async Task<Stream?> GetOrNullAsync(BlobProviderGetArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var containerClient = await GetBlobContainerClientAsync(args, ensureExists: false);
        var blobClient = containerClient.GetBlobClient(args.BlobName);

        try
        {
            var response = await blobClient.DownloadStreamingAsync(cancellationToken: args.CancellationToken);
            return response.Value.Content;
        }
        catch (RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async Task<Uri> GetReadUrlAsync(BlobProviderReadUrlArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var containerClient = await GetBlobContainerClientAsync(args, ensureExists: false);
        var blobClient = containerClient.GetBlobClient(args.BlobName);

        if (!blobClient.CanGenerateSasUri)
        {
            throw new BlobProviderNotSupportedException(
                "Azure blob provider cannot generate signed URLs with current credentials.");
        }

        if (args.Duration <= TimeSpan.Zero || args.Duration > TimeSpan.FromDays(7))
        {
            throw new BlobConfigurationException("Azure blob read URL duration must be greater than zero and no more than seven days.");
        }

        var sasBuilder = new BlobSasBuilder
        {
            BlobContainerName = containerClient.Name,
            BlobName = args.BlobName,
            Resource = "b",
            ExpiresOn = DateTimeOffset.UtcNow.Add(args.Duration)
        };
        sasBuilder.SetPermissions(BlobSasPermissions.Read);

        var signedUri = blobClient.GenerateSasUri(sasBuilder);

        _logger.LogDebug(
            "Generated Azure SAS URL for container {Container} with provider override container {ProviderContainer}.",
            args.ContainerName,
            containerClient.Name);

        return signedUri;
    }

    private async Task<BlobContainerClient> GetBlobContainerClientAsync(
        BlobProviderArgs args,
        bool ensureExists)
    {
        var options = args.Configuration.GetAzureConfiguration();
        var serviceClient = _blobServiceClientFactory.GetClient(options);
        var containerName = GetContainerName(args, options);
        var containerClient = serviceClient.GetBlobContainerClient(containerName);

        if (ensureExists && options.CreateContainerIfNotExists)
        {
            await containerClient.CreateIfNotExistsAsync(cancellationToken: args.CancellationToken);
        }

        return containerClient;
    }

    private string GetContainerName(BlobProviderArgs args, AzureBlobProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ContainerName))
        {
            return args.ContainerName;
        }

        return _blobNormalizeNamingService.NormalizeNaming(args.Configuration, options.ContainerName, null).ContainerName
               ?? args.ContainerName;
    }
}
