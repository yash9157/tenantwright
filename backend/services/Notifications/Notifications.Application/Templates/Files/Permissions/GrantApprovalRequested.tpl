<p>{{ requester_display_name | default "An operator" }} requested a sensitive access change.</p>
<p>Capability: {{ permission_name }}</p>
<p>Target: {{ provider_display_name }}</p>
<p>Reason: {{ reason | default "Not provided" }}</p>
<p>Ticket: {{ ticket_id | default "Not provided" }}</p>
