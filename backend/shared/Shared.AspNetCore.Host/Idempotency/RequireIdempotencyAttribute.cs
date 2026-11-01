namespace Shared.AspNetCore.Host.Idempotency;

[AttributeUsage(AttributeTargets.Method | AttributeTargets.Class, AllowMultiple = false, Inherited = true)]
public sealed class RequireIdempotencyAttribute : Attribute
{
}
