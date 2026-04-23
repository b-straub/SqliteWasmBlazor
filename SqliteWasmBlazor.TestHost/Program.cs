using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Configure logging - suppress verbose output during tests
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Add services to the container.
builder.Services.AddRazorPages();

var app = builder.Build();

// Support sub-path deployment (e.g. BLAZOR_BASE_PATH=/myapp) for E2E testing.
// UsePathBase strips the prefix from every incoming request so static asset and
// routing middleware don't need to know about it.
var basePath = app.Configuration["BLAZOR_BASE_PATH"] ?? "";
if (!string.IsNullOrEmpty(basePath))
{
    app.UsePathBase(basePath);

    // NOTE: test-only scaffolding — not production-ready.
    // Buffers and rewrites HTML responses so the test app boots correctly under a sub-path.
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path.Value ?? "";
        // Matches "/", "*.html", and extensionless paths (not just HTML) —
        // intentional; over-buffering has no observable side-effect in tests.
        var couldBeHtml = path == "/"
                          || path.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                          || !Path.HasExtension(path);

        if (!couldBeHtml)
        {
            await next(context);
            return;
        }

        // Strip Accept-Encoding (couldBeHtml paths only) so MapStaticAssets serves plain text.
        context.Request.Headers["Accept-Encoding"] = "";

        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        var contentType = context.Response.ContentType ?? "";
        if (context.Response.StatusCode == 200 && contentType.StartsWith("text/html"))
        {
            var html = await new StreamReader(buffer, Encoding.UTF8).ReadToEndAsync();
            // Rewrites the root-href literal; test apps always start with <base href="/>".
            html = html.Replace("<base href=\"/\"", $"<base href=\"{basePath}/\"");
            var bytes = Encoding.UTF8.GetBytes(html);
            context.Response.ContentLength = bytes.Length;
            await originalBody.WriteAsync(bytes);
        }
        else
        {
            await buffer.CopyToAsync(originalBody);
        }
    });
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}
else
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseRouting();

app.MapStaticAssets();
app.MapRazorPages();
app.MapControllers();
app.MapFallbackToFile("index.html");

app.Run();
// Make Program accessible to test project
namespace SqliteWasmBlazor.TestHost
{
    public partial class Program
    {
    }
}
