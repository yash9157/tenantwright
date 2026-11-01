using Amazon.S3;
using Amazon.S3.Model;
using Amazon.S3.Util;
using Microsoft.Extensions.Logging;
using System.IO;
using System.Net;

namespace Shared.BlobStoring.Providers.Aws;

public sealed class AwsBlobProvider : IBlobProvider
{
    private readonly IAwsS3ClientFactory _clientFactory;
    private readonly IBlobNormalizeNamingService _blobNormalizeNamingService;
    private readonly ILogger<AwsBlobProvider> _logger;

    public AwsBlobProvider(
        IAwsS3ClientFactory clientFactory,
        IBlobNormalizeNamingService blobNormalizeNamingService,
        ILogger<AwsBlobProvider> logger)
    {
        _clientFactory = clientFactory;
        _blobNormalizeNamingService = blobNormalizeNamingService;
        _logger = logger;
    }

    public async Task SaveAsync(BlobProviderSaveArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = args.Configuration.GetAwsConfiguration();
        var bucketName = GetBucketName(args, options);
        var client = _clientFactory.GetClient(options);

        if (options.CreateContainerIfNotExists)
        {
            await EnsureBucketExistsAsync(client, bucketName, args.CancellationToken);
        }

        var request = new PutObjectRequest
        {
            BucketName = bucketName,
            Key = args.BlobName,
            InputStream = args.BlobStream
        };

        if (!args.OverrideExisting)
        {
            request.Headers["If-None-Match"] = "*";
        }

        try
        {
            await client.PutObjectAsync(request, args.CancellationToken);
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode == HttpStatusCode.PreconditionFailed ||
            ex.StatusCode == HttpStatusCode.Conflict)
        {
            throw new BlobAlreadyExistsException(
                $"Blob '{args.BlobName}' already exists in container '{args.ContainerName}'.");
        }
    }

    public async Task<bool> DeleteAsync(BlobProviderDeleteArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = args.Configuration.GetAwsConfiguration();
        var bucketName = GetBucketName(args, options);
        var client = _clientFactory.GetClient(options);

        var exists = await ExistsAsync(
            new BlobProviderExistsArgs(
                args.ContainerName,
                args.Configuration,
                args.BlobName,
                args.CancellationToken));

        if (!exists)
        {
            return false;
        }

        await client.DeleteObjectAsync(
            new DeleteObjectRequest
            {
                BucketName = bucketName,
                Key = args.BlobName
            },
            args.CancellationToken);

        return true;
    }

    public async Task<bool> ExistsAsync(BlobProviderExistsArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = args.Configuration.GetAwsConfiguration();
        var bucketName = GetBucketName(args, options);
        var client = _clientFactory.GetClient(options);

        try
        {
            await client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest
                {
                    BucketName = bucketName,
                    Key = args.BlobName
                },
                args.CancellationToken);
            return true;
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode == HttpStatusCode.NotFound ||
            ex.ErrorCode?.Contains("NotFound", StringComparison.OrdinalIgnoreCase) == true)
        {
            return false;
        }
    }

    public async Task<Stream?> GetOrNullAsync(BlobProviderGetArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        var options = args.Configuration.GetAwsConfiguration();
        var bucketName = GetBucketName(args, options);
        var client = _clientFactory.GetClient(options);

        try
        {
            var response = await client.GetObjectAsync(
                new GetObjectRequest
                {
                    BucketName = bucketName,
                    Key = args.BlobName
                },
                args.CancellationToken);

            // The stream disposes the response when disposed by the caller.
            return response.ResponseStream;
        }
        catch (AmazonS3Exception ex) when (
            ex.StatusCode == HttpStatusCode.NotFound ||
            ex.ErrorCode?.Contains("NotFound", StringComparison.OrdinalIgnoreCase) == true)
        {
            return null;
        }
    }

    public Task<Uri> GetReadUrlAsync(BlobProviderReadUrlArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Duration > TimeSpan.FromDays(7))
        {
            throw new BlobConfigurationException("AWS pre-signed read URL duration cannot exceed 7 days.");
        }

        var options = args.Configuration.GetAwsConfiguration();
        var bucketName = GetBucketName(args, options);
        var client = _clientFactory.GetClient(options);

        var signedUrl = client.GetPreSignedURL(new GetPreSignedUrlRequest
        {
            BucketName = bucketName,
            Key = args.BlobName,
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(args.Duration)
        });

        _logger.LogDebug(
            "Generated AWS pre-signed URL for container {Container} with provider override container {Bucket}.",
            args.ContainerName,
            bucketName);

        return Task.FromResult(new Uri(signedUrl));
    }

    private static async Task EnsureBucketExistsAsync(
        IAmazonS3 client,
        string bucketName,
        CancellationToken cancellationToken)
    {
        if (await AmazonS3Util.DoesS3BucketExistV2Async(client, bucketName))
        {
            return;
        }

        await client.PutBucketAsync(
            new PutBucketRequest
            {
                BucketName = bucketName
            },
            cancellationToken);
    }

    private string GetBucketName(BlobProviderArgs args, AwsBlobProviderOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ContainerName))
        {
            return args.ContainerName;
        }

        return _blobNormalizeNamingService.NormalizeNaming(args.Configuration, options.ContainerName, null).ContainerName
               ?? args.ContainerName;
    }
}
