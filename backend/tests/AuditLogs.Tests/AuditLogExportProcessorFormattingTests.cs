using System.IO.Compression;
using System.Text;
using AuditLogs.Application.Auditing;
using Xunit;

namespace AuditLogs.Tests;

public class AuditLogExportProcessorFormattingTests
{
    [Fact]
    public void ToCsvLine_should_escape_commas_quotes_and_newlines()
    {
        var row = new AuditLogExportRow
        {
            Id = 1,
            EventId = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            TenantId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ExecutionTime = DateTime.SpecifyKind(new DateTime(2026, 2, 16, 10, 0, 0), DateTimeKind.Utc),
            ExecutionDuration = 12,
            ApplicationName = "identity-service",
            Category = "http.request",
            UserId = "user-1",
            UserName = "Jane \"Doe\"",
            HttpMethod = "POST",
            Url = "/api/items,with,comma",
            HttpStatusCode = 200,
            CorrelationId = "corr-1",
            TraceId = "trace-1",
            ClientId = "client-1"
        };

        var line = AuditLogExportProcessor.ToCsvLine(row);

        Assert.Contains("\"Jane \"\"Doe\"\"\"", line, StringComparison.Ordinal);
        Assert.Contains("\"/api/items,with,comma\"", line, StringComparison.Ordinal);
    }

    [Fact]
    public void Compress_should_round_trip_bytes()
    {
        var payload = Encoding.UTF8.GetBytes("line-1\nline-2\nline-3");

        var compressed = AuditLogExportProcessor.Compress(payload);
        var decompressed = Decompress(compressed);

        Assert.Equal(payload, decompressed);
    }

    private static byte[] Decompress(byte[] compressed)
    {
        using var input = new MemoryStream(compressed);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream();
        gzip.CopyTo(output);
        return output.ToArray();
    }
}
