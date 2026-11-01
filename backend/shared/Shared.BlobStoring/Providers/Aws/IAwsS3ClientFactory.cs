using Amazon.S3;

namespace Shared.BlobStoring.Providers.Aws;

public interface IAwsS3ClientFactory
{
    IAmazonS3 GetClient(AwsBlobProviderOptions options);
}
