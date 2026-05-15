// SqliteWasmBlazor.CryptoSync - Boot-status failure types for the
// post-migration seed verification stage.

namespace SqliteWasmBlazor.CryptoSync;

/// <summary>
/// No <see cref="DeviceSettings"/> row exists. The database has never been
/// provisioned as a CryptoSync instance — the app should run admin bootstrap
/// (admin device) or accept an invitation handshake (member device) before
/// opening any sync flow.
/// </summary>
public sealed record DeviceNotProvisionedFailure(string DatabaseName) : IDbInitFailure
{
    public string DefaultMessage =>
        "This device is not provisioned. Run admin bootstrap or accept an invitation before opening sync.";
}

/// <summary>
/// <see cref="DeviceSettings.IsAdmin"/> is true but no <see cref="ShareGroup"/>
/// with the system group context exists. Admin bootstrap was incomplete or
/// the row was deleted out-of-band — sync cannot proceed because there is no
/// system CEK to wrap envelopes under.
/// </summary>
public sealed record SystemSeedMissingFailure(string DatabaseName) : IDbInitFailure
{
    public string DefaultMessage =>
        "Admin device is missing the system share group. Bootstrap was incomplete or the database was tampered with.";
}

/// <summary>
/// Member device (<see cref="DeviceSettings.IsAdmin"/> is false) has no
/// <see cref="TrustedContact"/> with <see cref="TrustedContact.IsAdmin"/> set.
/// The invitation handshake never completed — incoming envelopes cannot be
/// admin-verified.
/// </summary>
public sealed record AdminContactMissingFailure(string DatabaseName) : IDbInitFailure
{
    public string DefaultMessage =>
        "No admin contact has been received. Complete the invitation handshake before opening sync.";
}
