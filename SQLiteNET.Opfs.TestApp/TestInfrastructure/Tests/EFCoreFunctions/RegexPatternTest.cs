using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using SqliteWasm.Data.Models;
using SqliteWasm.Data.Models.Models;

namespace SQLiteNET.Opfs.TestApp.TestInfrastructure.Tests.EFCoreFunctions;

/// <summary>
/// Tests EF Core regex function (regexp).
/// This function is registered in the worker and enables LINQ queries with regex matching.
/// Note: Uses Regex.IsMatch() which translates to the regexp() SQL function in SQLite.
/// </summary>
internal class RegexPatternTest(IDbContextFactory<TodoDbContext> factory)
    : SqliteWasmTest(factory)
{
    public override string Name => "EFCoreFunctions_RegexPattern";

    public override async ValueTask<string?> RunTestAsync()
    {
        await using var context = await Factory.CreateDbContextAsync();

        var entities = new[]
        {
            new TypeTestEntity { Id = 1, StringValue = "test@example.com" },
            new TypeTestEntity { Id = 2, StringValue = "user@domain.org" },
            new TypeTestEntity { Id = 3, StringValue = "invalid-email" },
            new TypeTestEntity { Id = 4, StringValue = "admin@company.net" },
            new TypeTestEntity { Id = 5, StringValue = "no-at-sign.com" }
        };

        context.TypeTests.AddRange(entities);
        await context.SaveChangesAsync();

        // Test regexp - email pattern
        var emailPattern = @"^[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}$";
        var emailMatches = await context.TypeTests
            .Where(e => Regex.IsMatch(e.StringValue, emailPattern))
            .Select(e => e.Id)
            .ToListAsync();
        if (!emailMatches.SequenceEqual(new[] { 1, 2, 4 }))
        {
            throw new InvalidOperationException($"Email regex test failed: expected [1,2,4], got [{string.Join(",", emailMatches)}]");
        }

        // Test regexp - contains pattern
        var containsPattern = @"example|company";
        var containsMatches = await context.TypeTests
            .Where(e => Regex.IsMatch(e.StringValue, containsPattern))
            .Select(e => e.Id)
            .ToListAsync();
        if (!containsMatches.SequenceEqual(new[] { 1, 4 }))
        {
            throw new InvalidOperationException($"Contains regex test failed: expected [1,4], got [{string.Join(",", containsMatches)}]");
        }

        return "OK";
    }
}
