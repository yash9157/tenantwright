<p>Your active sessions were revoked by an administrator action.</p>
<p>Action: {{ action | default "unknown" }}</p>
<p>Reason: {{ revocation_reason | default revocationReason | default "not provided" }}</p>
