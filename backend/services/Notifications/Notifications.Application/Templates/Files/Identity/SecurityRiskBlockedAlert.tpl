<p>A suspicious sign-in attempt was blocked for {{ user_name | default userName }}.</p>
<p>Risk level: {{ risk_level | default riskLevel }}</p>
<p>Risk score: {{ risk_score | default riskScore }}</p>
<p>Reasons: {{ risk_reasons | default riskReasons }}</p>
