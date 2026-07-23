// Logrr.Server — ASP.NET Core host. Wired up fully in a later step.
var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();
app.MapGet("/", () => "Logrr");
app.Run();
