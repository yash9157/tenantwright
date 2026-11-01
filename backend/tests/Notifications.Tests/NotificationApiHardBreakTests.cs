using System.Reflection;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Notifications.Controllers.Notifications;
using Xunit;

namespace Notifications.Tests;

public class NotificationApiHardBreakTests
{
    [Fact]
    public void Notifications_api_must_not_expose_send_endpoint()
    {
        var controllerAssembly = typeof(NotificationsController).Assembly;
        var forbiddenRoutes = controllerAssembly
            .GetTypes()
            .Where(type =>
                !type.IsAbstract &&
                typeof(ControllerBase).IsAssignableFrom(type) &&
                type.GetCustomAttribute<ApiControllerAttribute>() != null)
            .SelectMany(type => GetHttpMethodDescriptors(type)
                .Where(route =>
                    route.HttpMethods.Contains("POST", StringComparer.OrdinalIgnoreCase) &&
                    string.Equals(route.Route, "api/notifications/send", StringComparison.OrdinalIgnoreCase))
                .Select(route => $"{type.FullName}.{route.MethodName}:{route.Route}"))
            .ToList();

        Assert.True(
            forbiddenRoutes.Count == 0,
            "Hard-break violation: '/api/notifications/send' must not exist. Found: " + string.Join(", ", forbiddenRoutes));
    }

    private static IEnumerable<HttpRouteDescriptor> GetHttpMethodDescriptors(Type controllerType)
    {
        var classRoute = controllerType.GetCustomAttribute<RouteAttribute>()?.Template ?? string.Empty;

        foreach (var method in controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        {
            var httpMethodAttributes = method.GetCustomAttributes<HttpMethodAttribute>(inherit: true).ToList();
            if (httpMethodAttributes.Count == 0)
            {
                continue;
            }

            foreach (var attribute in httpMethodAttributes)
            {
                yield return new HttpRouteDescriptor
                {
                    MethodName = method.Name,
                    HttpMethods = attribute.HttpMethods.ToArray(),
                    Route = NormalizeRoute(classRoute, attribute.Template)
                };
            }
        }
    }

    private static string NormalizeRoute(string classRoute, string? methodRoute)
    {
        var parts = new[] { classRoute ?? string.Empty, methodRoute ?? string.Empty }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.Trim('/'));

        return string.Join("/", parts);
    }

    private sealed class HttpRouteDescriptor
    {
        public required string MethodName { get; init; }

        public required IReadOnlyCollection<string> HttpMethods { get; init; }

        public required string Route { get; init; }
    }
}
