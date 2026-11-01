-- Reset the local development databases for the template's core services.
-- Run this in pgAdmin Query Tool, psql, or DBeaver connected to the postgres database.
-- Only the tenantwright_* databases are touched; every other database on the server is left alone.
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'tenantwright_auth' AND pid <> pg_backend_pid();
DROP DATABASE IF EXISTS "tenantwright_auth";
CREATE DATABASE "tenantwright_auth";
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'tenantwright_identity' AND pid <> pg_backend_pid();
DROP DATABASE IF EXISTS "tenantwright_identity";
CREATE DATABASE "tenantwright_identity";
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'tenantwright_tenant' AND pid <> pg_backend_pid();
DROP DATABASE IF EXISTS "tenantwright_tenant";
CREATE DATABASE "tenantwright_tenant";
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'tenantwright_permissions' AND pid <> pg_backend_pid();
DROP DATABASE IF EXISTS "tenantwright_permissions";
CREATE DATABASE "tenantwright_permissions";
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'tenantwright_auditlogs' AND pid <> pg_backend_pid();
DROP DATABASE IF EXISTS "tenantwright_auditlogs";
CREATE DATABASE "tenantwright_auditlogs";
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'tenantwright_notifications' AND pid <> pg_backend_pid();
DROP DATABASE IF EXISTS "tenantwright_notifications";
CREATE DATABASE "tenantwright_notifications";
SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = 'tenantwright_workflows' AND pid <> pg_backend_pid();
DROP DATABASE IF EXISTS "tenantwright_workflows";
CREATE DATABASE "tenantwright_workflows";
