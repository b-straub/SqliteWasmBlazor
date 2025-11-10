var builder = WebApplication.CreateBuilder(args);

// Configure logging - suppress verbose output during tests
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Warning);

// Add services to the container.
builder.Services.AddRazorPages();

var app = builder.Build();

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
