<p>A new device sign-in was detected for {{ user_name | default userName }}.</p>
<p>Client IP: {{ client_ip_address | default clientIpAddress | default "unknown" }}</p>
<p>Browser: {{ browser_info | default browserInfo | default "unknown" }}</p>
<p>Location: {{ location | default "unknown" }}</p>
