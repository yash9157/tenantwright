using System.Text.Json;
using Shared;
using System.Security.Claims;
using System.Reflection;
using MailKit.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Caching.Distributed;
using MimeKit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Notifications.Application.Contracts.Delivery;
using Notifications.Application.Contracts.Emailing;
using Notifications.Application.Contracts.Notifications;
using Notifications.Application.Contracts.Presence;
using Notifications.Application.Contracts.Realtime;
using Notifications.Application.Contracts.Replay;
using Notifications.Application.Contracts.ProviderConfig;
using Notifications.Application.Contracts.Providers;
using Notifications.Application.Contracts.Templates;
using Notifications.Application.Notifications;
using Notifications.Application.Delivery;
using Notifications.Application.Presence;
using Notifications.Application.Replay;
using Notifications.Application.Emailing;
using Notifications.Application.ProviderConfig;
using Notifications.Application.Contracts.ProviderConfig.Security;
using Notifications.Application.Providers;
using Notifications.Application.Templates;
using Notifications.Controllers.EmailProviderConfig;
using Notifications.Controllers.NotificationProviders;
using Notifications.Controllers.NotificationReplay;
using Notifications.Controllers.Notifications;
using Notifications.Controllers.NotificationTemplates;
using Notifications.Controllers.Presence;
using Notifications.Domain.Notifications;
using Notifications.Domain.ProviderConfig;
using Notifications.Domain.Presence;
using Notifications.Domain.Shared.Constants;
using Notifications.Domain.Shared.Enums;
using Notifications.Domain.Templates;
using Notifications.EntityFrameworkCore.EntityFrameworkCore;
using Notifications.Presence;
using Shared.AuditLogs.Auditing;
using Shared.Caching.Abstractions;
using Shared.Domain.Entities;
using Shared.Domain.Repositories;
using Shared.EntityFrameworkCore;
using Shared.EventContracts;
using Shared.Modularity.DependencyInjection;
using Shared.MultiTenancy;
using Shared.Permissions.Definitions.Notifications;
using Shared.Security;
using Shared.Uow;
using Shared.Uow.Abstractions;
using Shared.Uow.Events;
using Shared.Uow.Options;
using Xunit;

namespace Notifications.Tests;

public class NotificationAdminConsoleTests
{
    [Fact]
    public async Task Template_import_should_be_idempotent()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Template_import_should_be_idempotent),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}"),
                TestTemplateFile.Create("Tenant.Welcome", "Tenant Welcome", "Tenant", "Tenant/Welcome.tpl", "Welcome {{model.tenantName}}")
            ]);

        await fixture.ImportService.EnsureImportedAsync();
        await fixture.ImportService.EnsureImportedAsync();

        Assert.Equal(2, await fixture.DbContext.NotificationTemplateDefinitions.CountAsync());
        Assert.Equal(2, await fixture.DbContext.NotificationTemplateRevisions.CountAsync());
    }

    [Fact]
    public async Task Template_admin_list_should_import_templates_and_support_filtering()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Template_admin_list_should_import_templates_and_support_filtering),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}"),
                TestTemplateFile.Create("Tenant.Welcome", "Tenant Welcome", "Tenant", "Tenant/Welcome.tpl", "Welcome {{model.tenantName}}")
            ]);

        var page = await fixture.Service.GetListAsync(new GetNotificationTemplatesInput
        {
            Filter = "Password",
            SkipCount = 0,
            MaxResultCount = 10
        });

        var item = Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal("Identity.PasswordReset", item.Name);
        Assert.Equal("Identity", item.Category);

        var detail = await fixture.Service.GetAsync("Tenant.Welcome");
        Assert.Equal("Tenant.Welcome", detail.Name);
        Assert.Equal("Tenant", detail.Category);
        Assert.Equal("Database", detail.SourceType);

        var manage = await fixture.Service.GetManageAsync("Identity.PasswordReset");
        Assert.Equal(1, manage.RevisionCount);
        Assert.Equal(1, manage.PublishedRevisionNumber);
        Assert.Equal("Database", manage.SourceType);

        var preview = await fixture.Service.PreviewAsync(new RenderTemplateRequestDto
        {
            TemplateName = "Identity.PasswordReset",
            Model = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userName"] = "Admin"
            }
        });

        Assert.Equal("<html>Hello Admin</html>", preview.Content);
    }

    [Fact]
    public async Task Saving_draft_should_not_change_runtime_until_publish()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Saving_draft_should_not_change_runtime_until_publish),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        await fixture.Service.SaveDraftAsync(new SaveNotificationTemplateDraftInput
        {
            TemplateName = "Identity.PasswordReset",
            DisplayName = "Password Reset",
            Category = "Identity",
            SubjectTemplate = "Draft subject for {{model.userName}}",
            BodyTemplate = "Updated {{model.userName}}"
        });

        var runtimeBeforePublish = await fixture.Service.PreviewAsync(new RenderTemplateRequestDto
        {
            TemplateName = "Identity.PasswordReset",
            Model = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userName"] = "Admin"
            }
        });
        Assert.Equal("<html>Hello Admin</html>", runtimeBeforePublish.Content);
        Assert.Null(runtimeBeforePublish.RenderedSubject);

        var draftPreview = await fixture.Service.PreviewDraftAsync(new PreviewNotificationTemplateDraftInput
        {
            TemplateName = "Identity.PasswordReset",
            SubjectTemplate = "Draft subject for {{model.userName}}",
            BodyTemplate = "Updated {{model.userName}}",
            Model = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userName"] = "Admin"
            }
        });
        Assert.Equal("<html>Updated Admin</html>", draftPreview.Content);
        Assert.Equal("Draft subject for Admin", draftPreview.RenderedSubject);

        var published = await fixture.Service.PublishDraftAsync(new PublishNotificationTemplateDraftInput
        {
            TemplateName = "Identity.PasswordReset"
        });
        Assert.Equal(2, published.PublishedRevisionNumber);
        Assert.Equal(2, published.RevisionCount);

        var runtimeAfterPublish = await fixture.Service.PreviewAsync(new RenderTemplateRequestDto
        {
            TemplateName = "Identity.PasswordReset",
            Model = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userName"] = "Admin"
            }
        });
        Assert.Equal("<html>Updated Admin</html>", runtimeAfterPublish.Content);
        Assert.Equal("Draft subject for Admin", runtimeAfterPublish.RenderedSubject);
    }

    [Fact]
    public async Task Validate_draft_should_report_variables_missing_samples_and_parse_errors_without_mutating_runtime()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Validate_draft_should_report_variables_missing_samples_and_parse_errors_without_mutating_runtime),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        var validation = await fixture.Service.ValidateDraftAsync(new ValidateNotificationTemplateDraftInput
        {
            TemplateName = "Identity.PasswordReset",
            SubjectTemplate = "Security notice for {{model.userName}}",
            BodyTemplate = "Hello {{model.userName}} from {{model.workspaceName}}",
            Model = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userName"] = "Admin"
            }
        });

        Assert.True(validation.IsValid);
        Assert.True(validation.CanRender);
        Assert.Equal(["workspaceName"], validation.MissingModelKeys);
        Assert.Equal("Admin", validation.SuggestedModel["userName"]);
        Assert.Equal("sample-workspaceName", validation.SuggestedModel["workspaceName"]);
        Assert.Contains(validation.Variables, variable =>
            variable.Source == NotificationTemplateValidationSources.Subject &&
            variable.Expression == "model.userName" &&
            variable.ModelKey == "userName");
        Assert.Contains(validation.Variables, variable =>
            variable.Source == NotificationTemplateValidationSources.Body &&
            variable.Expression == "model.workspaceName" &&
            variable.ModelKey == "workspaceName");

        var runtimeAfterValidation = await fixture.Service.PreviewAsync(new RenderTemplateRequestDto
        {
            TemplateName = "Identity.PasswordReset",
            Model = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userName"] = "Admin"
            }
        });
        Assert.Equal("<html>Hello Admin</html>", runtimeAfterValidation.Content);

        var invalid = await fixture.Service.ValidateDraftAsync(new ValidateNotificationTemplateDraftInput
        {
            TemplateName = "Identity.PasswordReset",
            BodyTemplate = "Broken {{ if model.userName }}",
            Model = new Dictionary<string, string?>(StringComparer.Ordinal)
        });

        Assert.False(invalid.IsValid);
        Assert.False(invalid.CanRender);
        Assert.Contains(invalid.Issues, issue =>
            issue.Severity == NotificationTemplateValidationIssueSeverities.Error &&
            issue.Code == NotificationTemplateValidationIssueCodes.TemplateSyntax &&
            issue.Source == NotificationTemplateValidationSources.Body);
    }

    [Fact]
    public async Task Create_should_add_a_new_blank_template_without_affecting_runtime_until_publish()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Create_should_add_a_new_blank_template_without_affecting_runtime_until_publish),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        var created = await fixture.Service.CreateAsync(new CreateNotificationTemplateInput
        {
            DisplayName = "Custom Welcome",
            Category = "Notifications",
            SubjectTemplate = "Welcome {{model.userName}}",
            BodyTemplate = "Custom body for {{model.userName}}"
        });

        Assert.Equal("Notifications.CustomWelcome", created.Name);
        Assert.Equal("Custom Welcome", created.DisplayName);
        Assert.Equal("Notifications", created.Category);
        Assert.Null(created.PublishedRevisionNumber);
        Assert.Equal(0, created.RevisionCount);
        Assert.Empty(created.Revisions);
        Assert.Null(created.PublishedAtUtc);
        Assert.Equal("Database", created.SourceType);

        var list = await fixture.Service.GetListAsync(new GetNotificationTemplatesInput
        {
            Filter = "Custom Welcome",
            SkipCount = 0,
            MaxResultCount = 10
        });
        var listed = Assert.Single(list.Items);
        Assert.Equal("Notifications.CustomWelcome", listed.Name);

        var manage = await fixture.Service.GetManageAsync("Notifications.CustomWelcome");
        Assert.Equal("Custom body for {{model.userName}}", manage.DraftBodyTemplate);
        Assert.Equal("Welcome {{model.userName}}", manage.DraftSubjectTemplate);

        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.Service.PreviewAsync(new RenderTemplateRequestDto
        {
            TemplateName = "Notifications.CustomWelcome",
            Model = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userName"] = "Admin"
            }
        }));

        var published = await fixture.Service.PublishDraftAsync(new PublishNotificationTemplateDraftInput
        {
            TemplateName = "Notifications.CustomWelcome"
        });
        Assert.Equal(1, published.PublishedRevisionNumber);
        Assert.Equal(1, published.RevisionCount);

        var runtimeAfterPublish = await fixture.Service.PreviewAsync(new RenderTemplateRequestDto
        {
            TemplateName = "Notifications.CustomWelcome",
            Model = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userName"] = "Admin"
            }
        });
        Assert.Equal("<html>Custom body for Admin</html>", runtimeAfterPublish.Content);
        Assert.Equal("Welcome Admin", runtimeAfterPublish.RenderedSubject);
    }

    [Fact]
    public async Task Create_should_fail_closed_for_duplicate_template_name()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Create_should_fail_closed_for_duplicate_template_name),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        await Assert.ThrowsAsync<Shared.SharedConflictException>(() => fixture.Service.CreateAsync(new CreateNotificationTemplateInput
        {
            DisplayName = "Password Reset",
            Category = "Identity",
            SubjectTemplate = "Duplicate {{model.userName}}",
            BodyTemplate = "Duplicate body"
        }));
    }

    [Fact]
    public async Task Template_samples_should_support_crud_and_unique_default_per_template()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Template_samples_should_support_crud_and_unique_default_per_template),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        var first = await fixture.Service.CreateSampleAsync(new CreateNotificationTemplateSampleInput
        {
            TemplateName = "Identity.PasswordReset",
            Name = "Happy path",
            Description = "Normal password reset model",
            Model = new Dictionary<string, string?> { ["userName"] = "Ada" },
            IsDefault = true
        });

        Assert.True(first.IsDefault);
        Assert.Equal("Ada", first.Model["userName"]);

        var second = await fixture.Service.CreateSampleAsync(new CreateNotificationTemplateSampleInput
        {
            TemplateName = "Identity.PasswordReset",
            Name = "Support path",
            Model = new Dictionary<string, string?> { ["userName"] = "Grace" },
            IsDefault = true
        });

        var samples = await fixture.Service.GetSamplesAsync("Identity.PasswordReset");
        Assert.Equal(2, samples.Count);
        Assert.False(samples.Single(sample => sample.Id == first.Id).IsDefault);
        Assert.True(samples.Single(sample => sample.Id == second.Id).IsDefault);

        var updated = await fixture.Service.UpdateSampleAsync(new UpdateNotificationTemplateSampleInput
        {
            Id = first.Id,
            Name = "Happy path updated",
            Description = "Updated description",
            Model = new Dictionary<string, string?> { ["userName"] = "Lin" },
            IsDefault = true
        });

        Assert.True(updated.IsDefault);
        Assert.Equal("Happy path updated", updated.Name);
        Assert.Equal("Lin", updated.Model["userName"]);

        samples = await fixture.Service.GetSamplesAsync("Identity.PasswordReset");
        Assert.True(samples.Single(sample => sample.Id == first.Id).IsDefault);
        Assert.False(samples.Single(sample => sample.Id == second.Id).IsDefault);

        await fixture.Service.DeleteSampleAsync(second.Id);

        samples = await fixture.Service.GetSamplesAsync("Identity.PasswordReset");
        Assert.Single(samples);
        Assert.Equal(first.Id, samples[0].Id);
    }

    [Fact]
    public async Task Template_samples_should_fail_closed_for_duplicates_and_unknown_templates()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Template_samples_should_fail_closed_for_duplicates_and_unknown_templates),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        await fixture.Service.CreateSampleAsync(new CreateNotificationTemplateSampleInput
        {
            TemplateName = "Identity.PasswordReset",
            Name = "Happy path",
            Model = new Dictionary<string, string?> { ["userName"] = "Ada" }
        });

        await Assert.ThrowsAsync<Shared.SharedConflictException>(() => fixture.Service.CreateSampleAsync(new CreateNotificationTemplateSampleInput
        {
            TemplateName = "Identity.PasswordReset",
            Name = "happy path",
            Model = new Dictionary<string, string?> { ["userName"] = "Grace" }
        }));

        await Assert.ThrowsAsync<EntityNotFoundException>(() => fixture.Service.CreateSampleAsync(new CreateNotificationTemplateSampleInput
        {
            TemplateName = "Missing.Template",
            Name = "Missing",
            Model = new Dictionary<string, string?>()
        }));
    }

    [Fact]
    public async Task Template_samples_should_not_mutate_draft_or_published_output()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Template_samples_should_not_mutate_draft_or_published_output),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        var before = await fixture.Service.PreviewAsync(new RenderTemplateRequestDto
        {
            TemplateName = "Identity.PasswordReset",
            Model = new Dictionary<string, string?> { ["userName"] = "Ada" }
        });

        await fixture.Service.CreateSampleAsync(new CreateNotificationTemplateSampleInput
        {
            TemplateName = "Identity.PasswordReset",
            Name = "Grace sample",
            Model = new Dictionary<string, string?> { ["userName"] = "Grace" },
            IsDefault = true
        });

        var after = await fixture.Service.PreviewAsync(new RenderTemplateRequestDto
        {
            TemplateName = "Identity.PasswordReset",
            Model = new Dictionary<string, string?> { ["userName"] = "Ada" }
        });
        var manage = await fixture.Service.GetManageAsync("Identity.PasswordReset");

        Assert.Equal(before.Content, after.Content);
        Assert.Equal("Hello {{model.userName}}", manage.DraftBodyTemplate);
    }

    [Fact]
    public async Task Template_content_blocks_should_support_crud_and_archive_restore()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Template_content_blocks_should_support_crud_and_archive_restore),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        var created = await fixture.Service.CreateContentBlockAsync(new CreateNotificationTemplateContentBlockInput
        {
            Key = "global.signature",
            DisplayName = "Global Signature",
            Category = "Shared",
            Description = "Standard support signature",
            SubjectSnippet = " - TenantWright Support",
            BodySnippet = "Regards, {{model.supportTeam}}"
        });

        Assert.Equal("global.signature", created.Key);
        Assert.Equal("Global Signature", created.DisplayName);
        Assert.Equal("Shared", created.Category);
        Assert.False(created.IsArchived);

        var activePage = await fixture.Service.GetContentBlocksAsync(new GetNotificationTemplateContentBlocksInput
        {
            Filter = "signature",
            ArchiveState = NotificationTemplateArchiveStates.Active,
            SkipCount = 0,
            MaxResultCount = 10
        });
        var active = Assert.Single(activePage.Items);
        Assert.Equal(created.Id, active.Id);

        var updated = await fixture.Service.UpdateContentBlockAsync(new UpdateNotificationTemplateContentBlockInput
        {
            Id = created.Id,
            Key = "global.signature",
            DisplayName = "Global Signature Updated",
            Category = "Shared",
            Description = "Updated support signature",
            SubjectSnippet = " - Updated",
            BodySnippet = "Updated regards"
        });

        Assert.Equal("Global Signature Updated", updated.DisplayName);
        Assert.Equal("Updated regards", updated.BodySnippet);

        var archived = await fixture.Service.ArchiveContentBlockAsync(created.Id);
        Assert.True(archived.IsArchived);
        Assert.NotNull(archived.ArchivedAtUtc);

        activePage = await fixture.Service.GetContentBlocksAsync(new GetNotificationTemplateContentBlocksInput
        {
            ArchiveState = NotificationTemplateArchiveStates.Active,
            SkipCount = 0,
            MaxResultCount = 10
        });
        Assert.Empty(activePage.Items);

        var archivedPage = await fixture.Service.GetContentBlocksAsync(new GetNotificationTemplateContentBlocksInput
        {
            ArchiveState = NotificationTemplateArchiveStates.Archived,
            SkipCount = 0,
            MaxResultCount = 10
        });
        Assert.Single(archivedPage.Items);

        var restored = await fixture.Service.RestoreContentBlockAsync(created.Id);
        Assert.False(restored.IsArchived);
        Assert.Null(restored.ArchivedAtUtc);
    }

    [Fact]
    public async Task Template_content_blocks_should_fail_closed_for_duplicate_keys()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Template_content_blocks_should_fail_closed_for_duplicate_keys),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        await fixture.Service.CreateContentBlockAsync(new CreateNotificationTemplateContentBlockInput
        {
            Key = "global.signature",
            DisplayName = "Global Signature",
            Category = "Shared",
            BodySnippet = "Regards"
        });

        await Assert.ThrowsAsync<Shared.SharedConflictException>(() => fixture.Service.CreateContentBlockAsync(new CreateNotificationTemplateContentBlockInput
        {
            Key = "GLOBAL.SIGNATURE",
            DisplayName = "Duplicate Signature",
            Category = "Shared",
            BodySnippet = "Duplicate"
        }));
    }

    [Fact]
    public async Task Template_content_blocks_should_not_mutate_draft_or_published_output()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Template_content_blocks_should_not_mutate_draft_or_published_output),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        var before = await fixture.Service.PreviewAsync(new RenderTemplateRequestDto
        {
            TemplateName = "Identity.PasswordReset",
            Model = new Dictionary<string, string?> { ["userName"] = "Ada" }
        });

        await fixture.Service.CreateContentBlockAsync(new CreateNotificationTemplateContentBlockInput
        {
            Key = "global.signature",
            DisplayName = "Global Signature",
            Category = "Shared",
            BodySnippet = "Regards, {{model.supportTeam}}"
        });

        var after = await fixture.Service.PreviewAsync(new RenderTemplateRequestDto
        {
            TemplateName = "Identity.PasswordReset",
            Model = new Dictionary<string, string?> { ["userName"] = "Ada" }
        });
        var manage = await fixture.Service.GetManageAsync("Identity.PasswordReset");

        Assert.Equal(before.Content, after.Content);
        Assert.Equal("Hello {{model.userName}}", manage.DraftBodyTemplate);
    }

    [Fact]
    public async Task Rename_should_only_allow_draft_only_database_templates_without_revisions()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Rename_should_only_allow_draft_only_database_templates_without_revisions),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        await fixture.Service.CreateAsync(new CreateNotificationTemplateInput
        {
            DisplayName = "Custom Welcome",
            Category = "Notifications",
            BodyTemplate = "Custom body"
        });

        var renamed = await fixture.Service.RenameAsync(new RenameNotificationTemplateInput
        {
            TemplateName = "Notifications.CustomWelcome",
            NewDisplayName = "Custom Welcome Renamed"
        });

        Assert.Equal("Notifications.CustomWelcomeRenamed", renamed.Name);
        Assert.Equal("Custom Welcome Renamed", renamed.DisplayName);
        Assert.Equal("Database/Notifications/CustomWelcomeRenamed.tpl", renamed.RelativePath);
        await Assert.ThrowsAsync<EntityNotFoundException>(() => fixture.Service.GetManageAsync("Notifications.CustomWelcome"));

        await Assert.ThrowsAsync<Shared.SharedConflictException>(() => fixture.Service.RenameAsync(new RenameNotificationTemplateInput
        {
            TemplateName = "Identity.PasswordReset",
            NewDisplayName = "Password Reset Copy"
        }));

    }

    [Fact]
    public async Task Archive_and_restore_should_filter_lists_without_losing_manage_detail_or_runtime_history()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Archive_and_restore_should_filter_lists_without_losing_manage_detail_or_runtime_history),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        await fixture.ImportService.EnsureImportedAsync();

        var archived = await fixture.Service.ArchiveAsync(new NotificationTemplateNameInput
        {
            TemplateName = "Identity.PasswordReset"
        });

        Assert.True(archived.IsArchived);
        Assert.NotNull(archived.ArchivedAtUtc);

        var activePage = await fixture.Service.GetListAsync(new GetNotificationTemplatesInput
        {
            ArchiveState = NotificationTemplateArchiveStates.Active,
            MaxResultCount = 10
        });
        Assert.DoesNotContain(activePage.Items, x => x.Name == "Identity.PasswordReset");

        var archivedPage = await fixture.Service.GetListAsync(new GetNotificationTemplatesInput
        {
            ArchiveState = NotificationTemplateArchiveStates.Archived,
            MaxResultCount = 10
        });
        var archivedItem = Assert.Single(archivedPage.Items);
        Assert.Equal("Identity.PasswordReset", archivedItem.Name);
        Assert.True(archivedItem.IsArchived);

        var manage = await fixture.Service.GetManageAsync("Identity.PasswordReset");
        Assert.True(manage.IsArchived);
        Assert.Equal(1, manage.PublishedRevisionNumber);

        var runtime = await fixture.Service.PreviewAsync(new RenderTemplateRequestDto
        {
            TemplateName = "Identity.PasswordReset",
            Model = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userName"] = "Admin"
            }
        });
        Assert.Equal("<html>Hello Admin</html>", runtime.Content);

        var restored = await fixture.Service.RestoreAsync(new NotificationTemplateNameInput
        {
            TemplateName = "Identity.PasswordReset"
        });
        Assert.False(restored.IsArchived);

        var restoredPage = await fixture.Service.GetListAsync(new GetNotificationTemplatesInput
        {
            ArchiveState = NotificationTemplateArchiveStates.Active,
            MaxResultCount = 10
        });
        Assert.Contains(restoredPage.Items, x => x.Name == "Identity.PasswordReset");
    }

    [Fact]
    public async Task Delete_draft_should_only_remove_draft_only_database_templates_without_revisions()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Delete_draft_should_only_remove_draft_only_database_templates_without_revisions),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        await fixture.Service.CreateAsync(new CreateNotificationTemplateInput
        {
            DisplayName = "Delete Me",
            Category = "Notifications",
            BodyTemplate = "Delete body"
        });

        await fixture.Service.DeleteDraftAsync(new NotificationTemplateNameInput
        {
            TemplateName = "Notifications.DeleteMe"
        });

        await Assert.ThrowsAsync<EntityNotFoundException>(() => fixture.Service.GetManageAsync("Notifications.DeleteMe"));

        await Assert.ThrowsAsync<Shared.SharedConflictException>(() => fixture.Service.DeleteDraftAsync(new NotificationTemplateNameInput
        {
            TemplateName = "Identity.PasswordReset"
        }));

        await fixture.Service.CreateAsync(new CreateNotificationTemplateInput
        {
            DisplayName = "Published",
            Category = "Notifications",
            BodyTemplate = "Published body"
        });
        await fixture.Service.PublishDraftAsync(new PublishNotificationTemplateDraftInput
        {
            TemplateName = "Notifications.Published"
        });

        await Assert.ThrowsAsync<Shared.SharedConflictException>(() => fixture.Service.DeleteDraftAsync(new NotificationTemplateNameInput
        {
            TemplateName = "Notifications.Published"
        }));
    }

    [Fact]
    public async Task Republish_should_restore_older_revision_as_the_new_published_version()
    {
        var fixture = CreateTemplateAdminFixture(
            nameof(Republish_should_restore_older_revision_as_the_new_published_version),
            [
                TestTemplateFile.Create("Identity.PasswordReset", "Password Reset", "Identity", "Identity/PasswordReset.tpl", "Hello {{model.userName}}")
            ]);

        await fixture.Service.SaveDraftAsync(new SaveNotificationTemplateDraftInput
        {
            TemplateName = "Identity.PasswordReset",
            DisplayName = "Password Reset",
            Category = "Identity",
            SubjectTemplate = "Revision two {{model.userName}}",
            BodyTemplate = "Revision two {{model.userName}}"
        });
        await fixture.Service.PublishDraftAsync(new PublishNotificationTemplateDraftInput
        {
            TemplateName = "Identity.PasswordReset"
        });

        var manageBeforeRollback = await fixture.Service.GetManageAsync("Identity.PasswordReset");
        var originalRevision = Assert.Single(manageBeforeRollback.Revisions.Where(x => x.RevisionNumber == 1));

        var rolledBack = await fixture.Service.RepublishAsync(new RepublishNotificationTemplateRevisionInput
        {
            TemplateName = "Identity.PasswordReset",
            RevisionId = originalRevision.Id
        });

        Assert.Equal(3, rolledBack.PublishedRevisionNumber);
        Assert.Equal(3, rolledBack.RevisionCount);

        var runtimeAfterRollback = await fixture.Service.PreviewAsync(new RenderTemplateRequestDto
        {
            TemplateName = "Identity.PasswordReset",
            Model = new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["userName"] = "Admin"
            }
        });
        Assert.Equal("<html>Hello Admin</html>", runtimeAfterRollback.Content);
        Assert.Null(runtimeAfterRollback.RenderedSubject);
    }

    [Fact]
    public async Task Replay_should_preserve_original_runtime_shape_and_recipients()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Replay_should_preserve_original_runtime_shape_and_recipients), tenantId);
        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Identity.PasswordReset",
            templateModelJson: JsonSerializer.Serialize(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["userName"] = "admin",
                    ["resetUrl"] = "https://localhost/reset"
                }),
            subject: "Reset password");

        var recipientA = new NotificationRecipient(
            id: 1,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "admin@example.test");
        var recipientB = new NotificationRecipient(
            id: 2,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "backup@example.test");

        dbContext.Notifications.Add(notification);
        dbContext.NotificationRecipients.AddRange(recipientA, recipientB);
        await dbContext.SaveChangesAsync();

        var scheduler = new CapturingNotificationDeliveryScheduler();
        var service = new NotificationReplayAppService(new EfTestRepository<Notification, Guid>(dbContext), new EfTestRepository<NotificationRecipient, long>(dbContext), scheduler, new ConfigurableDataFilter(true), new TestCurrentTenant(null), SharedOnlyTenantDatabases.Locator());

        var result = await service.ReplayAsync(new ReplayNotificationInput { NotificationId = notification.Id, OperationId = Guid.CreateVersion7() });

        Assert.True(result.Accepted);
        Assert.Equal(notification.Id, result.OriginalNotificationId);
        Assert.Equal(2, result.RecipientCount);
        Assert.NotNull(scheduler.LastRequest);
        Assert.Equal(notification.TenantId, scheduler.LastTenantId);
        Assert.Equal(NotificationSendChannel.Email, scheduler.LastRequest.Channel);
        Assert.Equal(notification.TemplateName, scheduler.LastRequest.TemplateName);
        Assert.Equal(notification.Subject, scheduler.LastRequest.Subject);
        Assert.False(scheduler.LastRequest.RespectUserPreferences);
        Assert.Collection(
            scheduler.LastRequest.Recipients,
            recipient => Assert.Equal("admin@example.test", recipient.RecipientValue),
            recipient => Assert.Equal("backup@example.test", recipient.RecipientValue));
        Assert.Equal("admin", scheduler.LastRequest.TemplateModel["userName"]);
    }

    [Fact]
    public async Task Replay_should_fail_when_notification_has_no_resolved_recipients()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Replay_should_fail_when_notification_has_no_resolved_recipients), tenantId);
        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.App,
            templateName: "Tenant.Created",
            templateModelJson: "{}");

        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync();

        var service = new NotificationReplayAppService(new EfTestRepository<Notification, Guid>(dbContext), new EfTestRepository<NotificationRecipient, long>(dbContext), new CapturingNotificationDeliveryScheduler(), new ConfigurableDataFilter(true), new TestCurrentTenant(null), SharedOnlyTenantDatabases.Locator());

        var exception = await Assert.ThrowsAsync<SharedConflictException>(() =>
            service.ReplayAsync(new ReplayNotificationInput { NotificationId = notification.Id, OperationId = Guid.CreateVersion7() }));

        Assert.Contains("no resolved recipients", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Replay_override_should_preview_and_execute_explicit_recipients_with_original_context()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Replay_override_should_preview_and_execute_explicit_recipients_with_original_context), tenantId);
        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Identity.PasswordReset",
            templateModelJson: JsonSerializer.Serialize(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["userName"] = "admin"
                }),
            subject: "Reset password");

        var originalRecipient = new NotificationRecipient(
            id: 1,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "admin@example.test");
        dbContext.Notifications.Add(notification);
        dbContext.NotificationRecipients.Add(originalRecipient);
        await dbContext.SaveChangesAsync();

        var scheduler = new CapturingNotificationDeliveryScheduler();
        var service = new NotificationReplayAppService(new EfTestRepository<Notification, Guid>(dbContext), new EfTestRepository<NotificationRecipient, long>(dbContext), scheduler, new ConfigurableDataFilter(true), new TestCurrentTenant(null), SharedOnlyTenantDatabases.Locator());
        var recipientIds = new List<long> { originalRecipient.Id };

        var preview = await service.PreviewOverrideAsync(new PreviewReplayNotificationOverrideInput
        {
            NotificationId = notification.Id,
            RecipientIds = recipientIds
        });

        Assert.True(preview.CanExecute);
        Assert.Equal(notification.Id, preview.NotificationId);
        Assert.Equal(notification.TenantId, preview.TenantId);
        Assert.Equal(notification.Channel, preview.Channel);
        Assert.Equal(notification.TemplateName, preview.TemplateName);
        Assert.Equal(1, preview.RecipientCount);
        var previewRecipient = Assert.Single(preview.Recipients);
        Assert.Equal(originalRecipient.Id, previewRecipient.RecipientId);
        Assert.Equal("a•••@example.test", previewRecipient.DisplayLabel);

        var operationId = Guid.CreateVersion7();
        var result = await service.ReplayOverrideAsync(new ReplayNotificationOverrideInput
        {
            NotificationId = notification.Id,
            OperationId = operationId,
            RecipientIds = recipientIds
        });

        Assert.True(result.Accepted);
        Assert.Equal(notification.Id, result.OriginalNotificationId);
        Assert.Equal(1, result.RecipientCount);
        Assert.NotNull(scheduler.LastRequest);
        Assert.Equal(notification.TenantId, scheduler.LastTenantId);
        Assert.Equal(NotificationSendChannel.Email, scheduler.LastRequest.Channel);
        Assert.Equal(notification.TemplateName, scheduler.LastRequest.TemplateName);
        Assert.Equal("Notifications.AdminReplayOverride", scheduler.LastRequest.SourceService);
        Assert.Equal(operationId, scheduler.LastRequest.RequestId);
        var recipient = Assert.Single(scheduler.LastRequest.Recipients);
        Assert.Equal(NotificationSendRecipientType.EmailAddress, recipient.RecipientType);
        Assert.Equal("admin@example.test", recipient.RecipientValue);
    }

    [Fact]
    public async Task Replay_override_should_fail_closed_without_explicit_recipients()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Replay_override_should_fail_closed_without_explicit_recipients), tenantId);
        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Identity.PasswordReset",
            templateModelJson: "{}");

        dbContext.Notifications.Add(notification);
        await dbContext.SaveChangesAsync();

        var service = new NotificationReplayAppService(new EfTestRepository<Notification, Guid>(dbContext), new EfTestRepository<NotificationRecipient, long>(dbContext), new CapturingNotificationDeliveryScheduler(), new ConfigurableDataFilter(true), new TestCurrentTenant(null), SharedOnlyTenantDatabases.Locator());

        var exception = await Assert.ThrowsAsync<SharedValidationException>(() =>
            service.ReplayOverrideAsync(new ReplayNotificationOverrideInput
            {
                NotificationId = notification.Id,
                OperationId = Guid.CreateVersion7(),
                RecipientIds = []
            }));

        Assert.Contains("original recipient", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delivery_resend_should_queue_only_failed_deliveries_for_original_notification()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Delivery_resend_should_queue_only_failed_deliveries_for_original_notification), tenantId);
        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Identity.PasswordReset",
            templateModelJson: JsonSerializer.Serialize(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["userName"] = "admin"
                }),
            subject: "Reset password");

        var failedRecipient = new NotificationRecipient(
            id: 11,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "failed@example.test");
        var sentRecipient = new NotificationRecipient(
            id: 12,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "sent@example.test");

        var failedDelivery = new NotificationDelivery(
            id: 21,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientId: failedRecipient.Id);
        failedDelivery.MarkProcessing(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(-50));
        failedDelivery.MarkFailed("mailbox_rejected", DateTime.UtcNow.AddMinutes(30));

        var sentDelivery = new NotificationDelivery(
            id: 22,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientId: sentRecipient.Id);
        sentDelivery.MarkProcessing(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(-50));
        sentDelivery.MarkProviderAccepted(DateTime.UtcNow.AddMinutes(-45), "provider-message-id");

        dbContext.Notifications.Add(notification);
        dbContext.NotificationRecipients.AddRange(failedRecipient, sentRecipient);
        dbContext.NotificationDeliveries.AddRange(failedDelivery, sentDelivery);
        await dbContext.SaveChangesAsync();

        var scheduler = new CapturingNotificationDeliveryScheduler();
        var service = new NotificationDeliveryOperationsAppService(
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            scheduler,
            new TestCurrentPrincipalAccessor("operator-1"),
            new NotificationsGrantedPermissionChecker(NotificationsPermissions.Deliveries.Resend),
            TimeProvider.System);

        var result = await service.ResendFailedAsync(new ResendFailedNotificationDeliveriesInput
        {
            NotificationId = notification.Id
        });

        Assert.True(result.Accepted);
        Assert.Equal(notification.Id, result.OriginalNotificationId);
        Assert.Equal(1, result.RecipientCount);
        Assert.Equal(1, result.FailedDeliveryCount);
        Assert.NotNull(scheduler.LastRequest);
        Assert.Equal(notification.TenantId, scheduler.LastTenantId);
        Assert.Equal(NotificationSendChannel.Email, scheduler.LastRequest.Channel);
        Assert.Equal(notification.TemplateName, scheduler.LastRequest.TemplateName);
        Assert.Equal(notification.Subject, scheduler.LastRequest.Subject);
        Assert.Equal("Notifications.AdminDeliveryResend", scheduler.LastRequest.SourceService);
        Assert.False(scheduler.LastRequest.RespectUserPreferences);
        var recipient = Assert.Single(scheduler.LastRequest.Recipients);
        Assert.Equal("failed@example.test", recipient.RecipientValue);
        Assert.Equal("admin", scheduler.LastRequest.TemplateModel["userName"]);
    }

    [Fact]
    public async Task Delivery_resend_should_fail_closed_for_non_failed_delivery()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Delivery_resend_should_fail_closed_for_non_failed_delivery), tenantId);
        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Identity.PasswordReset",
            templateModelJson: "{}",
            subject: "Reset password");
        var recipient = new NotificationRecipient(
            id: 31,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "sent@example.test");
        var sentDelivery = new NotificationDelivery(
            id: 41,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientId: recipient.Id);
        sentDelivery.MarkProcessing(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(-50));
        sentDelivery.MarkProviderAccepted(DateTime.UtcNow.AddMinutes(-45), "provider-message-id");

        dbContext.Notifications.Add(notification);
        dbContext.NotificationRecipients.Add(recipient);
        dbContext.NotificationDeliveries.Add(sentDelivery);
        await dbContext.SaveChangesAsync();

        var service = new NotificationDeliveryOperationsAppService(
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new CapturingNotificationDeliveryScheduler(),
            new TestCurrentPrincipalAccessor("operator-1"),
            new NotificationsGrantedPermissionChecker(NotificationsPermissions.Deliveries.Resend),
            TimeProvider.System);

        var exception = await Assert.ThrowsAsync<SharedConflictException>(() =>
            service.ResendDeliveryAsync(new ResendNotificationDeliveryInput
            {
                DeliveryId = sentDelivery.Id
            }));

        Assert.Contains("failed delivery", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Delivery_review_should_support_confirm_and_explicit_retry_only_for_terminal_review_states()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Delivery_review_should_support_confirm_and_explicit_retry_only_for_terminal_review_states), tenantId);
        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Security.Alert",
            templateModelJson: "{}",
            subject: "Security alert");
        var confirmRecipient = new NotificationRecipient(
            id: 71,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "confirm@example.test");
        var retryRecipient = new NotificationRecipient(
            id: 72,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "retry@example.test");
        var now = DateTime.UtcNow;
        var confirmDelivery = new NotificationDelivery(81, tenantId, notification.Id, confirmRecipient.Id);
        confirmDelivery.MarkProcessing(now.AddMinutes(-10), now.AddMinutes(-5), Guid.NewGuid());
        confirmDelivery.MarkNeedsReview("provider_outcome_ambiguous", now.AddMinutes(-4));
        var retryDelivery = new NotificationDelivery(82, tenantId, notification.Id, retryRecipient.Id);
        retryDelivery.MarkProcessing(now.AddMinutes(-10), now.AddMinutes(-5), Guid.NewGuid());
        retryDelivery.MarkDeadLettered("retry_limit_exhausted", now.AddMinutes(-4));

        dbContext.Notifications.Add(notification);
        dbContext.NotificationRecipients.AddRange(confirmRecipient, retryRecipient);
        dbContext.NotificationDeliveries.AddRange(confirmDelivery, retryDelivery);
        await dbContext.SaveChangesAsync();

        var service = new NotificationDeliveryOperationsAppService(
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new CapturingNotificationDeliveryScheduler(),
            new TestCurrentPrincipalAccessor("operator-1"),
            new NotificationsGrantedPermissionChecker(NotificationsPermissions.Deliveries.Resend),
            TimeProvider.System);

        var confirmed = await service.ConfirmDeliveryAsync(new ConfirmNotificationDeliveryInput
        {
            DeliveryId = confirmDelivery.Id,
            ProviderMessageId = "operator-confirmed"
        });
        var retried = await service.RetryDeliveryAsync(new RetryNotificationDeliveryInput
        {
            DeliveryId = retryDelivery.Id
        });

        Assert.Equal(NotificationStatus.ProviderAccepted, confirmed.Status);
        Assert.Equal("operator-confirmed", confirmed.ProviderMessageId);
        Assert.Equal(NotificationStatus.Queued, retried.Status);
        Assert.Null(retried.DeliveryAttemptId);
        Assert.Equal(NotificationStatus.ProviderAccepted, (await dbContext.NotificationDeliveries.FindAsync(confirmDelivery.Id))!.Status);
        Assert.Equal(NotificationStatus.Queued, (await dbContext.NotificationDeliveries.FindAsync(retryDelivery.Id))!.Status);

        var operatorEvents = await dbContext.NotificationDeliveryChronology
            .Where(x => x.DeliveryId == confirmDelivery.Id || x.DeliveryId == retryDelivery.Id)
            .Where(x => x.ActorType == NotificationDeliveryActorType.Operator)
            .ToListAsync();
        Assert.Contains(operatorEvents, x =>
            x.DeliveryId == confirmDelivery.Id &&
            x.EventKind == NotificationDeliveryEventKind.OperatorConfirmed &&
            x.ActorId == "operator-1");
        Assert.Contains(operatorEvents, x =>
            x.DeliveryId == retryDelivery.Id &&
            x.EventKind == NotificationDeliveryEventKind.Requeued &&
            x.ActorId == "operator-1");
    }

    [Fact]
    public async Task Delivery_worker_should_not_resend_an_expired_processing_attempt()
    {
        var fixture = await CreateWorkerFixtureAsync(
            nameof(Delivery_worker_should_not_resend_an_expired_processing_attempt),
            templateModelJson: "{}");
        fixture.Delivery.MarkProcessing(
            DateTime.UtcNow.AddMinutes(-10),
            DateTime.UtcNow.AddMinutes(-5),
            Guid.NewGuid());
        await fixture.DbContext.SaveChangesAsync();

        var processed = await fixture.Worker.ProcessBatchAsync();

        Assert.Equal(0, processed);
        Assert.Equal(0, fixture.Handler.CallCount);
        Assert.Equal(NotificationStatus.NeedsReview, fixture.Delivery.Status);
        Assert.Null(fixture.Delivery.NextAttemptTimeUtc);
    }

    [Fact]
    public async Task Delivery_worker_should_dead_letter_a_corrupt_template_model_without_sending()
    {
        var fixture = await CreateWorkerFixtureAsync(
            nameof(Delivery_worker_should_dead_letter_a_corrupt_template_model_without_sending),
            templateModelJson: "{");

        var processed = await fixture.Worker.ProcessBatchAsync();

        Assert.Equal(0, processed);
        Assert.Equal(0, fixture.Handler.CallCount);
        Assert.Equal(NotificationStatus.DeadLettered, fixture.Delivery.Status);
        Assert.Equal("template_model_invalid", fixture.Delivery.LastError);
    }

    [Fact]
    public async Task Delivery_worker_should_dead_letter_the_final_failed_attempt()
    {
        var fixture = await CreateWorkerFixtureAsync(
            nameof(Delivery_worker_should_dead_letter_the_final_failed_attempt),
            templateModelJson: "{}",
            maxRetryCount: 1);
        fixture.Handler.Result = ChannelDeliveryResult.PermanentRejection("provider_rejected");

        var processed = await fixture.Worker.ProcessBatchAsync();

        Assert.Equal(1, processed);
        Assert.Equal(1, fixture.Handler.CallCount);
        Assert.Equal(NotificationStatus.DeadLettered, fixture.Delivery.Status);
        Assert.Equal("provider_rejected", fixture.Delivery.LastError);
        Assert.NotNull(fixture.Delivery.DeliveryAttemptId);
    }

    [Fact]
    public async Task External_delivery_success_records_provider_acceptance_not_confirmed_delivery()
    {
        var fixture = await CreateWorkerFixtureAsync(
            nameof(External_delivery_success_records_provider_acceptance_not_confirmed_delivery),
            templateModelJson: "{}");

        Assert.Equal(1, await fixture.Worker.ProcessBatchAsync());

        Assert.Equal(NotificationStatus.ProviderAccepted, fixture.Delivery.Status);
    }

    [Fact]
    public async Task Unavailable_channel_moves_to_review_without_retry_or_false_success()
    {
        var fixture = await CreateWorkerFixtureAsync(
            nameof(Unavailable_channel_moves_to_review_without_retry_or_false_success),
            templateModelJson: "{}");
        fixture.Handler.Result = ChannelDeliveryResult.Unavailable("whatsapp_consent_authority_unavailable");

        Assert.Equal(1, await fixture.Worker.ProcessBatchAsync());

        Assert.Equal(NotificationStatus.NeedsReview, fixture.Delivery.Status);
        Assert.Null(fixture.Delivery.NextAttemptTimeUtc);
        Assert.Equal("whatsapp_consent_authority_unavailable", fixture.Delivery.LastError);
    }

    [Fact]
    public async Task Delivery_override_resend_should_preview_and_execute_explicit_recipients_for_failed_delivery_only()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Delivery_override_resend_should_preview_and_execute_explicit_recipients_for_failed_delivery_only), tenantId);
        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Identity.PasswordReset",
            templateModelJson: JsonSerializer.Serialize(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["userName"] = "admin"
                }),
            subject: "Reset password");
        var originalRecipient = new NotificationRecipient(
            id: 51,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "failed@example.test");
        var failedDelivery = new NotificationDelivery(
            id: 61,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientId: originalRecipient.Id);
        failedDelivery.MarkProcessing(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddMinutes(-50));
        failedDelivery.MarkFailed("mailbox_rejected", DateTime.UtcNow.AddMinutes(30));

        dbContext.Notifications.Add(notification);
        dbContext.NotificationRecipients.Add(originalRecipient);
        dbContext.NotificationDeliveries.Add(failedDelivery);
        await dbContext.SaveChangesAsync();

        var scheduler = new CapturingNotificationDeliveryScheduler();
        var service = new NotificationDeliveryOperationsAppService(
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            scheduler,
            new TestCurrentPrincipalAccessor("operator-1"),
            new NotificationsGrantedPermissionChecker(
                NotificationsPermissions.Deliveries.OverrideResend),
            TimeProvider.System);
        var recipients = new List<NotificationOverrideRecipientDto>
        {
            new()
            {
                RecipientType = NotificationRecipientType.EmailAddress,
                RecipientValue = "override@example.test"
            }
        };

        var preview = await service.PreviewOverrideResendAsync(new PreviewNotificationDeliveryOverrideResendInput
        {
            DeliveryId = failedDelivery.Id,
            Recipients = recipients
        });

        Assert.True(preview.CanExecute);
        Assert.Equal(failedDelivery.Id, preview.DeliveryId);
        Assert.Equal(notification.Id, preview.NotificationId);
        Assert.Equal("failed@example.test", preview.OriginalRecipientValue);
        Assert.Equal(1, preview.RecipientCount);

        var result = await service.ResendOverrideAsync(new ResendNotificationDeliveryOverrideInput
        {
            DeliveryId = failedDelivery.Id,
            Recipients = recipients
        });

        Assert.True(result.Accepted);
        Assert.Equal(notification.Id, result.OriginalNotificationId);
        Assert.Equal(1, result.RecipientCount);
        Assert.Equal(1, result.FailedDeliveryCount);
        Assert.NotNull(scheduler.LastRequest);
        Assert.Equal(notification.TenantId, scheduler.LastTenantId);
        Assert.Equal(NotificationSendChannel.Email, scheduler.LastRequest.Channel);
        Assert.Equal(notification.TemplateName, scheduler.LastRequest.TemplateName);
        Assert.Equal("Notifications.AdminDeliveryOverrideResend", scheduler.LastRequest.SourceService);
        var recipient = Assert.Single(scheduler.LastRequest.Recipients);
        Assert.Equal("override@example.test", recipient.RecipientValue);
    }

    [Fact]
    public async Task Test_send_should_dispatch_through_the_selected_configurations_smtp_settings()
    {
        var hostDbContext = CreateDbContext(nameof(Test_send_should_dispatch_through_the_selected_configurations_smtp_settings), tenantId: null);
        var currentTenant = new TestCurrentTenant(null);
        var hostConfig = new NotificationProviderConfig(
            id: Guid.NewGuid(),
            tenantId: null,
            channel: NotificationChannel.Email,
            providerType: "Smtp",
            name: "Primary",
            isActive: true,
            isDefault: true,
            priority: 0,
            settingsJson: EmailProviderConfigAppService.SerializeSettings("smtp.host.local", 25, null, "host@example.test", "Host Notifications", "Auto"),
            complianceContact: null);
        hostDbContext.NotificationProviderConfigs.Add(hostConfig);
        await hostDbContext.SaveChangesAsync();

        var hostTransportFactory = new RecordingSmtpTransportFactory();
        var hostService = new EmailProviderConfigAppService(
            new NotificationProviderConfigStore(
                new EfTestRepository<NotificationProviderConfig, Guid>(hostDbContext),
                currentTenant,
                new NoopNotificationsProviderSecretProtector(),
                new PassthroughHybridCache<NotificationProviderConfigCacheItem>()),
            new SmtpMessageDispatcher(hostTransportFactory, NullLogger<SmtpMessageDispatcher>.Instance));

        var hostResult = await hostService.SendTestAsync(hostConfig.Id, new EmailProviderConfigTestSendInput
        {
            RecipientEmail = "ops@example.test"
        });

        Assert.True(hostResult.Accepted);
        Assert.Null(hostResult.TenantId);
        Assert.Equal("ops@example.test", hostResult.RecipientEmail);
        Assert.Equal("smtp.host.local", hostTransportFactory.LastClient?.Host);
        Assert.Equal(25, hostTransportFactory.LastClient?.Port);

        var tenantId = Guid.NewGuid();
        var tenantDbContext = CreateDbContext(nameof(Test_send_should_dispatch_through_the_selected_configurations_smtp_settings) + "_tenant", tenantId);
        var tenantConfig = new NotificationProviderConfig(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            channel: NotificationChannel.Email,
            providerType: "Smtp",
            name: "Primary",
            isActive: true,
            isDefault: true,
            priority: 0,
            settingsJson: EmailProviderConfigAppService.SerializeSettings("smtp.tenant.local", 2525, null, "tenant@example.test", "Tenant Notifications", "Auto"),
            complianceContact: null);
        tenantDbContext.NotificationProviderConfigs.Add(tenantConfig);
        await tenantDbContext.SaveChangesAsync();

        var tenantCurrentTenant = new TestCurrentTenant(tenantId);
        var tenantTransportFactory = new RecordingSmtpTransportFactory();
        var tenantService = new EmailProviderConfigAppService(
            new NotificationProviderConfigStore(
                new EfTestRepository<NotificationProviderConfig, Guid>(tenantDbContext),
                tenantCurrentTenant,
                new NoopNotificationsProviderSecretProtector(),
                new PassthroughHybridCache<NotificationProviderConfigCacheItem>()),
            new SmtpMessageDispatcher(tenantTransportFactory, NullLogger<SmtpMessageDispatcher>.Instance));

        var tenantResult = await tenantService.SendTestAsync(tenantConfig.Id, new EmailProviderConfigTestSendInput
        {
            RecipientEmail = "tenant.ops@example.test"
        });

        Assert.True(tenantResult.Accepted);
        Assert.Equal(tenantId, tenantResult.TenantId);
        Assert.Equal("smtp.tenant.local", tenantTransportFactory.LastClient?.Host);
        Assert.Equal("tenant.ops@example.test", tenantResult.RecipientEmail);
    }

    [Fact]
    public async Task Test_send_should_fail_closed_for_an_inactive_configuration()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Test_send_should_fail_closed_for_an_inactive_configuration), tenantId);
        var currentTenant = new TestCurrentTenant(tenantId);

        var inactiveConfig = new NotificationProviderConfig(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            channel: NotificationChannel.Email,
            providerType: "Smtp",
            name: "Primary",
            isActive: false,
            isDefault: true,
            priority: 0,
            settingsJson: EmailProviderConfigAppService.SerializeSettings("smtp.tenant.local", 2525, null, "tenant@example.test", "Tenant Notifications", "Auto"),
            complianceContact: null);
        dbContext.NotificationProviderConfigs.Add(inactiveConfig);
        await dbContext.SaveChangesAsync();

        var service = new EmailProviderConfigAppService(
            new NotificationProviderConfigStore(
                new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
                currentTenant,
                new NoopNotificationsProviderSecretProtector(),
                new PassthroughHybridCache<NotificationProviderConfigCacheItem>()),
            new SmtpMessageDispatcher(new RecordingSmtpTransportFactory(), NullLogger<SmtpMessageDispatcher>.Instance));

        var inactive = await Assert.ThrowsAsync<Shared.SharedConflictException>(() =>
            service.SendTestAsync(inactiveConfig.Id, new EmailProviderConfigTestSendInput
            {
                RecipientEmail = "ops@example.test"
            }));
        Assert.Contains("inactive", inactive.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Email_provider_config_should_reject_access_to_a_foreign_tenants_configuration()
    {
        var currentTenantId = Guid.NewGuid();
        var foreignTenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Email_provider_config_should_reject_access_to_a_foreign_tenants_configuration), tenantId: null);

        var foreignConfig = new NotificationProviderConfig(
            id: Guid.NewGuid(),
            tenantId: foreignTenantId,
            channel: NotificationChannel.Email,
            providerType: "Smtp",
            name: "Primary",
            isActive: true,
            isDefault: true,
            priority: 0,
            settingsJson: EmailProviderConfigAppService.SerializeSettings("smtp.foreign.local", 2525, null, "foreign@example.test", "Foreign Tenant", "Auto"),
            complianceContact: null);
        dbContext.NotificationProviderConfigs.Add(foreignConfig);
        await dbContext.SaveChangesAsync();

        var currentTenant = new TestCurrentTenant(currentTenantId);
        var service = new EmailProviderConfigAppService(
            new NotificationProviderConfigStore(
                new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
                currentTenant,
                new NoopNotificationsProviderSecretProtector(),
                new PassthroughHybridCache<NotificationProviderConfigCacheItem>()),
            new SmtpMessageDispatcher(new RecordingSmtpTransportFactory(), NullLogger<SmtpMessageDispatcher>.Instance));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetAsync(foreignConfig.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.UpdateAsync(foreignConfig.Id, new UpdateEmailProviderConfigDto
        {
            Name = "Primary",
            Host = "smtp.foreign.local",
            Port = 2525,
            FromAddress = "foreign@example.test",
            FromName = "Foreign Tenant",
            SecureSocketOption = "Auto",
            IsActive = true
        }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.DeleteAsync(foreignConfig.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SetDefaultAsync(foreignConfig.Id));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.SendTestAsync(foreignConfig.Id, new EmailProviderConfigTestSendInput
        {
            RecipientEmail = "ops@example.test"
        }));
    }

    [Fact]
    public async Task Host_context_should_reject_an_explicit_tenant_scope_on_list_and_create()
    {
        var dbContext = CreateDbContext(nameof(Host_context_should_reject_an_explicit_tenant_scope_on_list_and_create), tenantId: null);
        var currentTenant = new TestCurrentTenant(null);
        var service = new EmailProviderConfigAppService(
            new NotificationProviderConfigStore(
                new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
                currentTenant,
                new NoopNotificationsProviderSecretProtector(),
                new PassthroughHybridCache<NotificationProviderConfigCacheItem>()),
            new SmtpMessageDispatcher(new RecordingSmtpTransportFactory(), NullLogger<SmtpMessageDispatcher>.Instance));

        var someTenantId = Guid.NewGuid();
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetListAsync(someTenantId));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.CreateAsync(new CreateEmailProviderConfigDto
        {
            TenantId = someTenantId,
            Name = "Primary",
            Host = "smtp.local",
            Port = 25,
            FromAddress = "a@example.test",
            SecureSocketOption = "Auto"
        }));
    }

    [Fact]
    public async Task Create_for_current_tenant_should_ignore_a_caller_supplied_tenant_override()
    {
        var currentTenantId = Guid.NewGuid();
        var requestedTenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Create_for_current_tenant_should_ignore_a_caller_supplied_tenant_override), currentTenantId);
        var currentTenant = new TestCurrentTenant(currentTenantId);
        var service = new EmailProviderConfigAppService(
            new NotificationProviderConfigStore(
                new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
                currentTenant,
                new NoopNotificationsProviderSecretProtector(),
                new PassthroughHybridCache<NotificationProviderConfigCacheItem>()),
            new SmtpMessageDispatcher(new RecordingSmtpTransportFactory(), NullLogger<SmtpMessageDispatcher>.Instance));

        var saved = await service.CreateForCurrentTenantAsync(new CreateEmailProviderConfigDto
        {
            TenantId = requestedTenantId,
            Name = "Primary",
            Host = "smtp.current.local",
            Port = 2525,
            FromAddress = "current@example.test",
            FromName = "Current Tenant",
            SecureSocketOption = "Auto",
            IsActive = true,
            IsDefault = true
        });

        Assert.Equal(currentTenantId, saved.TenantId);

        var list = await service.GetCurrentTenantListAsync();
        var only = Assert.Single(list);
        Assert.Equal(currentTenantId, only.TenantId);

        var testResult = await service.SendTestAsync(saved.Id, new EmailProviderConfigTestSendInput
        {
            RecipientEmail = "current.ops@example.test"
        });
        Assert.True(testResult.Accepted);
        Assert.Equal(currentTenantId, testResult.TenantId);
    }

    [Fact]
    public async Task Read_should_report_password_secret_presence_without_exposing_the_key()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Read_should_report_password_secret_presence_without_exposing_the_key), tenantId);
        var currentTenant = new TestCurrentTenant(tenantId);
        var service = new EmailProviderConfigAppService(
            new NotificationProviderConfigStore(
                new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
                currentTenant,
                new NoopNotificationsProviderSecretProtector(),
                new PassthroughHybridCache<NotificationProviderConfigCacheItem>()),
            new SmtpMessageDispatcher(new RecordingSmtpTransportFactory(), NullLogger<SmtpMessageDispatcher>.Instance));

        var withSecret = await service.CreateAsync(new CreateEmailProviderConfigDto
        {
            Name = "With secret",
            Host = "smtp.secret.local",
            Port = 587,
            UserName = "svc-mailer",
            PasswordSecretKey = "SMTP__PRIMARY__PASSWORD",
            FromAddress = "with@example.test",
            SecureSocketOption = "StartTls",
            IsActive = true,
            IsDefault = true
        });

        var withoutSecret = await service.CreateAsync(new CreateEmailProviderConfigDto
        {
            Name = "Without secret",
            Host = "smtp.open.local",
            Port = 25,
            FromAddress = "without@example.test",
            SecureSocketOption = "Auto",
            IsActive = true,
            IsDefault = false
        });

        // The response DTO reports only presence, never the key name itself.
        Assert.True(withSecret.HasPasswordSecret);
        Assert.False(withoutSecret.HasPasswordSecret);

        // The key name must not be discoverable anywhere on the serialized response.
        var serialized = JsonSerializer.Serialize(await service.GetAsync(withSecret.Id));
        Assert.DoesNotContain("SMTP__PRIMARY__PASSWORD", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("PasswordSecretKey", serialized, StringComparison.Ordinal);
        Assert.Contains("HasPasswordSecret", serialized, StringComparison.Ordinal);

        // List and get paths agree.
        var fetched = await service.GetAsync(withSecret.Id);
        Assert.True(fetched.HasPasswordSecret);
        var listed = await service.GetCurrentTenantListAsync();
        Assert.True(listed.Single(x => x.Id == withSecret.Id).HasPasswordSecret);
        Assert.False(listed.Single(x => x.Id == withoutSecret.Id).HasPasswordSecret);
    }

    [Fact]
    public async Task Create_should_reject_a_duplicate_name_within_the_same_tenant()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Create_should_reject_a_duplicate_name_within_the_same_tenant), tenantId);
        var currentTenant = new TestCurrentTenant(tenantId);
        var service = new EmailProviderConfigAppService(
            new NotificationProviderConfigStore(
                new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
                currentTenant,
                new NoopNotificationsProviderSecretProtector(),
                new PassthroughHybridCache<NotificationProviderConfigCacheItem>()),
            new SmtpMessageDispatcher(new RecordingSmtpTransportFactory(), NullLogger<SmtpMessageDispatcher>.Instance));

        await service.CreateAsync(new CreateEmailProviderConfigDto
        {
            Name = "Primary",
            Host = "smtp.one.local",
            Port = 587,
            FromAddress = "one@example.test",
            SecureSocketOption = "StartTls",
            IsActive = true,
            IsDefault = true
        });

        var duplicate = await Assert.ThrowsAsync<Shared.SharedConflictException>(() => service.CreateAsync(new CreateEmailProviderConfigDto
        {
            Name = "Primary",
            Host = "smtp.two.local",
            Port = 587,
            FromAddress = "two@example.test",
            SecureSocketOption = "StartTls",
            IsActive = true
        }));
        Assert.Contains("already exists", duplicate.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Creating_a_second_default_should_clear_the_previous_default()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Creating_a_second_default_should_clear_the_previous_default), tenantId);
        var currentTenant = new TestCurrentTenant(tenantId);
        var service = new EmailProviderConfigAppService(
            new NotificationProviderConfigStore(
                new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
                currentTenant,
                new NoopNotificationsProviderSecretProtector(),
                new PassthroughHybridCache<NotificationProviderConfigCacheItem>()),
            new SmtpMessageDispatcher(new RecordingSmtpTransportFactory(), NullLogger<SmtpMessageDispatcher>.Instance));

        var first = await service.CreateAsync(new CreateEmailProviderConfigDto
        {
            Name = "Primary",
            Host = "smtp.one.local",
            Port = 587,
            FromAddress = "one@example.test",
            SecureSocketOption = "StartTls",
            IsActive = true,
            IsDefault = true
        });

        var second = await service.CreateAsync(new CreateEmailProviderConfigDto
        {
            Name = "Marketing",
            Host = "smtp.two.local",
            Port = 587,
            FromAddress = "two@example.test",
            SecureSocketOption = "StartTls",
            IsActive = true,
            IsDefault = true
        });

        var reloadedFirst = await service.GetAsync(first.Id);
        Assert.False(reloadedFirst.IsDefault);
        Assert.True(second.IsDefault);
    }

    [Fact]
    public async Task SetDefaultAsync_should_flip_the_default_flag_between_configurations()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(SetDefaultAsync_should_flip_the_default_flag_between_configurations), tenantId);
        var currentTenant = new TestCurrentTenant(tenantId);
        var service = new EmailProviderConfigAppService(
            new NotificationProviderConfigStore(
                new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
                currentTenant,
                new NoopNotificationsProviderSecretProtector(),
                new PassthroughHybridCache<NotificationProviderConfigCacheItem>()),
            new SmtpMessageDispatcher(new RecordingSmtpTransportFactory(), NullLogger<SmtpMessageDispatcher>.Instance));

        var first = await service.CreateAsync(new CreateEmailProviderConfigDto
        {
            Name = "Primary",
            Host = "smtp.one.local",
            Port = 587,
            FromAddress = "one@example.test",
            SecureSocketOption = "StartTls",
            IsActive = true,
            IsDefault = true
        });
        var second = await service.CreateAsync(new CreateEmailProviderConfigDto
        {
            Name = "Marketing",
            Host = "smtp.two.local",
            Port = 587,
            FromAddress = "two@example.test",
            SecureSocketOption = "StartTls",
            IsActive = true
        });

        var updatedSecond = await service.SetDefaultAsync(second.Id);
        Assert.True(updatedSecond.IsDefault);

        var reloadedFirst = await service.GetAsync(first.Id);
        Assert.False(reloadedFirst.IsDefault);
    }

    [Fact]
    public async Task DeleteAsync_should_block_removing_the_default_while_other_configurations_exist()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(DeleteAsync_should_block_removing_the_default_while_other_configurations_exist), tenantId);
        var currentTenant = new TestCurrentTenant(tenantId);
        var service = new EmailProviderConfigAppService(
            new NotificationProviderConfigStore(
                new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
                currentTenant,
                new NoopNotificationsProviderSecretProtector(),
                new PassthroughHybridCache<NotificationProviderConfigCacheItem>()),
            new SmtpMessageDispatcher(new RecordingSmtpTransportFactory(), NullLogger<SmtpMessageDispatcher>.Instance));

        var first = await service.CreateAsync(new CreateEmailProviderConfigDto
        {
            Name = "Primary",
            Host = "smtp.one.local",
            Port = 587,
            FromAddress = "one@example.test",
            SecureSocketOption = "StartTls",
            IsActive = true,
            IsDefault = true
        });
        await service.CreateAsync(new CreateEmailProviderConfigDto
        {
            Name = "Marketing",
            Host = "smtp.two.local",
            Port = 587,
            FromAddress = "two@example.test",
            SecureSocketOption = "StartTls",
            IsActive = true
        });

        var blocked = await Assert.ThrowsAsync<Shared.SharedConflictException>(() => service.DeleteAsync(first.Id));
        Assert.Contains("Set a different configuration as default", blocked.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeleteAsync_should_allow_removing_the_sole_remaining_configuration()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(DeleteAsync_should_allow_removing_the_sole_remaining_configuration), tenantId);
        var currentTenant = new TestCurrentTenant(tenantId);
        var service = new EmailProviderConfigAppService(
            new NotificationProviderConfigStore(
                new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
                currentTenant,
                new NoopNotificationsProviderSecretProtector(),
                new PassthroughHybridCache<NotificationProviderConfigCacheItem>()),
            new SmtpMessageDispatcher(new RecordingSmtpTransportFactory(), NullLogger<SmtpMessageDispatcher>.Instance));

        var only = await service.CreateAsync(new CreateEmailProviderConfigDto
        {
            Name = "Primary",
            Host = "smtp.one.local",
            Port = 587,
            FromAddress = "one@example.test",
            SecureSocketOption = "StartTls",
            IsActive = true,
            IsDefault = true
        });

        await service.DeleteAsync(only.Id);

        var remaining = await service.GetCurrentTenantListAsync();
        Assert.Empty(remaining);
    }

    [Fact]
    public async Task Sender_should_resolve_the_default_configuration_not_just_the_most_recently_created_active_one()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Sender_should_resolve_the_default_configuration_not_just_the_most_recently_created_active_one), tenantId);

        var olderDefault = new NotificationProviderConfig(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            channel: NotificationChannel.Email,
            providerType: "Smtp",
            name: "Primary",
            isActive: true,
            isDefault: true,
            priority: 0,
            settingsJson: EmailProviderConfigAppService.SerializeSettings("smtp.older.local", 587, null, "older@example.test", "Older", "StartTls"),
            complianceContact: null);
        dbContext.NotificationProviderConfigs.Add(olderDefault);
        await dbContext.SaveChangesAsync();

        var newerNonDefault = new NotificationProviderConfig(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            channel: NotificationChannel.Email,
            providerType: "Smtp",
            name: "Marketing",
            isActive: true,
            isDefault: false,
            priority: 0,
            settingsJson: EmailProviderConfigAppService.SerializeSettings("smtp.newer.local", 2525, null, "newer@example.test", "Newer", "StartTls"),
            complianceContact: null);
        dbContext.NotificationProviderConfigs.Add(newerNonDefault);
        await dbContext.SaveChangesAsync();

        var currentTenant = new TestCurrentTenant(tenantId);
        var transportFactory = new RecordingSmtpTransportFactory();
        var resolver = new NotificationProviderConfigResolver(
            new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new ImmediateUnitOfWorkExecutor(),
            new NoopNotificationsProviderSecretProtector(),
            new PassthroughHybridCache<NotificationProviderConfigCacheItem>());
        var sender = new MailKitEmailNotificationSender(
            Options.Create(new NotificationsEmailOptions()),
            currentTenant,
            resolver,
            new SmtpMessageDispatcher(transportFactory, NullLogger<SmtpMessageDispatcher>.Instance));

        await sender.SendAsync("someone@example.test", "Subject", "<p>Body</p>");

        Assert.Equal("smtp.older.local", transportFactory.LastClient?.Host);
    }

    [Fact]
    public async Task Provider_diagnostics_should_summarize_configuration_health_and_recent_email_activity()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var dbContext = CreateDbContext(nameof(Provider_diagnostics_should_summarize_configuration_health_and_recent_email_activity), tenantId);

        var config = new NotificationProviderConfig(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            channel: NotificationChannel.Email,
            providerType: "Smtp",
            name: "Primary",
            isActive: true,
            isDefault: true,
            priority: 0,
            settingsJson: EmailProviderConfigAppService.SerializeSettings("smtp.tenant.local", 2525, null, "tenant@example.test", "Tenant Notifications", "Auto"),
            complianceContact: null);
        SetProperty(config, "CreationTime", now.AddDays(-14));
        SetProperty(config, "LastModificationTime", now.AddHours(-2));

        var sentNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Welcome",
            templateModelJson: "{}",
            subject: "Welcome");
        SetProperty(sentNotification, "CreationTime", now.AddHours(-6));
        var sentRecipient = new NotificationRecipient(
            id: 1,
            tenantId: tenantId,
            notificationId: sentNotification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "ops@example.test");
        var sentDelivery = new NotificationDelivery(
            id: 1,
            tenantId: tenantId,
            notificationId: sentNotification.Id,
            recipientId: sentRecipient.Id);
        SetProperty(sentDelivery, "CreationTime", now.AddHours(-6));
        sentDelivery.MarkProcessing(now.AddHours(-6), now.AddHours(-6).AddMinutes(5));
        sentDelivery.MarkProviderAccepted(now.AddHours(-6).AddMinutes(1), "provider-message-1");

        var failedNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Alert",
            templateModelJson: "{}",
            subject: "Alert");
        SetProperty(failedNotification, "CreationTime", now.AddHours(-1));
        var failedRecipient = new NotificationRecipient(
            id: 2,
            tenantId: tenantId,
            notificationId: failedNotification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "security@example.test");
        var failedDelivery = new NotificationDelivery(
            id: 2,
            tenantId: tenantId,
            notificationId: failedNotification.Id,
            recipientId: failedRecipient.Id);
        SetProperty(failedDelivery, "CreationTime", now.AddHours(-1));
        failedDelivery.MarkProcessing(now.AddHours(-1), now.AddHours(-1).AddMinutes(5));
        failedDelivery.MarkFailed("Mailbox rejected", now.AddMinutes(30));

        var smsNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Sms,
            templateName: "Tenant.Sms",
            templateModelJson: "{}",
            subject: "SMS");
        SetProperty(smsNotification, "CreationTime", now.AddMinutes(-20));
        var smsRecipient = new NotificationRecipient(
            id: 3,
            tenantId: tenantId,
            notificationId: smsNotification.Id,
            recipientType: NotificationRecipientType.PhoneNumber,
            recipientValue: "+919999999999");
        var smsDelivery = new NotificationDelivery(
            id: 3,
            tenantId: tenantId,
            notificationId: smsNotification.Id,
            recipientId: smsRecipient.Id);
        SetProperty(smsDelivery, "CreationTime", now.AddMinutes(-20));
        smsDelivery.MarkProcessing(now.AddMinutes(-20), now.AddMinutes(-15));
        smsDelivery.MarkProviderAccepted(now.AddMinutes(-19), "provider-message-sms");

        dbContext.NotificationProviderConfigs.Add(config);
        dbContext.Notifications.AddRange(sentNotification, failedNotification, smsNotification);
        dbContext.NotificationRecipients.AddRange(sentRecipient, failedRecipient, smsRecipient);
        dbContext.NotificationDeliveries.AddRange(sentDelivery, failedDelivery, smsDelivery);
        await dbContext.SaveChangesAsync();

        var service = new NotificationProviderDiagnosticsAppService(
            new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new TestFeatureChecker(NotificationsFeatures.Email),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(null),
            SharedOnlyTenantDatabases.FanOut());

        var summary = await service.GetSummaryAsync(tenantId);

        var channel = Assert.Single(summary.Channels);
        Assert.Equal(NotificationChannel.Email, channel.Channel);
        Assert.Equal(1, channel.ConfigurationCount);
        Assert.Equal(config.Id, channel.DefaultConfigurationId);
        Assert.Equal("Primary", channel.DefaultConfigurationName);
        Assert.Equal("Smtp", channel.ProviderType);
        Assert.True(channel.IsActive);
        Assert.False(channel.UsesHostFallback);
        Assert.Equal(NotificationProviderHealthStatus.Attention, channel.HealthStatus);
        Assert.Equal(7, summary.RecentWindowDays);
        Assert.Equal(2, channel.RecentAttemptCount);
        Assert.Equal(1, channel.RecentSuccessCount);
        Assert.Equal(1, channel.RecentFailureCount);
        Assert.Equal("Mailbox rejected", channel.LatestFailureError);
        Assert.Equal(config.LastModificationTime ?? config.CreationTime, channel.ConfigurationUpdatedAtUtc);
        Assert.NotNull(channel.LatestAttemptAtUtc);
        Assert.NotNull(channel.LatestSuccessAtUtc);
        Assert.NotNull(channel.LatestFailureAtUtc);
    }

    [Fact]
    public async Task Provider_diagnostics_activity_should_only_return_email_notifications_for_selected_tenant()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var dbContext = CreateDbContext(nameof(Provider_diagnostics_activity_should_only_return_email_notifications_for_selected_tenant), tenantId: null);

        var selectedNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Alert",
            templateModelJson: "{}",
            subject: "Alert");
        SetProperty(selectedNotification, "CreationTime", now.AddHours(-1));
        var selectedRecipient = new NotificationRecipient(
            id: 1,
            tenantId: tenantId,
            notificationId: selectedNotification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "security@example.test");
        var selectedDelivery = new NotificationDelivery(
            id: 1,
            tenantId: tenantId,
            notificationId: selectedNotification.Id,
            recipientId: selectedRecipient.Id);
        SetProperty(selectedDelivery, "CreationTime", now.AddHours(-1));
        selectedDelivery.MarkProcessing(now.AddHours(-1), now.AddMinutes(30));
        selectedDelivery.MarkFailed("Bounce", now.AddHours(1));

        var ignoredSmsNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Sms,
            templateName: "Tenant.Sms",
            templateModelJson: "{}",
            subject: "SMS");
        var ignoredSmsRecipient = new NotificationRecipient(
            id: 2,
            tenantId: tenantId,
            notificationId: ignoredSmsNotification.Id,
            recipientType: NotificationRecipientType.PhoneNumber,
            recipientValue: "+911111111111");
        var ignoredSmsDelivery = new NotificationDelivery(
            id: 2,
            tenantId: tenantId,
            notificationId: ignoredSmsNotification.Id,
            recipientId: ignoredSmsRecipient.Id);
        ignoredSmsDelivery.MarkProcessing(now.AddMinutes(-20), now.AddMinutes(-15));
        ignoredSmsDelivery.MarkProviderAccepted(now.AddMinutes(-19), "provider-message-sms");

        var ignoredOtherTenantNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: otherTenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Other",
            templateModelJson: "{}",
            subject: "Other tenant");
        var ignoredOtherTenantRecipient = new NotificationRecipient(
            id: 3,
            tenantId: otherTenantId,
            notificationId: ignoredOtherTenantNotification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "other@example.test");
        var ignoredOtherTenantDelivery = new NotificationDelivery(
            id: 3,
            tenantId: otherTenantId,
            notificationId: ignoredOtherTenantNotification.Id,
            recipientId: ignoredOtherTenantRecipient.Id);
        ignoredOtherTenantDelivery.MarkProcessing(now.AddMinutes(-10), now.AddMinutes(-5));
        ignoredOtherTenantDelivery.MarkProviderAccepted(now.AddMinutes(-9), "provider-message-other");

        dbContext.Notifications.AddRange(selectedNotification, ignoredSmsNotification, ignoredOtherTenantNotification);
        dbContext.NotificationRecipients.AddRange(selectedRecipient, ignoredSmsRecipient, ignoredOtherTenantRecipient);
        dbContext.NotificationDeliveries.AddRange(selectedDelivery, ignoredSmsDelivery, ignoredOtherTenantDelivery);
        await dbContext.SaveChangesAsync();

        var service = new NotificationProviderDiagnosticsAppService(
            new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new TestFeatureChecker(NotificationsFeatures.Email),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(null),
            SharedOnlyTenantDatabases.FanOut());

        var page = await service.GetRecentActivityAsync(new GetNotificationProviderActivityInput
        {
            TenantId = tenantId,
            SkipCount = 0,
            MaxResultCount = 10
        });

        var item = Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(selectedNotification.Id, item.NotificationId);
        Assert.Equal("Tenant.Alert", item.TemplateName);
        Assert.Equal(1, item.FailedCount);
        Assert.Equal(0, item.SentCount);
        Assert.Equal("Bounce", item.LatestFailureError);
        Assert.NotNull(item.LatestAttemptAtUtc);
        Assert.NotNull(item.LatestFailureAtUtc);
    }

    [Fact]
    public async Task Provider_diagnostics_should_include_sms_when_email_is_disabled_and_sms_is_enabled()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var dbContext = CreateDbContext(nameof(Provider_diagnostics_should_include_sms_when_email_is_disabled_and_sms_is_enabled), tenantId);

        var emailNotification = new Notification(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), NotificationChannel.Email, "Tenant.Email", "{}", "Email");
        var emailRecipient = new NotificationRecipient(
            501, tenantId, emailNotification.Id, NotificationRecipientType.EmailAddress, "email@example.test");
        var emailDelivery = new NotificationDelivery(501, tenantId, emailNotification.Id, emailRecipient.Id);
        emailDelivery.MarkProcessing(now.AddMinutes(-4), now.AddMinutes(-3));
        emailDelivery.MarkProviderAccepted(now.AddMinutes(-2), "email-message");

        var smsNotification = new Notification(
            Guid.NewGuid(), tenantId, Guid.NewGuid(), NotificationChannel.Sms, "Tenant.Sms", "{}", "SMS");
        var smsRecipient = new NotificationRecipient(
            502, tenantId, smsNotification.Id, NotificationRecipientType.PhoneNumber, "+919999999999");
        var smsDelivery = new NotificationDelivery(502, tenantId, smsNotification.Id, smsRecipient.Id);
        smsDelivery.MarkProcessing(now.AddMinutes(-4), now.AddMinutes(-3));
        smsDelivery.MarkProviderAccepted(now.AddMinutes(-2), "sms-message");

        dbContext.Notifications.AddRange(emailNotification, smsNotification);
        dbContext.NotificationRecipients.AddRange(emailRecipient, smsRecipient);
        dbContext.NotificationDeliveries.AddRange(emailDelivery, smsDelivery);
        await dbContext.SaveChangesAsync();

        var service = new NotificationProviderDiagnosticsAppService(
            new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new TestFeatureChecker(NotificationsFeatures.Sms),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(null),
            SharedOnlyTenantDatabases.FanOut());

        var activity = await service.GetRecentActivityAsync(new GetNotificationProviderActivityInput
        {
            TenantId = tenantId,
            MaxResultCount = 10
        });
        var activityItem = Assert.Single(activity.Items);
        Assert.Equal(NotificationChannel.Sms, activityItem.Channel);
        Assert.Equal(smsNotification.Id, activityItem.NotificationId);

        var analytics = await service.GetAnalyticsAsync(new GetNotificationProviderAnalyticsInput
        {
            TenantId = tenantId
        });
        Assert.Equal(1, analytics.TotalNotificationCount);
        Assert.Equal(1, analytics.TotalDeliveryCount);
        Assert.Equal(1, analytics.SentCount);
        Assert.Equal(1, analytics.AffectedProviderCount);

        var disabledEmailAnalytics = await service.GetAnalyticsAsync(new GetNotificationProviderAnalyticsInput
        {
            TenantId = tenantId,
            Channel = NotificationChannel.Email
        });
        Assert.Equal(0, disabledEmailAnalytics.TotalDeliveryCount);

        var unknownProviderAnalytics = await service.GetAnalyticsAsync(new GetNotificationProviderAnalyticsInput
        {
            TenantId = tenantId,
            Provider = "unknown-provider"
        });
        Assert.Equal(0, unknownProviderAnalytics.TotalDeliveryCount);

        var summary = await service.GetSummaryAsync(tenantId);
        var channel = Assert.Single(summary.Channels);
        Assert.Equal(NotificationChannel.Sms, channel.Channel);
        Assert.Equal(NotificationProviderHealthStatus.NotConfigured, channel.HealthStatus);
    }

    [Fact]
    public async Task Provider_diagnostics_activity_should_apply_activity_filters_and_status_summary()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var dbContext = CreateDbContext(nameof(Provider_diagnostics_activity_should_apply_activity_filters_and_status_summary), tenantId);

        var matchingNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Security.Alert",
            templateModelJson: "{}",
            subject: "Security provider alert");
        SetProperty(matchingNotification, "CreationTime", now.AddHours(-2));
        var matchingRecipient = new NotificationRecipient(
            id: 10,
            tenantId: tenantId,
            notificationId: matchingNotification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "security@example.test");
        var matchingDelivery = new NotificationDelivery(
            id: 10,
            tenantId: tenantId,
            notificationId: matchingNotification.Id,
            recipientId: matchingRecipient.Id);
        matchingDelivery.MarkProcessing(now.AddHours(-2), now.AddHours(-1));
        matchingDelivery.MarkFailed("Provider rejected security alert", now.AddMinutes(15));

        var ignoredTemplateNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Welcome",
            templateModelJson: "{}",
            subject: "Welcome");
        SetProperty(ignoredTemplateNotification, "CreationTime", now.AddHours(-1));
        var ignoredTemplateRecipient = new NotificationRecipient(
            id: 11,
            tenantId: tenantId,
            notificationId: ignoredTemplateNotification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "welcome@example.test");
        var ignoredTemplateDelivery = new NotificationDelivery(
            id: 11,
            tenantId: tenantId,
            notificationId: ignoredTemplateNotification.Id,
            recipientId: ignoredTemplateRecipient.Id);
        ignoredTemplateDelivery.MarkProcessing(now.AddHours(-1), now);
        ignoredTemplateDelivery.MarkProviderAccepted(now.AddMinutes(-30), "provider-message-welcome");

        var ignoredOldNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Security.Alert",
            templateModelJson: "{}",
            subject: "Old security alert");
        SetProperty(ignoredOldNotification, "CreationTime", now.AddDays(-10));
        var ignoredOldRecipient = new NotificationRecipient(
            id: 12,
            tenantId: tenantId,
            notificationId: ignoredOldNotification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "old@example.test");
        var ignoredOldDelivery = new NotificationDelivery(
            id: 12,
            tenantId: tenantId,
            notificationId: ignoredOldNotification.Id,
            recipientId: ignoredOldRecipient.Id);
        ignoredOldDelivery.MarkProcessing(now.AddDays(-10), now.AddDays(-9));
        ignoredOldDelivery.MarkFailed("Old provider error", now.AddDays(-9));

        dbContext.Notifications.AddRange(matchingNotification, ignoredTemplateNotification, ignoredOldNotification);
        dbContext.NotificationRecipients.AddRange(matchingRecipient, ignoredTemplateRecipient, ignoredOldRecipient);
        dbContext.NotificationDeliveries.AddRange(matchingDelivery, ignoredTemplateDelivery, ignoredOldDelivery);
        await dbContext.SaveChangesAsync();

        var service = new NotificationProviderDiagnosticsAppService(
            new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new TestFeatureChecker(NotificationsFeatures.Email),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(null),
            SharedOnlyTenantDatabases.FanOut());

        var page = await service.GetRecentActivityAsync(new GetNotificationProviderActivityInput
        {
            TenantId = tenantId,
            Status = NotificationStatus.Failed,
            SourceType = NotificationProviderActivitySourceType.Runtime,
            TemplateName = "Security",
            Filter = "provider alert",
            FromUtc = now.AddDays(-1),
            ToUtc = now.AddMinutes(1),
            SkipCount = 0,
            MaxResultCount = 10
        });

        var item = Assert.Single(page.Items);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(matchingNotification.Id, item.NotificationId);
        Assert.Equal(NotificationProviderActivitySourceType.Runtime, item.SourceType);
        Assert.Equal(NotificationStatus.Failed, item.Status);
        Assert.Equal("Provider rejected security alert", item.LatestFailureError);
    }

    [Fact]
    public async Task Provider_analytics_should_aggregate_email_delivery_reliability_for_selected_context()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var dbContext = CreateDbContext(nameof(Provider_analytics_should_aggregate_email_delivery_reliability_for_selected_context), tenantId: null);

        var config = new NotificationProviderConfig(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            channel: NotificationChannel.Email,
            providerType: "Smtp",
            name: "Primary",
            isActive: true,
            isDefault: true,
            priority: 0,
            settingsJson: EmailProviderConfigAppService.SerializeSettings("smtp.analytics.local", 587, "analytics-user", "alerts@example.test", "TenantWright Alerts", "StartTls"),
            complianceContact: null);

        var sentNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Welcome",
            templateModelJson: "{}",
            subject: "Welcome");
        SetProperty(sentNotification, "CreationTime", now.AddHours(-4));
        var sentRecipient = new NotificationRecipient(
            id: 100,
            tenantId: tenantId,
            notificationId: sentNotification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "welcome@example.test");
        var sentDelivery = new NotificationDelivery(
            id: 100,
            tenantId: tenantId,
            notificationId: sentNotification.Id,
            recipientId: sentRecipient.Id);
        sentDelivery.MarkProcessing(now.AddHours(-4), now.AddHours(-3));
        sentDelivery.MarkProviderAccepted(now.AddHours(-3), "provider-sent");

        var failedNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Security.Alert",
            templateModelJson: "{}",
            subject: "Security alert");
        SetProperty(failedNotification, "CreationTime", now.AddHours(-2));
        var failedRecipient = new NotificationRecipient(
            id: 101,
            tenantId: tenantId,
            notificationId: failedNotification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "security@example.test");
        var failedDelivery = new NotificationDelivery(
            id: 101,
            tenantId: tenantId,
            notificationId: failedNotification.Id,
            recipientId: failedRecipient.Id);
        failedDelivery.MarkProcessing(now.AddHours(-2), now.AddHours(-1));
        failedDelivery.MarkProcessing(now.AddHours(-1), now.AddMinutes(-30));
        failedDelivery.MarkFailed("SMTP 550 Mailbox rejected", now.AddMinutes(30));

        var ignoredSmsNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Sms,
            templateName: "Tenant.Sms",
            templateModelJson: "{}",
            subject: "SMS");
        var ignoredSmsRecipient = new NotificationRecipient(
            id: 102,
            tenantId: tenantId,
            notificationId: ignoredSmsNotification.Id,
            recipientType: NotificationRecipientType.PhoneNumber,
            recipientValue: "+911111111111");
        var ignoredSmsDelivery = new NotificationDelivery(
            id: 102,
            tenantId: tenantId,
            notificationId: ignoredSmsNotification.Id,
            recipientId: ignoredSmsRecipient.Id);
        ignoredSmsDelivery.MarkProcessing(now.AddHours(-1), now);
        ignoredSmsDelivery.MarkProviderAccepted(now, "provider-sms");

        var ignoredOtherTenantNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: otherTenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Other",
            templateModelJson: "{}",
            subject: "Other tenant");
        var ignoredOtherTenantRecipient = new NotificationRecipient(
            id: 103,
            tenantId: otherTenantId,
            notificationId: ignoredOtherTenantNotification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "other@example.test");
        var ignoredOtherTenantDelivery = new NotificationDelivery(
            id: 103,
            tenantId: otherTenantId,
            notificationId: ignoredOtherTenantNotification.Id,
            recipientId: ignoredOtherTenantRecipient.Id);
        ignoredOtherTenantDelivery.MarkProcessing(now.AddHours(-1), now);
        ignoredOtherTenantDelivery.MarkFailed("Other tenant failure", now);

        dbContext.NotificationProviderConfigs.Add(config);
        dbContext.Notifications.AddRange(sentNotification, failedNotification, ignoredSmsNotification, ignoredOtherTenantNotification);
        dbContext.NotificationRecipients.AddRange(sentRecipient, failedRecipient, ignoredSmsRecipient, ignoredOtherTenantRecipient);
        dbContext.NotificationDeliveries.AddRange(sentDelivery, failedDelivery, ignoredSmsDelivery, ignoredOtherTenantDelivery);
        await dbContext.SaveChangesAsync();

        var service = new NotificationProviderDiagnosticsAppService(
            new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new TestFeatureChecker(NotificationsFeatures.Email),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(null),
            SharedOnlyTenantDatabases.FanOut());

        var analytics = await service.GetAnalyticsAsync(new GetNotificationProviderAnalyticsInput
        {
            TenantId = tenantId,
            Channel = NotificationChannel.Email,
            Provider = "smtp.analytics",
            FromUtc = now.AddDays(-1),
            ToUtc = now.AddMinutes(1)
        });

        Assert.Equal(tenantId, analytics.TenantId);
        Assert.Equal(2, analytics.TotalDeliveryCount);
        Assert.Equal(2, analytics.TotalNotificationCount);
        Assert.Equal(1, analytics.SentCount);
        Assert.Equal(1, analytics.FailedCount);
        Assert.Equal(50, analytics.SuccessRate);
        Assert.Equal(50, analytics.FailureRate);
        Assert.Equal(50, analytics.RetryRate);
        Assert.Equal(2, analytics.AffectedTemplateCount);
        Assert.Equal(1, analytics.AffectedProviderCount);
        Assert.Contains(analytics.StatusMix, x => x.Status == NotificationStatus.ProviderAccepted && x.Count == 1);
        Assert.Contains(analytics.StatusMix, x => x.Status == NotificationStatus.Failed && x.Count == 1);
        Assert.Contains(analytics.TopFailingTemplates, x => x.Key == "Tenant.Security.Alert" && x.FailedCount == 1);
        Assert.Contains(analytics.TopFailingProviders, x => x.Key == "smtp.analytics.local" && x.FailedCount == 1);
        Assert.Contains(analytics.TopErrorCategories, x => x.Key == "SMTP 550 Mailbox rejected" && x.FailedCount == 1);
    }

    [Fact]
    public async Task Provider_analytics_should_apply_status_template_search_and_date_filters()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var dbContext = CreateDbContext(nameof(Provider_analytics_should_apply_status_template_search_and_date_filters), tenantId);

        var matchingNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Security.Alert",
            templateModelJson: "{}",
            subject: "Provider outage alert");
        SetProperty(matchingNotification, "CreationTime", now.AddHours(-2));
        var matchingRecipient = new NotificationRecipient(201, tenantId, matchingNotification.Id, NotificationRecipientType.EmailAddress, "security@example.test");
        var matchingDelivery = new NotificationDelivery(201, tenantId, matchingNotification.Id, matchingRecipient.Id);
        matchingDelivery.MarkProcessing(now.AddHours(-2), now.AddHours(-1));
        matchingDelivery.MarkFailed("Provider outage", now.AddMinutes(10));

        var ignoredSentNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Security.Alert",
            templateModelJson: "{}",
            subject: "Provider outage alert");
        SetProperty(ignoredSentNotification, "CreationTime", now.AddHours(-1));
        var ignoredSentRecipient = new NotificationRecipient(202, tenantId, ignoredSentNotification.Id, NotificationRecipientType.EmailAddress, "sent@example.test");
        var ignoredSentDelivery = new NotificationDelivery(202, tenantId, ignoredSentNotification.Id, ignoredSentRecipient.Id);
        ignoredSentDelivery.MarkProcessing(now.AddHours(-1), now);
        ignoredSentDelivery.MarkProviderAccepted(now, "provider-sent");

        var ignoredOldNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Security.Alert",
            templateModelJson: "{}",
            subject: "Provider outage alert");
        SetProperty(ignoredOldNotification, "CreationTime", now.AddDays(-5));
        var ignoredOldRecipient = new NotificationRecipient(203, tenantId, ignoredOldNotification.Id, NotificationRecipientType.EmailAddress, "old@example.test");
        var ignoredOldDelivery = new NotificationDelivery(203, tenantId, ignoredOldNotification.Id, ignoredOldRecipient.Id);
        ignoredOldDelivery.MarkProcessing(now.AddDays(-5), now.AddDays(-4));
        ignoredOldDelivery.MarkFailed("Old provider outage", now.AddDays(-4));

        dbContext.Notifications.AddRange(matchingNotification, ignoredSentNotification, ignoredOldNotification);
        dbContext.NotificationRecipients.AddRange(matchingRecipient, ignoredSentRecipient, ignoredOldRecipient);
        dbContext.NotificationDeliveries.AddRange(matchingDelivery, ignoredSentDelivery, ignoredOldDelivery);
        await dbContext.SaveChangesAsync();

        var service = new NotificationProviderDiagnosticsAppService(
            new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new TestFeatureChecker(NotificationsFeatures.Email),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(null),
            SharedOnlyTenantDatabases.FanOut());

        var analytics = await service.GetAnalyticsAsync(new GetNotificationProviderAnalyticsInput
        {
            TenantId = tenantId,
            Status = NotificationStatus.Failed,
            TemplateName = "Security",
            Filter = "outage",
            FromUtc = now.AddDays(-1),
            ToUtc = now.AddMinutes(1)
        });

        Assert.Equal(1, analytics.TotalDeliveryCount);
        Assert.Equal(1, analytics.FailedCount);
        Assert.Equal(0, analytics.SentCount);
        Assert.Single(analytics.DeliveryVolume);
        Assert.Contains(analytics.TopErrorCategories, x => x.Key == "Provider outage" && x.FailedCount == 1);
    }

    [Fact]
    public async Task Current_tenant_provider_analytics_should_use_authenticated_tenant_only()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var dbContext = CreateDbContext(nameof(Current_tenant_provider_analytics_should_use_authenticated_tenant_only), tenantId: null);

        var currentNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Analytics.Alert",
            templateModelJson: "{}",
            subject: "Tenant analytics outage");
        SetProperty(currentNotification, "CreationTime", now.AddHours(-2));
        var currentRecipient = new NotificationRecipient(401, tenantId, currentNotification.Id, NotificationRecipientType.EmailAddress, "tenant@example.test");
        var currentDelivery = new NotificationDelivery(401, tenantId, currentNotification.Id, currentRecipient.Id);
        currentDelivery.MarkProcessing(now.AddHours(-2), now.AddHours(-1));
        currentDelivery.MarkFailed("Tenant SMTP failure", now.AddMinutes(-30));

        var otherTenantNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: otherTenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Analytics.Other",
            templateModelJson: "{}",
            subject: "Other tenant analytics outage");
        SetProperty(otherTenantNotification, "CreationTime", now.AddHours(-1));
        var otherTenantRecipient = new NotificationRecipient(402, otherTenantId, otherTenantNotification.Id, NotificationRecipientType.EmailAddress, "other@example.test");
        var otherTenantDelivery = new NotificationDelivery(402, otherTenantId, otherTenantNotification.Id, otherTenantRecipient.Id);
        otherTenantDelivery.MarkProcessing(now.AddHours(-1), now.AddMinutes(-50));
        otherTenantDelivery.MarkFailed("Other tenant failure", now.AddMinutes(-40));

        dbContext.Notifications.AddRange(currentNotification, otherTenantNotification);
        dbContext.NotificationRecipients.AddRange(currentRecipient, otherTenantRecipient);
        dbContext.NotificationDeliveries.AddRange(currentDelivery, otherTenantDelivery);
        await dbContext.SaveChangesAsync();

        var service = new NotificationProviderDiagnosticsAppService(
            new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new TestFeatureChecker(NotificationsFeatures.Email),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(tenantId),
            SharedOnlyTenantDatabases.FanOut());

        var analytics = await service.GetCurrentTenantAnalyticsAsync(new GetCurrentTenantNotificationProviderAnalyticsInput
        {
            Status = NotificationStatus.Failed,
            TemplateName = "Analytics",
            Filter = "outage",
            FromUtc = now.AddDays(-1),
            ToUtc = now.AddMinutes(1)
        });

        Assert.Equal(tenantId, analytics.TenantId);
        Assert.Equal(1, analytics.TotalDeliveryCount);
        Assert.Equal(1, analytics.FailedCount);
        Assert.Contains(analytics.TopFailingTemplates, x => x.Key == "Tenant.Analytics.Alert");
        Assert.DoesNotContain(analytics.TopFailingTemplates, x => x.Key == "Tenant.Analytics.Other");
    }

    [Fact]
    public async Task Current_tenant_provider_analytics_should_fail_without_tenant_context()
    {
        var dbContext = CreateDbContext(nameof(Current_tenant_provider_analytics_should_fail_without_tenant_context), tenantId: null);
        var service = new NotificationProviderDiagnosticsAppService(
            new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new TestFeatureChecker(NotificationsFeatures.Email),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(null),
            SharedOnlyTenantDatabases.FanOut());

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.GetCurrentTenantAnalyticsAsync(new GetCurrentTenantNotificationProviderAnalyticsInput()));

        Assert.Contains("active tenant", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Current_tenant_provider_analytics_export_should_use_authenticated_tenant_only()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var dbContext = CreateDbContext(nameof(Current_tenant_provider_analytics_export_should_use_authenticated_tenant_only), tenantId: null);

        var currentNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Export.Alert",
            templateModelJson: "{}",
            subject: "Tenant export failure");
        SetProperty(currentNotification, "CreationTime", now.AddMinutes(-15));
        var currentRecipient = new NotificationRecipient(451, tenantId, currentNotification.Id, NotificationRecipientType.EmailAddress, "tenant-export@example.test");
        var currentDelivery = new NotificationDelivery(451, tenantId, currentNotification.Id, currentRecipient.Id);
        currentDelivery.MarkProcessing(now.AddMinutes(-15), now.AddMinutes(-14));
        currentDelivery.MarkFailed("Tenant export SMTP failure", now.AddMinutes(-13));

        var otherTenantNotification = new Notification(
            id: Guid.NewGuid(),
            tenantId: otherTenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Export.Other",
            templateModelJson: "{}",
            subject: "Other tenant export failure");
        SetProperty(otherTenantNotification, "CreationTime", now.AddMinutes(-10));
        var otherTenantRecipient = new NotificationRecipient(452, otherTenantId, otherTenantNotification.Id, NotificationRecipientType.EmailAddress, "other-export@example.test");
        var otherTenantDelivery = new NotificationDelivery(452, otherTenantId, otherTenantNotification.Id, otherTenantRecipient.Id);
        otherTenantDelivery.MarkProcessing(now.AddMinutes(-10), now.AddMinutes(-9));
        otherTenantDelivery.MarkFailed("Other tenant export failure", now.AddMinutes(-8));

        dbContext.Notifications.AddRange(currentNotification, otherTenantNotification);
        dbContext.NotificationRecipients.AddRange(currentRecipient, otherTenantRecipient);
        dbContext.NotificationDeliveries.AddRange(currentDelivery, otherTenantDelivery);
        await dbContext.SaveChangesAsync();

        var service = new NotificationProviderDiagnosticsAppService(
            new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new TestFeatureChecker(NotificationsFeatures.Email),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(tenantId),
            SharedOnlyTenantDatabases.FanOut());

        var jsonExport = await service.ExportCurrentTenantAnalyticsAsync(new GetCurrentTenantNotificationProviderAnalyticsExportInput
        {
            Status = NotificationStatus.Failed,
            TemplateName = "Export",
            Filter = "failure",
            FromUtc = now.AddHours(-1),
            ToUtc = now.AddMinutes(1),
            Format = NotificationProviderAnalyticsExportFormat.Json
        });

        Assert.Equal(NotificationProviderAnalyticsExportFormat.Json, jsonExport.Format);
        Assert.Equal("application/json", jsonExport.ContentType);
        Assert.Equal(tenantId, jsonExport.FilterSummary.TenantId);
        Assert.Null(jsonExport.FilterSummary.Channel);
        Assert.Null(jsonExport.FilterSummary.Provider);
        Assert.Contains("Tenant.Export.Alert", jsonExport.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Tenant.Export.Other", jsonExport.Content, StringComparison.Ordinal);

        var csvExport = await service.ExportCurrentTenantAnalyticsAsync(new GetCurrentTenantNotificationProviderAnalyticsExportInput
        {
            Status = NotificationStatus.Failed,
            TemplateName = "Export",
            Filter = "failure",
            FromUtc = now.AddHours(-1),
            ToUtc = now.AddMinutes(1),
            Format = NotificationProviderAnalyticsExportFormat.Csv
        });

        Assert.Equal(NotificationProviderAnalyticsExportFormat.Csv, csvExport.Format);
        Assert.Equal("text/csv", csvExport.ContentType);
        Assert.Contains("KPI,Total deliveries,1", csvExport.Content, StringComparison.Ordinal);
        Assert.Contains("Top failing templates,Tenant.Export.Alert", csvExport.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Tenant.Export.Other", csvExport.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Current_tenant_provider_analytics_export_should_fail_without_tenant_context()
    {
        var dbContext = CreateDbContext(nameof(Current_tenant_provider_analytics_export_should_fail_without_tenant_context), tenantId: null);
        var service = new NotificationProviderDiagnosticsAppService(
            new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new TestFeatureChecker(NotificationsFeatures.Email),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(null),
            SharedOnlyTenantDatabases.FanOut());

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ExportCurrentTenantAnalyticsAsync(new GetCurrentTenantNotificationProviderAnalyticsExportInput()));

        Assert.Contains("active tenant", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_analytics_export_should_return_json_and_csv_for_current_filters()
    {
        var tenantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var dbContext = CreateDbContext(nameof(Provider_analytics_export_should_return_json_and_csv_for_current_filters), tenantId);

        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Tenant.Security.Alert",
            templateModelJson: "{}",
            subject: "Provider export alert");
        SetProperty(notification, "CreationTime", now.AddMinutes(-10));
        var recipient = new NotificationRecipient(301, tenantId, notification.Id, NotificationRecipientType.EmailAddress, "ops@example.test");
        var delivery = new NotificationDelivery(301, tenantId, notification.Id, recipient.Id);
        delivery.MarkProcessing(now.AddMinutes(-10), now.AddMinutes(-9));
        delivery.MarkFailed("SMTP 550 export failure", now.AddMinutes(-8));

        dbContext.Notifications.Add(notification);
        dbContext.NotificationRecipients.Add(recipient);
        dbContext.NotificationDeliveries.Add(delivery);
        await dbContext.SaveChangesAsync();

        var service = new NotificationProviderDiagnosticsAppService(
            new EfTestRepository<NotificationProviderConfig, Guid>(dbContext),
            new EfTestRepository<Notification, Guid>(dbContext),
            new EfTestRepository<NotificationDelivery, long>(dbContext),
            new TestFeatureChecker(NotificationsFeatures.Email),
            new ConfigurableDataFilter(enabled: false),
            new TestCurrentTenant(null),
            SharedOnlyTenantDatabases.FanOut());

        var jsonExport = await service.ExportAnalyticsAsync(new GetNotificationProviderAnalyticsExportInput
        {
            TenantId = tenantId,
            Status = NotificationStatus.Failed,
            TemplateName = "Security",
            Filter = "export",
            FromUtc = now.AddHours(-1),
            ToUtc = now.AddMinutes(1),
            Format = NotificationProviderAnalyticsExportFormat.Json
        });

        Assert.Equal(NotificationProviderAnalyticsExportFormat.Json, jsonExport.Format);
        Assert.Equal("application/json", jsonExport.ContentType);
        Assert.EndsWith(".json", jsonExport.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(tenantId, jsonExport.FilterSummary.TenantId);
        Assert.Contains("\"totalDeliveryCount\": 1", jsonExport.Content, StringComparison.Ordinal);
        Assert.Contains("\"statusMix\"", jsonExport.Content, StringComparison.Ordinal);
        Assert.Contains("Tenant.Security.Alert", jsonExport.Content, StringComparison.Ordinal);

        var csvExport = await service.ExportAnalyticsAsync(new GetNotificationProviderAnalyticsExportInput
        {
            TenantId = tenantId,
            Status = NotificationStatus.Failed,
            TemplateName = "Security",
            Filter = "export",
            FromUtc = now.AddHours(-1),
            ToUtc = now.AddMinutes(1),
            Format = NotificationProviderAnalyticsExportFormat.Csv
        });

        Assert.Equal(NotificationProviderAnalyticsExportFormat.Csv, csvExport.Format);
        Assert.Equal("text/csv", csvExport.ContentType);
        Assert.EndsWith(".csv", csvExport.FileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Section,Key,Value,Count,Percentage,Total,Sent,Failed,Retried", csvExport.Content, StringComparison.Ordinal);
        Assert.Contains("KPI,Total deliveries,1", csvExport.Content, StringComparison.Ordinal);
        Assert.Contains("Top failing templates,Tenant.Security.Alert", csvExport.Content, StringComparison.Ordinal);
        Assert.Contains("Top error categories,SMTP 550 export failure", csvExport.Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Presence_query_should_summarize_and_filter_observed_users()
    {
        var tenantId = Guid.NewGuid();
        var otherTenantId = Guid.NewGuid();
        var now = DateTime.UtcNow;
        var dbContext = CreateDbContext(nameof(Presence_query_should_summarize_and_filter_observed_users), tenantId: null);

        dbContext.NotificationPresenceRecords.AddRange(
            new NotificationPresenceRecord(Guid.NewGuid(), tenantId, "active-user", now.AddSeconds(-30)),
            new NotificationPresenceRecord(Guid.NewGuid(), tenantId, "stale-user", now.AddMinutes(-10)),
            new NotificationPresenceRecord(Guid.NewGuid(), tenantId, "inactive-user", now.AddHours(-2)),
            new NotificationPresenceRecord(Guid.NewGuid(), otherTenantId, "other-tenant-user", now.AddSeconds(-10)));
        await dbContext.SaveChangesAsync();

        var service = new NotificationPresenceQueryAppService(
            new EfTestRepository<NotificationPresenceRecord, Guid>(dbContext),
            Options.Create(new NotificationsPresenceOptions
            {
                Enabled = true,
                TtlSeconds = 60
            }),
            new TestCurrentTenant(null));

        var summary = await service.GetSummaryAsync(new GetNotificationPresenceInput
        {
            TenantId = tenantId,
            StaleAfterMinutes = 30
        });

        Assert.Equal(1, summary.ActiveCount);
        Assert.Equal(1, summary.StaleCount);
        Assert.Equal(1, summary.InactiveCount);
        Assert.Equal(3, summary.TotalCount);
        Assert.Equal(60, summary.TtlSeconds);
        Assert.Equal(30, summary.StaleAfterMinutes);

        var stalePage = await service.GetListAsync(new GetNotificationPresenceInput
        {
            TenantId = tenantId,
            Status = NotificationPresenceStatus.Stale,
            StaleAfterMinutes = 30,
            SkipCount = 0,
            MaxResultCount = 10
        });

        var stale = Assert.Single(stalePage.Items);
        Assert.Equal("stale-user", stale.UserId);
        Assert.Equal(NotificationPresenceStatus.Stale, stale.Status);
        Assert.Equal(stale.LastSeenAtUtc.AddSeconds(60), stale.ExpiresAtUtc);
        Assert.True(stale.HeartbeatAgeSeconds >= 0);

        var filteredPage = await service.GetListAsync(new GetNotificationPresenceInput
        {
            TenantId = tenantId,
            Filter = "inactive",
            StaleAfterMinutes = 30,
            SkipCount = 0,
            MaxResultCount = 10
        });

        var filtered = Assert.Single(filteredPage.Items);
        Assert.Equal("inactive-user", filtered.UserId);
        Assert.Equal(NotificationPresenceStatus.Inactive, filtered.Status);
    }

    [Fact]
    public async Task Presence_query_should_fail_closed_for_cross_tenant_requests()
    {
        var currentTenantId = Guid.NewGuid();
        var requestedTenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Presence_query_should_fail_closed_for_cross_tenant_requests), currentTenantId);
        var service = new NotificationPresenceQueryAppService(
            new EfTestRepository<NotificationPresenceRecord, Guid>(dbContext),
            Options.Create(new NotificationsPresenceOptions()),
            new TestCurrentTenant(currentTenantId));

        var exception = await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.GetListAsync(new GetNotificationPresenceInput
        {
            TenantId = requestedTenantId
        }));

        Assert.Contains("Cross-tenant", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Presence_store_should_keep_boolean_check_and_upsert_observed_record()
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(nameof(Presence_store_should_keep_boolean_check_and_upsert_observed_record), tenantId);
        var store = new DistributedNotificationsPresenceStore(
            new TestDistributedCache(),
            Options.Create(new NotificationsPresenceOptions
            {
                Enabled = true,
                TtlSeconds = 60
            }),
            new EfTestRepository<NotificationPresenceRecord, Guid>(dbContext));

        await store.SetActiveAsync(tenantId, "u1");
        var firstSeenAt = await dbContext.NotificationPresenceRecords
            .Where(x => x.TenantId == tenantId && x.UserId == "u1")
            .Select(x => x.LastSeenAtUtc)
            .SingleAsync();

        await store.SetActiveAsync(tenantId, "u1");

        Assert.True(await store.IsActiveAsync(tenantId, "u1"));
        var record = await dbContext.NotificationPresenceRecords.SingleAsync(x => x.TenantId == tenantId && x.UserId == "u1");
        Assert.Equal("u1", record.UserId);
        Assert.True(record.LastSeenAtUtc >= firstSeenAt);
        Assert.Equal(1, await dbContext.NotificationPresenceRecords.CountAsync(x => x.TenantId == tenantId && x.UserId == "u1"));
    }

    [Fact]
    public void Email_provider_config_controller_should_expose_test_send_endpoint_with_test_send_policy()
    {
        var action = typeof(EmailProviderConfigController).GetMethod(nameof(EmailProviderConfigController.TestSendAsync));
        Assert.NotNull(action);

        var route = action!.GetCustomAttribute<HttpPostAttribute>();
        Assert.Equal("{id:guid}/test-send", route?.Template);

        var authorize = action.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault();
        Assert.Equal(NotificationsPermissions.Config.TestSend, authorize?.Policy);

        Assert.NotNull(action.GetCustomAttribute<AuditedAttribute>(inherit: true));
    }

    [Fact]
    public void Notification_providers_controller_should_expose_summary_and_activity_endpoints_with_providers_read_policy()
    {
        var summaryAction = typeof(NotificationProvidersController).GetMethod(nameof(NotificationProvidersController.GetSummaryAsync));
        Assert.NotNull(summaryAction);
        Assert.Equal("summary", summaryAction!.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Providers.Read,
            summaryAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);

        var activityAction = typeof(NotificationProvidersController).GetMethod(nameof(NotificationProvidersController.GetActivityAsync));
        Assert.NotNull(activityAction);
        Assert.Equal("activity", activityAction!.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Providers.ActivityRead,
            activityAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);

        var analyticsAction = typeof(NotificationProvidersController).GetMethod(nameof(NotificationProvidersController.GetAnalyticsAsync));
        Assert.NotNull(analyticsAction);
        Assert.Equal("analytics", analyticsAction!.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Providers.AnalyticsRead,
            analyticsAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);

        var currentTenantAnalyticsAction = typeof(NotificationProvidersController).GetMethod(nameof(NotificationProvidersController.GetCurrentTenantAnalyticsAsync));
        Assert.NotNull(currentTenantAnalyticsAction);
        Assert.Equal("current-tenant/analytics", currentTenantAnalyticsAction!.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Providers.AnalyticsRead,
            currentTenantAnalyticsAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotEqual(
            NotificationsPermissions.Query.Read,
            currentTenantAnalyticsAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);

        var analyticsExportAction = typeof(NotificationProvidersController).GetMethod(nameof(NotificationProvidersController.ExportAnalyticsAsync));
        Assert.NotNull(analyticsExportAction);
        Assert.Equal("analytics/export", analyticsExportAction!.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Providers.AnalyticsRead,
            analyticsExportAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);

        var currentTenantAnalyticsExportAction = typeof(NotificationProvidersController).GetMethod(nameof(NotificationProvidersController.ExportCurrentTenantAnalyticsAsync));
        Assert.NotNull(currentTenantAnalyticsExportAction);
        Assert.Equal("current-tenant/analytics/export", currentTenantAnalyticsExportAction!.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Providers.AnalyticsRead,
            currentTenantAnalyticsExportAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotEqual(
            NotificationsPermissions.Query.Read,
            currentTenantAnalyticsExportAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
    }

    [Fact]
    public void Presence_controller_should_expose_summary_and_observations_with_presence_read_policy()
    {
        var controllerAuthorize = typeof(PresenceController)
            .GetCustomAttributes<AuthorizeAttribute>(inherit: true)
            .SingleOrDefault();
        Assert.Equal(NotificationsPermissions.Presence.Read, controllerAuthorize?.Policy);

        var booleanAction = typeof(PresenceController).GetMethod(nameof(PresenceController.IsActiveAsync));
        Assert.NotNull(booleanAction);
        Assert.Null(booleanAction!.GetCustomAttribute<HttpGetAttribute>()?.Template);

        var summaryAction = typeof(PresenceController).GetMethod(nameof(PresenceController.GetSummaryAsync));
        Assert.NotNull(summaryAction);
        Assert.Equal("summary", summaryAction!.GetCustomAttribute<HttpGetAttribute>()?.Template);

        var observationsAction = typeof(PresenceController).GetMethod(nameof(PresenceController.GetObservationsAsync));
        Assert.NotNull(observationsAction);
        Assert.Equal("observations", observationsAction!.GetCustomAttribute<HttpGetAttribute>()?.Template);
    }

    [Fact]
    public void Notification_templates_controller_should_expose_manage_endpoints_with_expected_policies()
    {
        var createAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.CreateAsync));
        Assert.NotNull(createAction);
        Assert.Null(createAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Create,
            createAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(createAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var getManageAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.GetManageAsync));
        Assert.NotNull(getManageAction);
        Assert.Equal("{templateName}/manage", getManageAction!.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            getManageAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);

        var previewDraftAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.PreviewDraftAsync));
        Assert.NotNull(previewDraftAction);
        Assert.Equal("preview-draft", previewDraftAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            previewDraftAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);

        var validateDraftAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.ValidateDraftAsync));
        Assert.NotNull(validateDraftAction);
        Assert.Equal("validate-draft", validateDraftAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            validateDraftAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);

        var saveDraftAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.SaveDraftAsync));
        Assert.NotNull(saveDraftAction);
        Assert.Equal("draft", saveDraftAction!.GetCustomAttribute<HttpPutAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            saveDraftAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(saveDraftAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var publishAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.PublishDraftAsync));
        Assert.NotNull(publishAction);
        Assert.Equal("publish", publishAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Publish,
            publishAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(publishAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var republishAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.RepublishAsync));
        Assert.NotNull(republishAction);
        Assert.Equal("republish", republishAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Publish,
            republishAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(republishAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var renameAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.RenameAsync));
        Assert.NotNull(renameAction);
        Assert.Equal("rename", renameAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Rename,
            renameAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(renameAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var archiveAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.ArchiveAsync));
        Assert.NotNull(archiveAction);
        Assert.Equal("archive", archiveAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Archive,
            archiveAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(archiveAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var restoreAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.RestoreAsync));
        Assert.NotNull(restoreAction);
        Assert.Equal("restore", restoreAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Archive,
            restoreAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(restoreAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var deleteDraftAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.DeleteDraftAsync));
        Assert.NotNull(deleteDraftAction);
        Assert.Equal("delete-draft", deleteDraftAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.DeleteDraft,
            deleteDraftAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(deleteDraftAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var getSamplesAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.GetSamplesAsync));
        Assert.NotNull(getSamplesAction);
        Assert.Equal("{templateName}/samples", getSamplesAction!.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            getSamplesAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);

        var createSampleAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.CreateSampleAsync));
        Assert.NotNull(createSampleAction);
        Assert.Equal("samples", createSampleAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            createSampleAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(createSampleAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var updateSampleAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.UpdateSampleAsync));
        Assert.NotNull(updateSampleAction);
        Assert.Equal("samples/{id:guid}", updateSampleAction!.GetCustomAttribute<HttpPutAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            updateSampleAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(updateSampleAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var setDefaultSampleAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.SetDefaultSampleAsync));
        Assert.NotNull(setDefaultSampleAction);
        Assert.Equal("samples/{id:guid}/default", setDefaultSampleAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            setDefaultSampleAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(setDefaultSampleAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var deleteSampleAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.DeleteSampleAsync));
        Assert.NotNull(deleteSampleAction);
        Assert.Equal("samples/{id:guid}", deleteSampleAction!.GetCustomAttribute<HttpDeleteAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            deleteSampleAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(deleteSampleAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var getContentBlocksAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.GetContentBlocksAsync));
        Assert.NotNull(getContentBlocksAction);
        Assert.Equal("content-blocks", getContentBlocksAction!.GetCustomAttribute<HttpGetAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            getContentBlocksAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);

        var createContentBlockAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.CreateContentBlockAsync));
        Assert.NotNull(createContentBlockAction);
        Assert.Equal("content-blocks", createContentBlockAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            createContentBlockAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(createContentBlockAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var updateContentBlockAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.UpdateContentBlockAsync));
        Assert.NotNull(updateContentBlockAction);
        Assert.Equal("content-blocks/{id:guid}", updateContentBlockAction!.GetCustomAttribute<HttpPutAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            updateContentBlockAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(updateContentBlockAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var archiveContentBlockAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.ArchiveContentBlockAsync));
        Assert.NotNull(archiveContentBlockAction);
        Assert.Equal("content-blocks/{id:guid}/archive", archiveContentBlockAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            archiveContentBlockAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(archiveContentBlockAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var restoreContentBlockAction = typeof(NotificationTemplatesController).GetMethod(nameof(NotificationTemplatesController.RestoreContentBlockAsync));
        Assert.NotNull(restoreContentBlockAction);
        Assert.Equal("content-blocks/{id:guid}/restore", restoreContentBlockAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Templates.Manage,
            restoreContentBlockAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(restoreContentBlockAction.GetCustomAttribute<AuditedAttribute>(inherit: true));
    }

    [Fact]
    public void Notifications_controller_should_expose_failed_delivery_resend_endpoints_with_resend_policy()
    {
        var resendFailedAction = typeof(NotificationsController).GetMethod(nameof(NotificationsController.ResendFailedDeliveriesAsync));
        Assert.NotNull(resendFailedAction);
        Assert.Equal("{id:guid}/deliveries/resend-failed", resendFailedAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Deliveries.Resend,
            resendFailedAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(resendFailedAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var resendDeliveryAction = typeof(NotificationsController).GetMethod(nameof(NotificationsController.ResendDeliveryAsync));
        Assert.NotNull(resendDeliveryAction);
        Assert.Equal("deliveries/{deliveryId:long}/resend", resendDeliveryAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Deliveries.Resend,
            resendDeliveryAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(resendDeliveryAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var previewOverrideAction = typeof(NotificationsController).GetMethod(nameof(NotificationsController.PreviewDeliveryOverrideResendAsync));
        Assert.NotNull(previewOverrideAction);
        Assert.Equal("deliveries/{deliveryId:long}/override-preview", previewOverrideAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Deliveries.OverrideResend,
            previewOverrideAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);

        var resendOverrideAction = typeof(NotificationsController).GetMethod(nameof(NotificationsController.ResendDeliveryOverrideAsync));
        Assert.NotNull(resendOverrideAction);
        Assert.Equal("deliveries/{deliveryId:long}/override-resend", resendOverrideAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Deliveries.OverrideResend,
            resendOverrideAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(resendOverrideAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var confirmAction = typeof(NotificationsController).GetMethod(nameof(NotificationsController.ConfirmDeliveryAsync));
        Assert.NotNull(confirmAction);
        Assert.Equal("deliveries/{deliveryId:long}/review/confirm", confirmAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Deliveries.Resend,
            confirmAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(confirmAction.GetCustomAttribute<AuditedAttribute>(inherit: true));

        var retryAction = typeof(NotificationsController).GetMethod(nameof(NotificationsController.RetryDeliveryAsync));
        Assert.NotNull(retryAction);
        Assert.Equal("deliveries/{deliveryId:long}/review/retry", retryAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Deliveries.Resend,
            retryAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(retryAction.GetCustomAttribute<AuditedAttribute>(inherit: true));
    }

    [Fact]
    public void Replay_controller_should_expose_override_endpoints_with_override_policies()
    {
        var previewAction = typeof(NotificationReplayController).GetMethod(nameof(NotificationReplayController.PreviewOverrideAsync));
        Assert.NotNull(previewAction);
        Assert.Equal("override-preview", previewAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Replay.OverridePreview,
            previewAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);

        var executeAction = typeof(NotificationReplayController).GetMethod(nameof(NotificationReplayController.ReplayOverrideAsync));
        Assert.NotNull(executeAction);
        Assert.Equal("override", executeAction!.GetCustomAttribute<HttpPostAttribute>()?.Template);
        Assert.Equal(
            NotificationsPermissions.Replay.OverrideExecute,
            executeAction.GetCustomAttributes<AuthorizeAttribute>(inherit: true).SingleOrDefault()?.Policy);
        Assert.NotNull(executeAction.GetCustomAttribute<AuditedAttribute>(inherit: true));
    }

    private static TemplateAdminFixture CreateTemplateAdminFixture(string databaseName, IReadOnlyList<TestTemplateFile> templates)
    {
        var dbContext = CreateDbContext(databaseName, tenantId: null);
        var definitionsRepository = new EfTestRepository<NotificationTemplateDefinition, Guid>(dbContext);
        var revisionsRepository = new EfTestRepository<NotificationTemplateRevision, Guid>(dbContext);
        var samplesRepository = new EfTestRepository<NotificationTemplateSample, Guid>(dbContext);
        var contentBlocksRepository = new EfTestRepository<NotificationTemplateContentBlock, Guid>(dbContext);
        var fileSource = new InMemoryFileSystemTemplateSource(
            [TestTemplateFile.Create("Layout", "Layout", "Shared", "Layout.tpl", "<html>{{model.content}}</html>"), .. templates]);
        var options = Options.Create(new NotificationsTemplateOptions
        {
            LayoutTemplateName = "Layout",
            Extension = ".tpl",
            RootPath = "ignored"
        });
        var importService = new NotificationTemplateImportService(definitionsRepository, revisionsRepository, fileSource);
        var templateStore = new DatabaseBackedTemplateStore(
            definitionsRepository,
            revisionsRepository,
            importService,
            fileSource,
            options);
        var rendererAppService = new TemplateRendererAppService(new ScribanNotificationsTemplateRenderer(templateStore, options));
        var service = new NotificationTemplateAdminAppService(
            definitionsRepository,
            revisionsRepository,
            samplesRepository,
            contentBlocksRepository,
            importService,
            rendererAppService,
            options);

        return new TemplateAdminFixture(dbContext, service, importService);
    }

    private static async Task<DeliveryWorkerFixture> CreateWorkerFixtureAsync(
        string databaseName,
        string templateModelJson,
        int maxRetryCount = 3)
    {
        var tenantId = Guid.NewGuid();
        var dbContext = CreateDbContext(databaseName, tenantId);
        var notification = new Notification(
            id: Guid.NewGuid(),
            tenantId: tenantId,
            requestId: Guid.NewGuid(),
            channel: NotificationChannel.Email,
            templateName: "Security.Alert",
            templateModelJson: templateModelJson,
            subject: "Security alert");
        var recipient = new NotificationRecipient(
            id: 901,
            tenantId: tenantId,
            notificationId: notification.Id,
            recipientType: NotificationRecipientType.EmailAddress,
            recipientValue: "worker@example.test");
        var delivery = new NotificationDelivery(902, tenantId, notification.Id, recipient.Id);
        SetProperty(delivery, "CreationTime", DateTime.UtcNow.AddMinutes(-15));

        dbContext.Notifications.Add(notification);
        dbContext.NotificationRecipients.Add(recipient);
        dbContext.NotificationDeliveries.Add(delivery);
        await dbContext.SaveChangesAsync();

        var handler = new CapturingChannelDeliveryHandler(NotificationChannel.Email);
        var worker = new NotificationDeliveryWorker(
            new ImmediateUnitOfWorkExecutor(),
            NotificationsEfCoreTestHarness.AsDbContextProvider(dbContext),
            [handler],
            new NoopRealtimePublisher(),
            new NoopAuditLogPublisher(),
            new TestCurrentTenant(tenantId),
            new StubNotificationsTemplateRenderer(),
            Options.Create(new NotificationsDeliveryOptions
            {
                BatchSize = 10,
                MaxRetryCount = maxRetryCount,
                ProcessingLeaseSeconds = 30,
                BaseRetryDelaySeconds = 1,
                MaxRetryDelaySeconds = 1
            }),
            NullLogger<NotificationDeliveryWorker>.Instance);

        return new DeliveryWorkerFixture(dbContext, delivery, handler, worker);
    }

    private static NotificationsDbContextApp CreateDbContext(string databaseName, Guid? tenantId)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentTenant>(new TestCurrentTenant(tenantId));
        services.AddSingleton<Shared.Data.Filtering.IDataFilter>(new AdminConsoleDataFilter());
        services.AddSingleton<IAuditPropertySetter>(new NoopAuditPropertySetter());
        services.AddSingleton<IAuditingManager>(new NoopAuditingManager());
        services.AddSingleton<Shared.Uow.Abstractions.IUnitOfWorkManager>(new NoopUnitOfWorkManager());
        services.AddSingleton<ICurrentPrincipalAccessor>(new TestCurrentPrincipalAccessor("u1"));
        var provider = services.BuildServiceProvider();
        var options = new DbContextOptionsBuilder<NotificationsDbContextApp>()
            .UseInMemoryDatabase(databaseName)
            .Options;

        var dbContext = new NotificationsDbContextApp(options)
        {
            ServiceProvider = provider
        };
        dbContext.Initialize(new EfCoreDbContextInitializationContext(
            new global::Notifications.Tests.NoopUnitOfWork(new UnitOfWorkOptions())));
        return dbContext;
    }

    private sealed record TemplateAdminFixture(
        NotificationsDbContextApp DbContext,
        NotificationTemplateAdminAppService Service,
        NotificationTemplateImportService ImportService);

    private sealed record DeliveryWorkerFixture(
        NotificationsDbContextApp DbContext,
        NotificationDelivery Delivery,
        CapturingChannelDeliveryHandler Handler,
        NotificationDeliveryWorker Worker);

    private sealed record TestTemplateFile(
        string Name,
        string DisplayName,
        string Category,
        string RelativePath,
        string BodyTemplate,
        string? SubjectTemplate = null)
    {
        public static TestTemplateFile Create(
            string name,
            string displayName,
            string category,
            string relativePath,
            string bodyTemplate,
            string? subjectTemplate = null)
        {
            return new TestTemplateFile(name, displayName, category, relativePath, bodyTemplate, subjectTemplate);
        }
    }

    private sealed class InMemoryFileSystemTemplateSource : IFileSystemTemplateSource
    {
        private readonly Dictionary<string, TestTemplateFile> _templates;

        public InMemoryFileSystemTemplateSource(IEnumerable<TestTemplateFile> templates)
        {
            _templates = templates.ToDictionary(template => template.Name, StringComparer.Ordinal);
        }

        public IReadOnlyList<FileSystemTemplateDescriptor> DiscoverTemplates(bool includeLayout = false)
        {
            return _templates.Values
                .Where(template => includeLayout || !string.Equals(template.Name, "Layout", StringComparison.OrdinalIgnoreCase))
                .OrderBy(template => template.Category, StringComparer.OrdinalIgnoreCase)
                .ThenBy(template => template.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(template => new FileSystemTemplateDescriptor
                {
                    Name = template.Name,
                    DisplayName = template.DisplayName,
                    Category = template.Category,
                    RelativePath = template.RelativePath
                })
                .ToList();
        }

        public Task<StoredNotificationTemplateContent?> GetAsync(string templateName, CancellationToken cancellationToken = default)
        {
            if (!_templates.TryGetValue(templateName, out var template))
            {
                return Task.FromResult<StoredNotificationTemplateContent?>(null);
            }

            return Task.FromResult<StoredNotificationTemplateContent?>(new StoredNotificationTemplateContent
            {
                TemplateName = template.Name,
                BodyTemplate = template.BodyTemplate,
                SubjectTemplate = template.SubjectTemplate,
                RelativePath = template.RelativePath,
                SourceType = "FileSystem"
            });
        }
    }

    private sealed class TestFeatureChecker(params string[] enabledFeatures)
        : Permissions.Application.Contracts.Features.IFeatureCheckerClient
    {
        private readonly HashSet<string> _enabledFeatures = enabledFeatures.ToHashSet(StringComparer.Ordinal);

        public Task<bool> IsEnabledAsync(
            Guid? tenantId,
            string featureName,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_enabledFeatures.Contains(featureName));
        }

        public Task<Dictionary<string, bool>> AreEnabledAsync(
            Guid? tenantId,
            IReadOnlyCollection<string> featureNames,
            CancellationToken cancellationToken = default)
        {
            return Task.FromResult(featureNames.ToDictionary(
                featureName => featureName,
                featureName => _enabledFeatures.Contains(featureName),
                StringComparer.Ordinal));
        }
    }

    private sealed class CapturingNotificationDeliveryScheduler : INotificationDeliveryScheduler
    {
        public CapturedNotificationRequest? LastRequest { get; private set; }
        public Guid? LastTenantId { get; private set; }

        public Task<NotificationScheduleResult> ScheduleAsync(
            NotificationChannelRequestTenant input,
            CancellationToken cancellationToken = default)
            => CaptureAsync(CapturedNotificationRequest.From(input));

        public Task<NotificationScheduleResult> ScheduleAsync(
            NotificationChannelRequestHost input,
            CancellationToken cancellationToken = default)
            => CaptureAsync(CapturedNotificationRequest.From(input));

        private Task<NotificationScheduleResult> CaptureAsync(CapturedNotificationRequest input)
        {
            LastRequest = input;
            LastTenantId = input.TenantId;
            return Task.FromResult(new NotificationScheduleResult
            {
                Accepted = true,
                IsDuplicate = false,
                SkippedByPreferences = false,
                RequestId = input.RequestId,
                NotificationId = Guid.CreateVersion7(),
                QueuedCount = input.Recipients.Count
            });
        }
    }

    private sealed class CapturingChannelDeliveryHandler(NotificationChannel channel) : IChannelDeliveryHandler
    {
        public NotificationChannel Channel { get; } = channel;
        public int CallCount { get; private set; }
        public ChannelDeliveryResult Result { get; set; } = ChannelDeliveryResult.Success("provider-message");

        public Task<ChannelDeliveryResult> DeliverAsync(
            ChannelDeliveryContext context,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(Result);
        }
    }

    private sealed class NoopRealtimePublisher : INotificationsRealtimePublisher
    {
        public Task PublishAppNotificationAsync(
            AppNotificationSignalDto input,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public Task PublishSessionRevokedAsync(
            SessionRevokedSignalDto input,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoopAuditLogPublisher : Shared.AuditLogs.Publishing.IAuditLogWriteRequestPublisher
    {
        public Task PublishAsync(
            Shared.AuditLogs.Publishing.AuditLogWriteRequest request,
            CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class NoopNotificationsProviderSecretProtector : INotificationsProviderSecretProtector
    {
        public string Protect(Guid? tenantId, NotificationChannel channel, Guid configId, string plaintext) => plaintext;

        public string Unprotect(Guid? tenantId, NotificationChannel channel, Guid configId, string storedValue) => storedValue;

        public bool IsProtected(string? storedValue) => false;

        public bool IsProtectedByCurrentKey(string? storedValue) => false;

        public string Fingerprint(string plaintext) => "fake";
    }

    private sealed class PassthroughHybridCache<TValue> : IHybridCache<TValue, string>
        where TValue : class
    {
        public async Task<TValue?> GetOrCreateAsync(
            string key,
            Func<Task<TValue>> factory,
            Func<Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions>? optionsFactory = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            return await factory();
        }

        public Task SetAsync(
            string key,
            TValue value,
            Microsoft.Extensions.Caching.Hybrid.HybridCacheEntryOptions? options = null,
            bool? hideErrors = null,
            bool considerUow = false,
            CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string key, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        public Task RemoveManyAsync(IEnumerable<string> keys, bool? hideErrors = null, bool considerUow = false, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingSmtpTransportFactory : ISmtpTransportFactory
    {
        public RecordingSmtpTransportClient? LastClient { get; private set; }

        public ISmtpTransportClient Create()
        {
            var client = new RecordingSmtpTransportClient();
            LastClient = client;
            return client;
        }
    }

    private sealed class RecordingSmtpTransportClient : ISmtpTransportClient
    {
        public string? Host { get; private set; }
        public int Port { get; private set; }
        public SecureSocketOptions SecureSocketOptions { get; private set; }
        public string? UserName { get; private set; }
        public string? Password { get; private set; }
        public MimeMessage? Message { get; private set; }

        public Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default)
        {
            Host = host;
            Port = port;
            SecureSocketOptions = options;
            return Task.CompletedTask;
        }

        public Task AuthenticateAsync(string userName, string password, CancellationToken cancellationToken = default)
        {
            UserName = userName;
            Password = password;
            return Task.CompletedTask;
        }

        public Task<string> SendAsync(MimeMessage message, CancellationToken cancellationToken = default)
        {
            Message = message;
            return Task.FromResult("provider-accepted");
        }

        public Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }

        public void Dispose()
        {
        }
    }

    private sealed class TestDistributedCache : Microsoft.Extensions.Caching.Distributed.IDistributedCache
    {
        private readonly Dictionary<string, byte[]> _values = new(StringComparer.Ordinal);

        public byte[]? Get(string key)
        {
            return _values.TryGetValue(key, out var value) ? value : null;
        }

        public Task<byte[]?> GetAsync(string key, CancellationToken token = default)
        {
            return Task.FromResult(Get(key));
        }

        public void Set(string key, byte[] value, DistributedCacheEntryOptions options)
        {
            _values[key] = value;
        }

        public Task SetAsync(string key, byte[] value, DistributedCacheEntryOptions options, CancellationToken token = default)
        {
            Set(key, value, options);
            return Task.CompletedTask;
        }

        public void Refresh(string key)
        {
        }

        public Task RefreshAsync(string key, CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        public void Remove(string key)
        {
            _values.Remove(key);
        }

        public Task RemoveAsync(string key, CancellationToken token = default)
        {
            Remove(key);
            return Task.CompletedTask;
        }
    }

    private sealed class TestCurrentTenant : ICurrentTenant
    {
        public bool IsAvailable => Id.HasValue;
        public Guid? Id { get; private set; }
        public string? Name { get; private set; }

        public TestCurrentTenant(Guid? id)
        {
            Id = id;
        }

        public IDisposable Change(Guid? id, string? name = null)
        {
            var previousId = Id;
            var previousName = Name;
            Id = id;
            Name = name;
            return new DisposeAction(() =>
            {
                Id = previousId;
                Name = previousName;
            });
        }
    }

    private sealed class TestCurrentPrincipalAccessor : ICurrentPrincipalAccessor
    {
        private ClaimsPrincipal _principal;

        public ClaimsPrincipal Principal => _principal;

        public TestCurrentPrincipalAccessor(string userId)
        {
            _principal = new ClaimsPrincipal(
                new ClaimsIdentity(
                    [new Claim(SharedClaimTypes.Subject, userId)],
                    authenticationType: "Test"));
        }

        public IDisposable Change(ClaimsPrincipal principal)
        {
            var previous = _principal;
            _principal = principal;
            return new DisposeAction(() => _principal = previous);
        }
    }

    private sealed class DisposeAction : IDisposable
    {
        private readonly Action _action;

        public DisposeAction(Action action)
        {
            _action = action;
        }

        public void Dispose()
        {
            _action();
        }
    }

    private static void SetProperty(object target, string propertyName, object? value)
    {
        var property = target.GetType().GetProperty(
            propertyName,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(property);
        property!.SetValue(target, value);
    }

    private sealed class EfTestRepository<TEntity, TKey> : IRepository<TEntity, TKey>
        where TEntity : class, IEntity<TKey>
    {
        private readonly NotificationsDbContextApp _dbContext;

        public EfTestRepository(NotificationsDbContextApp dbContext)
        {
            _dbContext = dbContext;
        }

        public string? EntityName { get; set; }
        public string ProviderName => "EfCoreInMemory";

        public Task<IQueryable<TEntity>> WithDetailsAsync()
        {
            return Task.FromResult(_dbContext.Set<TEntity>().AsQueryable());
        }

        public Task<IQueryable<TEntity>> WithDetailsAsync(params System.Linq.Expressions.Expression<Func<TEntity, object>>[] propertySelectors)
        {
            IQueryable<TEntity> query = _dbContext.Set<TEntity>().AsQueryable();
            foreach (var propertySelector in propertySelectors)
            {
                query = query.Include(propertySelector);
            }

            return Task.FromResult(query);
        }

        public Task<IQueryable<TEntity>> GetQueryableAsync()
        {
            return Task.FromResult(_dbContext.Set<TEntity>().AsQueryable());
        }

        public Task<TEntity?> FindAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default)
        {
            return _dbContext.Set<TEntity>().FindAsync([id], cancellationToken).AsTask();
        }

        public async Task<TEntity> GetAsync(TKey id, bool includeDetails = true, CancellationToken cancellationToken = default)
        {
            var entity = await FindAsync(id, includeDetails, cancellationToken);
            return entity ?? throw new InvalidOperationException("Entity not found.");
        }

        public Task<TEntity?> FindAsync(System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate, bool includeDetails = true, CancellationToken cancellationToken = default)
        {
            return _dbContext.Set<TEntity>().FirstOrDefaultAsync(predicate, cancellationToken);
        }

        public async Task<TEntity> GetAsync(System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate, bool includeDetails = true, CancellationToken cancellationToken = default)
        {
            var entity = await FindAsync(predicate, includeDetails, cancellationToken);
            return entity ?? throw new InvalidOperationException("Entity not found.");
        }

        public async Task DeleteAsync(System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            var entities = await _dbContext.Set<TEntity>().Where(predicate).ToListAsync(cancellationToken);
            _dbContext.Set<TEntity>().RemoveRange(entities);
            if (autoSave)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        public async Task<TEntity> InsertAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            await _dbContext.Set<TEntity>().AddAsync(entity, cancellationToken);
            if (autoSave)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }

            return entity;
        }

        public async Task InsertManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            await _dbContext.Set<TEntity>().AddRangeAsync(entities, cancellationToken);
            if (autoSave)
            {
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        public Task<TEntity> UpdateAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            _dbContext.Set<TEntity>().Update(entity);
            return autoSave ? SaveAndReturnAsync(entity, cancellationToken) : Task.FromResult(entity);
        }

        public Task UpdateManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            _dbContext.Set<TEntity>().UpdateRange(entities);
            return autoSave ? _dbContext.SaveChangesAsync(cancellationToken) : Task.CompletedTask;
        }

        public Task DeleteAsync(TEntity entity, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            _dbContext.Set<TEntity>().Remove(entity);
            return autoSave ? _dbContext.SaveChangesAsync(cancellationToken) : Task.CompletedTask;
        }

        public Task DeleteManyAsync(IEnumerable<TEntity> entities, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            _dbContext.Set<TEntity>().RemoveRange(entities);
            return autoSave ? _dbContext.SaveChangesAsync(cancellationToken) : Task.CompletedTask;
        }

        public Task DeleteAsync(TKey id, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            return DeleteAsync(x => EqualityComparer<TKey>.Default.Equals(x.Id, id), autoSave, cancellationToken);
        }

        public Task DeleteManyAsync(IEnumerable<TKey> ids, bool autoSave = false, CancellationToken cancellationToken = default)
        {
            var set = ids.ToHashSet();
            return DeleteAsync(x => set.Contains(x.Id), autoSave, cancellationToken);
        }

        public Task DeleteDirectAsync(System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate, CancellationToken cancellationToken = default)
        {
            return _dbContext.Set<TEntity>().Where(predicate).ExecuteDeleteAsync(cancellationToken);
        }

        public Task<List<TEntity>> GetListAsync(bool includeDetails = false, CancellationToken cancellationToken = default)
        {
            return _dbContext.Set<TEntity>().ToListAsync(cancellationToken);
        }

        public Task<List<TEntity>> GetListAsync(System.Linq.Expressions.Expression<Func<TEntity, bool>> predicate, bool includeDetails = false, CancellationToken cancellationToken = default)
        {
            return _dbContext.Set<TEntity>().Where(predicate).ToListAsync(cancellationToken);
        }

        public Task<long> GetCountAsync(CancellationToken cancellationToken = default)
        {
            return _dbContext.Set<TEntity>().LongCountAsync(cancellationToken);
        }

        public Task<List<TEntity>> GetPagedListAsync(int skipCount, int maxResultCount, string sorting, bool includeDetails = false, CancellationToken cancellationToken = default)
        {
            return _dbContext.Set<TEntity>()
                .Skip(skipCount)
                .Take(maxResultCount)
                .ToListAsync(cancellationToken);
        }

        private async Task<TEntity> SaveAndReturnAsync(TEntity entity, CancellationToken cancellationToken)
        {
            await _dbContext.SaveChangesAsync(cancellationToken);
            return entity;
        }
    }

    private sealed class AdminConsoleDataFilter : Shared.Data.Filtering.IDataFilter
    {
        public IDisposable Enable<TFilter>() where TFilter : class => new DisposeAction(() => { });
        public IDisposable Disable<TFilter>() where TFilter : class => new DisposeAction(() => { });
        public bool IsEnabled<TFilter>() where TFilter : class
            => typeof(TFilter) != typeof(Shared.Domain.IMultiTenant);
    }

    private sealed class NoopAuditPropertySetter : IAuditPropertySetter
    {
        public void SetCreationProperties(object entity) { }
        public void SetModificationProperties(object entity) { }
        public void SetDeletionProperties(object entity) { }
        public void IncrementEntityVersionProperty(object entity) { }
    }

    private sealed class NoopAuditingManager : IAuditingManager
    {
        public IAuditLogScope? Current { get; } = new NoopAuditLogScope();

        public IAuditLogSaveHandle BeginScope()
        {
            return NoopAuditLogSaveHandle.Instance;
        }
    }

    private sealed class NoopAuditLogScope : IAuditLogScope
    {
        public AuditLogInfo Log { get; } = new();
    }

    private sealed class NoopAuditLogSaveHandle : IAuditLogSaveHandle
    {
        public static readonly NoopAuditLogSaveHandle Instance = new();

        public Task SaveAsync() => Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class NoopUnitOfWorkManager : Shared.Uow.Abstractions.IUnitOfWorkManager
    {
        public Shared.Uow.Abstractions.IUnitOfWork? Current => null;
        public Shared.Uow.Abstractions.IUnitOfWork Begin(Shared.Uow.Options.UnitOfWorkOptions options, bool requiresNew = false) => throw new NotSupportedException();
        public Shared.Uow.Abstractions.IUnitOfWork Begin(bool requiresNew = false) => throw new NotSupportedException();
        public Shared.Uow.Abstractions.IUnitOfWork Reserve(string reservationName, bool requiresNew = false) => throw new NotSupportedException();
        public void BeginReserved(string reservationName, Shared.Uow.Options.UnitOfWorkOptions options) => throw new NotSupportedException();
        public bool TryBeginReserved(string reservationName, Shared.Uow.Options.UnitOfWorkOptions options) => throw new NotSupportedException();
    }

}
