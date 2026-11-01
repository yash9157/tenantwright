using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using System.Collections.Concurrent;
using System.Text;

namespace Shared.BlobStoring.Providers.Aws;

public sealed class AwsS3ClientFactory : IAwsS3ClientFactory, IDisposable
{
    private readonly ConcurrentDictionary<string, IAmazonS3> _clients = new(StringComparer.Ordinal);

    public IAmazonS3 GetClient(AwsBlobProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var key = BuildClientKey(options);
        return _clients.GetOrAdd(key, _ => CreateClient(options));
    }

    public void Dispose()
    {
        foreach (var client in _clients.Values)
        {
            client.Dispose();
        }

        _clients.Clear();
    }

    private static AmazonS3Client CreateClient(AwsBlobProviderOptions options)
    {
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle,
            UseHttp = !options.UseSsl
        };

        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            config.ServiceURL = options.ServiceUrl;
        }

        if (!string.IsNullOrWhiteSpace(options.Region))
        {
            config.RegionEndpoint = RegionEndpoint.GetBySystemName(options.Region);
        }

        if (!string.IsNullOrWhiteSpace(options.AccessKeyId) &&
            !string.IsNullOrWhiteSpace(options.SecretAccessKey))
        {
            var credentials = new BasicAWSCredentials(options.AccessKeyId, options.SecretAccessKey);
            return new AmazonS3Client(credentials, config);
        }

        if (options.UseDefaultCredentials)
        {
            return new AmazonS3Client(config);
        }

        throw new BlobConfigurationException(
            "AWS blob provider requires AccessKeyId/SecretAccessKey or UseDefaultCredentials=true.");
    }

    private static string BuildClientKey(AwsBlobProviderOptions options)
    {
        var builder = new StringBuilder();
        builder.Append(options.Region);
        builder.Append('|');
        builder.Append(options.ServiceUrl);
        builder.Append('|');
        builder.Append(options.ForcePathStyle ? "1" : "0");
        builder.Append('|');
        builder.Append(options.UseSsl ? "1" : "0");
        builder.Append('|');
        builder.Append(options.AccessKeyId);
        builder.Append('|');
        builder.Append(options.UseDefaultCredentials ? "1" : "0");
        return builder.ToString();
    }
}
