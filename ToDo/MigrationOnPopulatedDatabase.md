# Migrating a populated database has never been exercised

`InitializeSqliteWasmDatabaseAsync` applies pending migrations at startup, and
`docs/advanced-features.md` tells consumers that EF Core migrations "work
normally". Nothing in this repository has ever confirmed that against a
database with data in it.

## Why the demo cannot confirm it

The demo does not migrate. Its answer to a schema that no longer matches the
model is to report a mismatch and ask the user to reset the pool, and schema
changes here are made by regenerating `InitialCreate` rather than by adding a
migration on top. That is a coherent stance for a sample whose data is
generated on demand — and it means the demo only ever runs a migration against
an empty database, where it costs nothing and can hardly fail.

So the one path a real consumer depends on is the one the sample never walks.

## What is untested

- **Cost on real data.** The active-todos partial index in `InitialCreate` is a
  useful yardstick: building it over 4M rows measured 1.32 s locally, plain. On
  an encrypted pool expect several times that — every page read and written goes
  through ChaCha20-Poly1305, and the browse-path measurements put that multiplier
  near 8.6x for scan-bound work. A consumer's migration doing comparable work
  stalls the boot path for seconds with the UI already up and no indication why.
- **Failure and interruption.** A migration that throws part way, or a tab closed
  mid-migration, has never been exercised. `__EFMigrationsHistory` recovery is
  covered for *missing* history (`MigrationRecovery_*` tests) but not for a
  migration that failed while applying.
- **Encrypted pools.** Migrations write through the encryption VFS like anything
  else. Nothing suggests they behave differently; nothing has confirmed it.

## What to do

Cover it in the TestApp, which exists to walk paths the demo cannot:

1. Seed a table large enough that the migration takes measurable time.
2. Apply a migration to it — plain pool and encrypted pool.
3. Assert the schema changed, the data survived, and record the duration so a
   regression in migration cost is visible rather than merely felt.
4. Interrupt one part-way and assert what the next boot does.

Open question worth settling alongside: whether a migration that takes seconds
should report progress through `IDbInitializationStatus`, or whether a boot-path
migration that slow is itself the thing to warn consumers about.
