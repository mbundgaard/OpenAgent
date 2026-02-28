var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

app.MapGet("/health", () => Results.Ok());

app.Run();

// Make Program accessible to integration tests
public partial class Program;
