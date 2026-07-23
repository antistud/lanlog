using System.Text.Json;
using Logrr.Contracts;
using Logrr.Server.Auth;

namespace Logrr.Server.Ingest;

public static class IngestEndpoints
{
    public static void MapIngestEndpoints(this IEndpointRouteBuilder app, long maxRequestBytes)
    {
        // Seq-compatible CLEF endpoint (SPEC §6.1).
        app.MapPost("/api/events/raw", async (HttpContext http, TokenAuthenticator auth, IngestService ingest) =>
        {
            var authResult = auth.Authenticate(http, TokenScopes.Ingest);
            if (!authResult.Ok)
            {
                return Fail(authResult.Failure);
            }
            if (TooLarge(http, maxRequestBytes))
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            using var reader = new StreamReader(http.Request.Body);
            var body = await reader.ReadToEndAsync();
            var result = ingest.IngestClef(body, authResult.Context!.App);
            return Results.Json(result, statusCode: StatusCodes.Status201Created);
        });

        // Plain-JSON endpoint for legacy clients (SPEC §6.2).
        app.MapPost("/api/v1/events", async (HttpContext http, TokenAuthenticator auth, IngestService ingest) =>
        {
            var authResult = auth.Authenticate(http, TokenScopes.Ingest);
            if (!authResult.Ok)
            {
                return Fail(authResult.Failure);
            }
            if (TooLarge(http, maxRequestBytes))
            {
                return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
            }

            JsonElement root;
            try
            {
                using var doc = await JsonDocument.ParseAsync(http.Request.Body);
                root = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                return Results.BadRequest(new { error = "request body is not valid JSON" });
            }

            var result = ingest.IngestPlain(root, authResult.Context!.App);
            return Results.Json(result, statusCode: StatusCodes.Status201Created);
        });
    }

    private static bool TooLarge(HttpContext http, long max) =>
        http.Request.ContentLength is { } len && len > max;

    private static IResult Fail(AuthFailure failure) => failure switch
    {
        AuthFailure.Forbidden => Results.StatusCode(StatusCodes.Status403Forbidden),
        _ => Results.StatusCode(StatusCodes.Status401Unauthorized),
    };
}
