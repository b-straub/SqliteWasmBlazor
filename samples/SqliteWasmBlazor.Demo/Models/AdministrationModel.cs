using System.Diagnostics;
using MessagePack;
using Microsoft.EntityFrameworkCore;
using RxBlazorV2.Interface;
using RxBlazorV2.Model;
using RxBlazorV2.MudBlazor.Components;
using SqliteWasmBlazor.Crypto.UI.Components.Encryption;
using SqliteWasmBlazor.Models;
using SqliteWasmBlazor.Models.DTOs;
using SqliteWasmBlazor.Models.Extensions;

namespace SqliteWasmBlazor.Demo.Models;

/// <summary>
/// The Administration page's model: FTS5 index maintenance of its own, plus
/// the encryption panel's reset card reached through a model reference.
///
/// <para>
/// The FTS5 operations are commands rather than click handlers because they
/// are slow: a rebuild walks every row, and on a database with real content
/// that is long enough for a screen where nothing moves to read as a screen
/// where nothing is happening. A command carries its own <c>Executing</c> state, so
/// <c>MudButtonAsyncRx</c> spins, disables itself, and offers cancellation
/// without a busy flag anywhere in the page.
/// </para>
///
/// <para>
/// <b>Why the reference.</b> A component is reactive for exactly one model,
/// and it is the component's re-render that makes a bound
/// <c>MudButtonAsyncRx</c> show its command's <c>Executing</c> state — the
/// button reads that at render time rather than subscribing itself. So a
/// command reached through a second, merely injected model never animates.
/// Models compose through the partial constructor instead: the page binds
/// this model, this model references <see cref="EncryptionModel"/>, and both
/// sets of buttons live under one reactive component. RXBG061 forbids
/// composing two <c>*ModelComponent</c> panels in one assembly; it says
/// nothing about a model referencing another model, and RXBG052 restricts
/// only component-trigger hook generation across assemblies, not the
/// reference itself.
/// </para>
/// </summary>
[ObservableModelScope(ModelScope.Scoped)]
[ObservableComponent]
public partial class AdministrationModel : ObservableModel
{
    public partial AdministrationModel(
        IDbContextFactory<TodoDbContext> contextFactory,
        ISqliteWasmDatabaseService databaseService,
        StatusModel statusModel,
        EncryptionModel encryption);

    // Encryption is reached by path from the markup (Model.Encryption.Reset,
    // .ResetLabel, .ResetHint, .ResetConfirmation, .Localizer). That is not a
    // style choice: the generator merges the referenced model's stream into
    // this one re-prefixed as "Model.Encryption.X", and the component's
    // Filter() is built from the names the markup uses. A pass-through
    // property here would put "Model.ResetPool" in that filter — a name
    // nothing ever emits — and the reset card would stop re-rendering.

    /// <summary>How many rows the next generation run writes.</summary>
    public partial int EntryCount { get; set; } = 100;

    /// <summary>Rows written so far by the running generation.</summary>
    public partial int GenerateProgress { get; set; }

    /// <summary>Timing line under the card; survives until the next run.</summary>
    public partial string PerformanceInfo { get; set; } = string.Empty;

    public double GenerateProgressPercent =>
        EntryCount > 0 ? (double)GenerateProgress / EntryCount * 100 : 0;

    [ObservableCommand(nameof(GenerateTestDataAsync))]
    public partial IObservableCommandAsync GenerateTestData { get; }

    /// <summary>
    /// Bulk-insert <see cref="EntryCount"/> rows in batches.
    ///
    /// <para>
    /// Each batch ends on <c>Task.Delay(1)</c> rather than <c>Task.Yield()</c>.
    /// That is the difference between a cancel button that works and one that
    /// does not: a yield posts a continuation the runtime picks straight back
    /// up, so a queued click never runs, while a one-millisecond delay hands
    /// control to the browser's event loop and the click is dispatched. The
    /// progress bar needs the same window to paint.
    /// </para>
    /// </summary>
    private async Task GenerateTestDataAsync(CancellationToken cancellationToken)
    {
        GenerateProgress = 0;
        PerformanceInfo = string.Empty;

        var stopwatch = Stopwatch.StartNew();
        const int batchSize = 10000;
        var totalInserted = 0;

        try
        {
            for (var batchStart = 0; batchStart < EntryCount; batchStart += batchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var count = Math.Min(batchSize, EntryCount - batchStart);

                var header = MessagePackFileHeaderV2.Create<TodoItemDto>(
                    tableName: "TodoItems",
                    primaryKeyColumn: "Id",
                    recordCount: count,
                    mode: 0);

                using var stream = new MemoryStream();
                MessagePackSerializer.Serialize(stream, header);

                for (var i = 0; i < count; i++)
                {
                    var prefix = TaskPrefixes[Random.Next(TaskPrefixes.Length)];
                    var type = TaskTypes[Random.Next(TaskTypes.Length)];

                    var dto = new TodoItemDto
                    {
                        Id = Guid.NewGuid(),
                        Title = $"{prefix} #{batchStart + i + 1}",
                        Description = $"{type} task - {Guid.NewGuid()}",
                        UpdatedAt = DateTime.UtcNow,
                        IsCompleted = Random.Next(100) < 30
                    };

                    MessagePackSerializer.Serialize(stream, dto);
                }

                await DatabaseService.ImportRowsAsync(
                    TodoListModel.DatabaseName, stream.ToArray(), cancellationToken);
                totalInserted += count;
                GenerateProgress = totalInserted;

                await Task.Delay(1, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Reported, not rethrown: the rows already written are real, and
            // the run ends as a completed command rather than a failed one.
            PerformanceInfo = $"Cancelled after {totalInserted:N0} entries.";
            StatusModel.AddWarning(PerformanceInfo, nameof(GenerateTestData));
            return;
        }

        stopwatch.Stop();
        var itemsPerSecond = EntryCount / stopwatch.Elapsed.TotalSeconds;
        var batches = (totalInserted + batchSize - 1) / batchSize;
        PerformanceInfo =
            $"Generated {EntryCount:N0} entries in {stopwatch.ElapsedMilliseconds}ms " +
            $"({itemsPerSecond:F0} items/sec) — {batches} batches.";
        StatusModel.AddSuccess(PerformanceInfo, nameof(GenerateTestData));

        await OptimizeIndexAfterBulkInsertAsync(cancellationToken);
    }

    /// <summary>
    /// An optimize is worth trying after a bulk insert, but it is not what the
    /// user asked for — a failure here degrades to a rebuild, and a failure of
    /// that is a warning rather than a failed generation.
    /// </summary>
    private async Task OptimizeIndexAfterBulkInsertAsync(CancellationToken cancellationToken)
    {
        await using var context = await ContextFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            await context.OptimizeTodoItemFts5IndexAsync(cancellationToken);
        }
        catch
        {
            try
            {
                await context.RebuildTodoItemFts5IndexAsync(cancellationToken);
                StatusModel.AddWarning(
                    "FTS5 index was missing or corrupt after the bulk insert; it has been rebuilt.",
                    nameof(GenerateTestData));
            }
            catch (Exception rebuildFailure)
            {
                StatusModel.AddWarning(
                    $"FTS5 rebuild failed: {rebuildFailure.Message}. Use Rebuild FTS5 Index.",
                    nameof(GenerateTestData));
            }
        }
    }

    private static readonly string[] TaskPrefixes =
        ["Task", "Work", "Project", "Meeting", "Call", "Email", "Review", "Plan", "Design", "Test"];

    private static readonly string[] TaskTypes =
        ["urgent", "important", "routine", "followup", "research", "development", "documentation", "deployment"];

    private static readonly Random Random = new();

    [ObservableCommand(nameof(OptimizeAsync))]
    public partial IObservableCommandAsync Optimize { get; }

    [ObservableCommand(nameof(RebuildAsync))]
    public partial IObservableCommandAsync Rebuild { get; }

    [ObservableCommand(nameof(CheckIntegrityAsync))]
    public partial IObservableCommandAsync CheckIntegrity { get; }

    private async Task OptimizeAsync(CancellationToken cancellationToken)
    {
        var elapsed = await RunAsync(
            (context, ct) => context.OptimizeTodoItemFts5IndexAsync(ct), cancellationToken);
        StatusModel.AddSuccess(
            $"FTS5 index optimized in {elapsed}ms — index size reduced, query performance improved.",
            nameof(Optimize));
    }

    private async Task RebuildAsync(CancellationToken cancellationToken)
    {
        var elapsed = await RunAsync(
            (context, ct) => context.RebuildTodoItemFts5IndexAsync(ct), cancellationToken);
        StatusModel.AddSuccess(
            $"FTS5 index rebuilt in {elapsed}ms — full index reconstruction completed.",
            nameof(Rebuild));
    }

    private async Task CheckIntegrityAsync(CancellationToken cancellationToken)
    {
        var elapsed = await RunAsync(
            (context, ct) => context.CheckTodoItemFts5IntegrityAsync(ct), cancellationToken);
        StatusModel.AddSuccess(
            $"FTS5 integrity check passed in {elapsed}ms — no corruption detected.",
            nameof(CheckIntegrity));
    }

    /// <summary>
    /// Open a context, time the operation, dispose. A throw propagates: the
    /// command routes it to <see cref="StatusModel"/> as an error, which is
    /// what the page's snackbar calls used to do by hand.
    /// </summary>
    private async Task<long> RunAsync(
        Func<TodoDbContext, CancellationToken, ValueTask> operation,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        await using var context = await ContextFactory.CreateDbContextAsync(cancellationToken);
        await operation(context, cancellationToken);
        stopwatch.Stop();
        return stopwatch.ElapsedMilliseconds;
    }
}