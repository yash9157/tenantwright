<table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="width:100%;max-width:640px;border-collapse:separate;border-spacing:0;background:#ffffff;border:1px solid #e1e3e4;border-radius:20px;overflow:hidden;box-shadow:0 28px 56px -18px rgba(0,104,94,0.10);">
  <tr>
    <td align="center" style="padding:32px 32px 20px;">
      <div style="font-family:Manrope,Arial,sans-serif;font-size:26px;font-weight:800;letter-spacing:-0.02em;color:#00685e;">
        Clinical Sanctuary
      </div>
      <div style="margin-top:8px;font-size:12px;font-weight:700;letter-spacing:0.18em;text-transform:uppercase;color:#526069;">
        Secure organization invitation
      </div>
    </td>
  </tr>
  <tr>
    <td style="padding:0 32px 32px;text-align:center;">
      <h1 style="margin:0 0 16px;font-family:Manrope,Arial,sans-serif;font-size:30px;line-height:1.1;font-weight:800;letter-spacing:-0.03em;color:#191c1d;">
        Join {{model.tenantName}}
      </h1>
      <p style="margin:0 auto 24px;max-width:460px;font-size:15px;line-height:1.7;color:#526069;">
        Your organization invited {{model.recipient}} to activate a secure account{{ if model.roleName && model.roleName != empty }} as {{ model.roleName }}{{ end }}.
      </p>
      <div style="margin:0 0 24px;">
        <a href="{{model.link}}" style="display:inline-block;padding:15px 30px;border-radius:12px;background:#00685e;background-image:linear-gradient(135deg,#00685e 0%,#008377 100%);color:#ffffff;text-decoration:none;font-family:Manrope,Arial,sans-serif;font-size:15px;font-weight:700;box-shadow:0 18px 32px -18px rgba(0,104,94,0.35);">
          Accept Invitation
        </a>
      </div>
      <table role="presentation" width="100%" cellpadding="0" cellspacing="0" border="0" style="width:100%;border-collapse:separate;border-spacing:0;background:#f3f4f5;border:1px solid #e1e3e4;border-radius:14px;">
        <tr>
          <td style="padding:16px 18px;text-align:left;">
            <div style="margin:0 0 8px;font-size:11px;font-weight:700;letter-spacing:0.14em;text-transform:uppercase;color:#526069;">
              Direct invitation link
            </div>
            <a href="{{model.link}}" style="color:#00685e;word-break:break-all;text-decoration:none;">{{model.link}}</a>
          </td>
        </tr>
      </table>
      <p style="margin:24px auto 0;max-width:420px;font-size:12px;line-height:1.7;color:#526069;">
        This link expires automatically. If you did not expect this invitation, you can ignore this email.
      </p>
    </td>
  </tr>
</table>
