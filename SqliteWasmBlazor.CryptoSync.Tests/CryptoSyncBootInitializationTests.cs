using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SqliteWasmBlazor;
using Xunit;

namespace SqliteWasmBlazor.CryptoSync.Tests;

/// <summary>
/// Verifies <see cref="CryptoSyncServiceCollectionExtensions.VerifyCryptoSyncSeedAsync"/>
/// reports the expected typed failure for each seed-state condition and stays
/// <see cref="DbInitState.READY"/> for properly provisioned admin / member
/// devices. <see cref="TestSyncContext"/> ships with a full admin seed via
/// <c>HasData</c>; each test mutates that fixture into the scenario it covers.
/// </summary>
public class CryptoSyncBootInitializationTests : IDisposable
{
    private const string DbName = "BootTest.db";

    private readonly SqliteConnection _connection;
    private readonly TestSyncContext _context;
    private readonly DbInitializationService _status;

    public CryptoSyncBootInitializationTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        var options = new DbContextOptionsBuilder<TestSyncContext>()
            .UseSqlite(_connection)
            .Options;

        _context = new TestSyncContext(options);
        _context.Database.EnsureCreated();

        _status = new DbInitializationService();
        _status.Report(DbInitState.READY);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    [Fact]
    public async Task NoDeviceSettings_ReportsDeviceNotProvisioned()
    {
        var device = await _context.DeviceSettings.SingleAsync();
        _context.DeviceSettings.Remove(device);
        await _context.SaveChangesAsync();

        await CryptoSyncServiceCollectionExtensions.VerifyCryptoSyncSeedAsync(_context, _status, DbName);

        Assert.Equal(DbInitState.FAILED, _status.State);
        var failure = Assert.IsType<DeviceNotProvisionedFailure>(_status.Failure);
        Assert.Equal(DbName, failure.DatabaseName);
    }

    [Fact]
    public async Task AdminDeviceWithoutSystemGroup_ReportsSystemSeedMissing()
    {
        // Fixture already provisions an admin device — only need to remove the
        // system share group to land in the failure case.
        var systemGroup = await _context.ShareGroups
            .SingleAsync(g => g.GroupContext == CryptoSyncBootstrap.SystemGroupContext);
        _context.ShareGroups.Remove(systemGroup);
        await _context.SaveChangesAsync();

        await CryptoSyncServiceCollectionExtensions.VerifyCryptoSyncSeedAsync(_context, _status, DbName);

        Assert.Equal(DbInitState.FAILED, _status.State);
        Assert.IsType<SystemSeedMissingFailure>(_status.Failure);
    }

    [Fact]
    public async Task MemberDeviceWithoutAdminContact_ReportsAdminContactMissing()
    {
        await DemoteToMemberAsync();

        var admin = await _context.Contacts.SingleAsync(c => c.IsAdmin);
        _context.Contacts.Remove(admin);
        await _context.SaveChangesAsync();

        await CryptoSyncServiceCollectionExtensions.VerifyCryptoSyncSeedAsync(_context, _status, DbName);

        Assert.Equal(DbInitState.FAILED, _status.State);
        Assert.IsType<AdminContactMissingFailure>(_status.Failure);
    }

    [Fact]
    public async Task ProperlySeededAdmin_StaysReady()
    {
        await CryptoSyncServiceCollectionExtensions.VerifyCryptoSyncSeedAsync(_context, _status, DbName);

        Assert.Equal(DbInitState.READY, _status.State);
        Assert.Null(_status.Failure);
    }

    [Fact]
    public async Task ProperlySeededMember_StaysReady()
    {
        await DemoteToMemberAsync();

        await CryptoSyncServiceCollectionExtensions.VerifyCryptoSyncSeedAsync(_context, _status, DbName);

        Assert.Equal(DbInitState.READY, _status.State);
        Assert.Null(_status.Failure);
    }

    [Fact]
    public async Task AdminBranch_DoesNotCheckAdminContact()
    {
        // Admin device with system group present but admin contact deleted —
        // admin branch must not consult the contact table, so the boot stays
        // READY. Proves admin/member branches are exclusive.
        var admin = await _context.Contacts.SingleAsync(c => c.IsAdmin);
        _context.Contacts.Remove(admin);
        await _context.SaveChangesAsync();

        await CryptoSyncServiceCollectionExtensions.VerifyCryptoSyncSeedAsync(_context, _status, DbName);

        Assert.Equal(DbInitState.READY, _status.State);
        Assert.Null(_status.Failure);
    }

    private async Task DemoteToMemberAsync()
    {
        var device = await _context.DeviceSettings.SingleAsync();
        device.IsAdmin = false;
        await _context.SaveChangesAsync();
    }
}
