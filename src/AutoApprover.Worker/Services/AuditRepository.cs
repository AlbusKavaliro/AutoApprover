using System.Diagnostics.CodeAnalysis;
using AutoApprover.Worker.Models;
using Npgsql;

namespace AutoApprover.Worker.Services;

internal sealed class AuditRepository(string connectionString) : IAuditRepository
{
    [SuppressMessage("Security", "CA2100", Justification = "Die SQL-Statements sind fest verdingte DDL/DML-Schema-Statements ohne Benutzereingaben.")]
    [SuppressMessage("Usage", "CA2007", Justification = "await using auf IAsyncDisposable: ConfigureAwait ist an der Deklaration nicht anwendbar; der Token wird an OpenAsync/ExecuteNonQueryAsync weitergereicht.")]
    public async Task EnsureSchemaAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        var commands = new[]
        {
            """
            CREATE TABLE IF NOT EXISTS environment_auto_approve_config (
                id                   SERIAL PRIMARY KEY,
                environment_name     TEXT NOT NULL,
                project_name         TEXT NOT NULL,
                pipeline_id          TEXT,
                is_enabled           BOOLEAN DEFAULT TRUE,
                allowed_pr_creator   TEXT DEFAULT 'renovate',
                allowed_change_types TEXT[] DEFAULT ARRAY['dockerfile_digest','patch','minor'],
                created_at           TIMESTAMPTZ DEFAULT now(),
                updated_at           TIMESTAMPTZ DEFAULT now(),
                UNIQUE (environment_name, project_name)
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS auto_approve_audit_log (
                id                BIGSERIAL PRIMARY KEY,
                environment_name  TEXT NOT NULL,
                project_name      TEXT NOT NULL,
                pipeline_name     TEXT,
                run_id            INTEGER,
                commit_id         TEXT,
                pr_id             INTEGER,
                pr_creator        TEXT,
                pr_title          TEXT,
                change_type       TEXT,
                decision          TEXT NOT NULL,
                reason            TEXT,
                approved_at       TIMESTAMPTZ,
                deployment_ok     BOOLEAN,
                created_at        TIMESTAMPTZ DEFAULT now()
            );
            """,
            """
            CREATE TABLE IF NOT EXISTS feature_flags (
                id          SERIAL PRIMARY KEY,
                name        TEXT UNIQUE NOT NULL,
                is_enabled  BOOLEAN DEFAULT FALSE,
                description TEXT,
                updated_at  TIMESTAMPTZ DEFAULT now()
            );
            """,
            """
            INSERT INTO feature_flags (name, is_enabled, description)
            VALUES ('auto_approve_global', TRUE, 'Hauptschalter für den Auto-Approver'),
                   ('auto_approve_production', TRUE, 'Auto-Approver auch in Produktion erlauben')
            ON CONFLICT (name) DO NOTHING;
            """
        };

        foreach (var cmd in commands)
        {
            await using var command = new NpgsqlCommand(cmd, conn);
            await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    [SuppressMessage("Usage", "CA2007", Justification = "await using auf IAsyncDisposable: ConfigureAwait ist an der Deklaration nicht anwendbar; der Token wird an OpenAsync/ExecuteNonQueryAsync weitergereicht.")]
    public async Task WriteAsync(DecisionResult decision, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(decision);

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        const string sql = """
            INSERT INTO auto_approve_audit_log
                (environment_name, project_name, pipeline_name, run_id, commit_id, pr_id, pr_creator, pr_title, change_type, decision, reason, approved_at)
            VALUES
                (@environmentName, @projectName, @pipelineName, @runId, @commitId, @prId, @prCreator, @prTitle, @changeType, @decision, @reason, @approvedAt);
        """;

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("environmentName", decision.EnvironmentName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("projectName", decision.ProjectName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("pipelineName", decision.PipelineName ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("runId", decision.RunId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("commitId", decision.CommitId ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("prId", decision.PullRequestId == 0 ? (object)DBNull.Value : decision.PullRequestId);
        cmd.Parameters.AddWithValue("prCreator", decision.PullRequestCreator ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("prTitle", decision.PullRequestTitle ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("changeType", decision.ChangeType ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("decision", decision.Decision ?? "skipped");
        cmd.Parameters.AddWithValue("reason", decision.Reason ?? (object)DBNull.Value);
        cmd.Parameters.AddWithValue("approvedAt", decision.ApprovedAt ?? (object)DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }
}
