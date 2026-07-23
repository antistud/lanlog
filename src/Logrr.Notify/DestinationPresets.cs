namespace Logrr.Notify;

/// <summary>
/// Starter templates (SPEC §10.1): a preset is just a prefilled URL/headers/body-template
/// triple — there are no vendor-specific code paths.
/// </summary>
public static class DestinationPresets
{
    public sealed record Preset(string Key, string Name, string ContentType, string BodyTemplate,
        string? TicketIdPath, string? TicketUrlPath, string UrlHint);

    public static readonly IReadOnlyList<Preset> All =
    [
        new("gitea", "Gitea Issues", "application/json",
            """
            {
              "title": "[{{app.name}}] {{event.message | truncate:120}}",
              "body": "**Level:** {{event.level}}\n**When:** {{event.timestamp}}\n**Occurrences:** {{occurrence.count}} since {{occurrence.firstSeen}}\n\n{{event.exception | md}}\n\n[View in Logrr]({{link.event}})",
              "labels": ["bug", "logrr"]
            }
            """,
            "$.number", "$.html_url",
            "https://gitea.example/api/v1/repos/{owner}/{repo}/issues"),

        new("github", "GitHub Issues", "application/json",
            """
            {
              "title": "[{{app.name}}] {{event.message | truncate:120}}",
              "body": "**Level:** {{event.level}}\n**Occurrences:** {{occurrence.count}}\n\n{{event.exception | md}}\n\n[View in Logrr]({{link.event}})",
              "labels": ["bug"]
            }
            """,
            "$.number", "$.html_url",
            "https://api.github.com/repos/{owner}/{repo}/issues"),

        new("jira", "Jira Cloud", "application/json",
            """
            {
              "fields": {
                "project": { "key": "OPS" },
                "summary": "[{{app.name}}] {{event.message | truncate:120}}",
                "description": "Level {{event.level}} — {{occurrence.count}} occurrences.\n\n{{event.exception}}",
                "issuetype": { "name": "Bug" }
              }
            }
            """,
            "$.key", "$.self",
            "https://your-domain.atlassian.net/rest/api/2/issue"),

        new("zendesk", "Zendesk", "application/json",
            """
            {
              "ticket": {
                "subject": "[{{app.name}}] {{event.message | truncate:120}}",
                "comment": { "body": "{{event.exception}}\n\n{{link.event}}" }
              }
            }
            """,
            "$.ticket.id", "$.ticket.url",
            "https://your-domain.zendesk.com/api/v2/tickets.json"),

        new("slack", "Slack", "application/json",
            """
            { "text": "*[{{app.name}}]* {{event.level}}: {{event.message | truncate:200}}\n<{{link.event}}|View in Logrr>" }
            """,
            null, null,
            "https://hooks.slack.com/services/T000/B000/XXXX"),

        new("teams", "Microsoft Teams", "application/json",
            """
            { "text": "[{{app.name}}] {{event.level}}: {{event.message | truncate:200}} — {{link.event}}" }
            """,
            null, null,
            "https://outlook.office.com/webhook/..."),

        new("generic", "Generic JSON", "application/json",
            """
            {
              "app": "{{app.id}}",
              "level": "{{event.level}}",
              "message": "{{event.message}}",
              "exception": "{{event.exception}}",
              "count": {{occurrence.count}},
              "link": "{{link.event}}"
            }
            """,
            "$.id", "$.url",
            "https://example.com/webhook"),
    ];
}
