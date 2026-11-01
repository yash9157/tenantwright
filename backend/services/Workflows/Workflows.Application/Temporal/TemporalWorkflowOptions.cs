using System.ComponentModel.DataAnnotations;

namespace Workflows.Application.Temporal;

public sealed class TemporalWorkflowOptions
{
    public const string SectionName = "Temporal";
    public const string DefaultWorkflowTaskQueue = "tenantwright-workflows-orchestration-v1";
    public const string DefaultActivityTaskQueue = "tenantwright-workflows-activities-v1";

    public bool Enabled { get; set; }

    /// <summary>
    /// Starts Temporal task-queue pollers only after this service owns at least
    /// one concrete workflow or activity definition. The client configuration
    /// remains validated while the worker is intentionally disabled.
    /// </summary>
    public bool WorkerEnabled { get; set; }

    [Required, MinLength(3)]
    public string TargetHost { get; set; } = "localhost:7233";

    [Required, MinLength(1)]
    public string Namespace { get; set; } = "tenantwright";

    [Required, MinLength(3)]
    public string WorkflowTaskQueue { get; set; } = DefaultWorkflowTaskQueue;

    [Required, MinLength(3)]
    public string ActivityTaskQueue { get; set; } = DefaultActivityTaskQueue;

    public bool UseTls { get; set; }

    public string TlsDomain { get; set; } = string.Empty;

    public string ServerRootCaCertificatePath { get; set; } = string.Empty;

    public string ClientCertificatePath { get; set; } = string.Empty;

    public string ClientPrivateKeyPath { get; set; } = string.Empty;

    public string ApiKeyPath { get; set; } = string.Empty;

    [Range(1, 1000)]
    public int MaxConcurrentWorkflowTasks { get; set; } = 100;

    [Range(1, 1000)]
    public int MaxConcurrentActivities { get; set; } = 100;

    public TimeSpan GracefulShutdownTimeout { get; set; } = TimeSpan.FromSeconds(30);
}
