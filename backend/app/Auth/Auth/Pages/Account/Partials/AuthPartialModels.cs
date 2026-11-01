using Microsoft.AspNetCore.Html;

namespace Auth.Pages.Account.Partials;

/// <summary>
/// Single source of truth for auth-surface branding. Change here to rebrand
/// every interactive auth page consistently.
/// </summary>
public static class AuthBrand
{
    public const string Name = "TenantWright";
    public const string Tagline = "Secure account access";
    public const string LegalName = "TenantWright";
}

/// <summary>
/// Thin-stroke inline SVG icons (Lucide, ISC-licensed paths). No icon font:
/// always renders, always flex-centers. Call as @AuthIcons.Get("mail", "size-5").
/// </summary>
public static class AuthIcons
{
    private static readonly Dictionary<string, string> Paths = new()
    {
        ["building"] = "<path d=\"M6 22V4a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v18Z\"/><path d=\"M6 12H4a2 2 0 0 0-2 2v6a2 2 0 0 0 2 2h2\"/><path d=\"M18 9h2a2 2 0 0 1 2 2v9a2 2 0 0 1-2 2h-2\"/><path d=\"M10 6h4\"/><path d=\"M10 10h4\"/><path d=\"M10 14h4\"/><path d=\"M10 18h4\"/>",
        ["id-card"] = "<path d=\"M16 10h2\"/><path d=\"M16 14h2\"/><path d=\"M6.17 15a3 3 0 0 1 5.66 0\"/><circle cx=\"9\" cy=\"11\" r=\"2\"/><rect x=\"2\" y=\"5\" width=\"20\" height=\"14\" rx=\"2\"/>",
        ["user"] = "<path d=\"M19 21v-2a4 4 0 0 0-4-4H9a4 4 0 0 0-4 4v2\"/><circle cx=\"12\" cy=\"7\" r=\"4\"/>",
        ["mail"] = "<path d=\"m22 7-8.991 5.727a2 2 0 0 1-2.009 0L2 7\"/><rect x=\"2\" y=\"4\" width=\"20\" height=\"16\" rx=\"2\"/>",
        ["phone"] = "<path d=\"M13.832 16.568a1 1 0 0 0 1.213-.303l.355-.465A2 2 0 0 1 17 15h3a2 2 0 0 1 2 2v3a2 2 0 0 1-2 2A18 18 0 0 1 2 4a2 2 0 0 1 2-2h3a2 2 0 0 1 2 2v3a2 2 0 0 1-.8 1.6l-.468.351a1 1 0 0 0-.292 1.233 14 14 0 0 0 6.392 6.384\"/>",
        ["lock"] = "<rect width=\"18\" height=\"11\" x=\"3\" y=\"11\" rx=\"2\" ry=\"2\"/><path d=\"M7 11V7a5 5 0 0 1 10 0v4\"/>",
        ["eye"] = "<path d=\"M2.062 12.348a1 1 0 0 1 0-.696 10.75 10.75 0 0 1 19.876 0 1 1 0 0 1 0 .696 10.75 10.75 0 0 1-19.876 0\"/><circle cx=\"12\" cy=\"12\" r=\"3\"/>",
        ["eye-off"] = "<path d=\"M10.733 5.076a10.744 10.744 0 0 1 11.205 6.575 1 1 0 0 1 0 .696 10.747 10.747 0 0 1-1.444 2.49\"/><path d=\"M14.084 14.158a3 3 0 0 1-4.242-4.242\"/><path d=\"M17.479 17.499a10.75 10.75 0 0 1-15.417-5.151 1 1 0 0 1 0-.696 10.75 10.75 0 0 1 4.446-5.143\"/><path d=\"m2 2 20 20\"/>",
        ["arrow-right"] = "<path d=\"M5 12h14\"/><path d=\"m12 5 7 7-7 7\"/>",
        ["arrow-left"] = "<path d=\"m12 19-7-7 7-7\"/><path d=\"M19 12H5\"/>",
        ["check"] = "<path d=\"M20 6 9 17l-5-5\"/>",
        ["check-circle"] = "<path d=\"M21.801 10A10 10 0 1 1 17 3.335\"/><path d=\"m9 11 3 3L22 4\"/>",
        ["alert-circle"] = "<circle cx=\"12\" cy=\"12\" r=\"10\"/><line x1=\"12\" x2=\"12\" y1=\"8\" y2=\"12\"/><line x1=\"12\" x2=\"12.01\" y1=\"16\" y2=\"16\"/>",
        ["shield-check"] = "<path d=\"M20 13c0 5-3.5 7.5-7.66 8.95a1 1 0 0 1-.67-.01C7.5 20.5 4 18 4 13V6a1 1 0 0 1 1-1c2 0 4.5-1.2 6.24-2.72a1.17 1.17 0 0 1 1.52 0C14.51 3.81 17 5 19 5a1 1 0 0 1 1 1z\"/><path d=\"m9 12 2 2 4-4\"/>",
        ["key"] = "<path d=\"M2.586 17.414A2 2 0 0 0 2 18.828V21a1 1 0 0 0 1 1h3a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h1a1 1 0 0 0 1-1v-1a1 1 0 0 1 1-1h.172a2 2 0 0 0 1.414-.586l.814-.814a6.5 6.5 0 1 0-4-4z\"/><circle cx=\"16.5\" cy=\"7.5\" r=\".5\" fill=\"currentColor\"/>",
        ["info"] = "<circle cx=\"12\" cy=\"12\" r=\"10\"/><path d=\"M12 16v-4\"/><path d=\"M12 8h.01\"/>",
        ["activity"] = "<path d=\"M22 12h-2.48a2 2 0 0 0-1.93 1.46l-2.35 8.36a.25.25 0 0 1-.48 0L9.24 2.18a.25.25 0 0 0-.48 0l-2.35 8.36A2 2 0 0 1 4.49 12H2\"/>",
        ["support"] = "<path d=\"M3 5a2 2 0 0 1 2-2h3.28a1 1 0 0 1 .948.684l1.498 4.493a1 1 0 0 1-.502 1.21l-2.257 1.13a11 11 0 0 0 5 5l1.13-2.257a1 1 0 0 1 1.21-.502l4.493 1.498a1 1 0 0 1 .684.949V19a2 2 0 0 1-2 2h-1C9.716 21 3 14.284 3 6Z\"/>",
    };

    public static HtmlString Get(string name, string cls = "size-5", string stroke = "1.75")
    {
        var body = Paths.TryGetValue(name, out var p) ? p : "<circle cx=\"12\" cy=\"12\" r=\"9\"/>";
        return new HtmlString(
            $"<svg viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"{stroke}\" stroke-linecap=\"round\" stroke-linejoin=\"round\" class=\"{cls}\" aria-hidden=\"true\">{body}</svg>");
    }
}

public sealed class AuthActionButtonModel
{
    public string Text { get; set; } = "Continue";
    public string? IconClass { get; set; }
    public string? IconSymbol { get; set; }
    public string Type { get; set; } = "submit";
    public bool IsPrimary { get; set; } = true;
    public bool IsEmerald { get; set; }
    public string CssClass { get; set; } = string.Empty;
    public string? LoadingText { get; set; }
}

/// <summary>Icon tile + heading + optional description. Standardizes the top of every auth card.</summary>
public sealed class AuthHeaderModel
{
    public string Icon { get; set; } = "shield-check";
    public string Heading { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool Centered { get; set; } = true;
    /// <summary>primary | success | danger | warning | info</summary>
    public string Tone { get; set; } = "primary";
    public bool ShowIcon { get; set; } = true;
}

/// <summary>Bottom-of-card prompt + link (e.g. "Already have an account? Sign in").</summary>
public sealed class AuthFooterModel
{
    public string Prompt { get; set; } = string.Empty;
    public string LinkText { get; set; } = string.Empty;
    public string LinkHref { get; set; } = "#";
}

/// <summary>An inline alert/notice with a semantic tone.</summary>
public sealed class AuthAlertModel
{
    public string Message { get; set; } = string.Empty;
    /// <summary>error | success | info | warning</summary>
    public string Tone { get; set; } = "error";
}

/// <summary>
/// A standard labelled text input (icon + control + status + validation/helper).
/// Renders name/id/value manually instead of asp-for so it can be reused across pages;
/// safe because every host model is [BindProperty], so Value already holds the
/// attempted value after a failed POST, and Name drives both binding and data-valmsg-for.
/// Not for password fields (those use authPasswordField + the toggle partial).
/// </summary>
public sealed class AuthFieldModel
{
    /// <summary>Bound property path, e.g. "Input.Tenant". Drives name, id, and data-valmsg-for.</summary>
    public string Name { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Value { get; set; }
    public string Icon { get; set; } = "building";
    public string? Placeholder { get; set; }
    /// <summary>Client validation strategy key for data-auth-validate (tenant, email, username, login-id, phone…).</summary>
    public string? Validate { get; set; }
    public string Type { get; set; } = "text";
    public string? InputMode { get; set; }
    public string? Autocomplete { get; set; }
    public string? Helper { get; set; }
    public bool Optional { get; set; }
    public int? MaxLength { get; set; }

    /// <summary>asp-for-style id: "Input.Tenant" → "Input_Tenant".</summary>
    public string FieldId => Name.Replace('.', '_');
}

/// <summary>A resend action with a client-side cooldown ("Resend in 45s").</summary>
public sealed class AuthResendModel
{
    public string IdleLabel { get; set; } = "Resend email";
    public string Href { get; set; } = "#";
    /// <summary>sessionStorage key — unique per flow so cooldowns don't collide.</summary>
    public string Key { get; set; } = "auth.resend";
    public int Seconds { get; set; } = 45;
    /// <summary>secondary renders an outline button; primary renders a filled button.</summary>
    public bool IsPrimary { get; set; }
}
