using Microsoft.Extensions.Options;
using Temporalio.Client;

namespace Workflows.Application.Temporal;

public interface ITenantWrightTemporalClientProvider
{
    Task<ITemporalClient> GetAsync(CancellationToken cancellationToken = default);
}

public sealed class TenantWrightTemporalClientProvider(IOptions<TemporalWorkflowOptions> options)
    : ITenantWrightTemporalClientProvider, IDisposable
{
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private ITemporalClient? _client;

    public async Task<ITemporalClient> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_client is not null)
            return _client;

        await _connectionGate.WaitAsync(cancellationToken);
        try
        {
            if (_client is not null)
                return _client;

            var value = options.Value;
            if (!value.Enabled)
                throw new InvalidOperationException("Temporal workflow execution is disabled.");

            var connectOptions = new TemporalClientConnectOptions(value.TargetHost)
            {
                Namespace = value.Namespace,
                Tls = value.UseTls ? await CreateTlsOptionsAsync(value, cancellationToken) : null,
                ApiKey = await ReadOptionalSecretAsync(value.ApiKeyPath, cancellationToken)
            };
            _client = await TemporalClient.ConnectAsync(connectOptions);
            return _client;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private static async Task<TlsOptions> CreateTlsOptionsAsync(
        TemporalWorkflowOptions options,
        CancellationToken cancellationToken) =>
        new()
        {
            Domain = NullIfWhiteSpace(options.TlsDomain),
            ServerRootCACert = await ReadOptionalBytesAsync(options.ServerRootCaCertificatePath, cancellationToken),
            ClientCert = await ReadOptionalBytesAsync(options.ClientCertificatePath, cancellationToken),
            ClientPrivateKey = await ReadOptionalBytesAsync(options.ClientPrivateKeyPath, cancellationToken)
        };

    private static async Task<string?> ReadOptionalSecretAsync(string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        var value = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("The configured Temporal API-key file is empty.")
            : value;
    }

    private static Task<byte[]?> ReadOptionalBytesAsync(string path, CancellationToken cancellationToken) =>
        string.IsNullOrWhiteSpace(path)
            ? Task.FromResult<byte[]?>(null)
            : ReadBytesAsync(path, cancellationToken);

    private static async Task<byte[]?> ReadBytesAsync(string path, CancellationToken cancellationToken) =>
        await File.ReadAllBytesAsync(path, cancellationToken);

    private static string? NullIfWhiteSpace(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    public void Dispose()
    {
        _connectionGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
