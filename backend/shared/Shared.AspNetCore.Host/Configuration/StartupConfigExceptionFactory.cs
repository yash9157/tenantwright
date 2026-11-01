using System;

namespace Shared.AspNetCore.Host.Configuration;

public static class StartupConfigExceptionFactory
{
    public static InvalidOperationException InvalidConfiguration(
        string service,
        string key,
        string rule,
        string fixHint)
    {
        return new InvalidOperationException(
            "Startup configuration validation failed." + Environment.NewLine +
            $"Service: {service}" + Environment.NewLine +
            $"Key: {key}" + Environment.NewLine +
            $"Rule: {rule}" + Environment.NewLine +
            $"Fix: {fixHint}");
    }
}
