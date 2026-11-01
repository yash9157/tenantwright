# Alerts, Dashboards, and Runbooks

## Alerts

Every alert needs a user or platform impact, stable signal, severity, owner, actionable threshold, evaluation window, and recovery action. Prefer multi-window symptoms or sustained saturation over single-sample noise. Verify routing without exposing receiver secrets.

## Dashboards

Organize from service-level outcomes to resource and dependency detail. Show rate, errors, duration, saturation, deployment context, and links between traces and logs where configured. Keep variables bounded and useful across tenants without exposing tenant-owned data.

## Runbooks

For each alert document:

1. impact and scope;
2. safe first checks;
3. queries or dashboards;
4. likely causes;
5. mitigation and rollback boundaries;
6. escalation owner;
7. recovery verification.

A dashboard without a decision it supports, or an alert without an owned response, is incomplete.
