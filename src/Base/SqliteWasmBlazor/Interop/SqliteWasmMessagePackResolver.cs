using MessagePack;

namespace SqliteWasmBlazor;

/// <summary>
/// Names this assembly's generated MessagePack resolver.
/// </summary>
/// <remarks>
/// Without it the source generator emits <c>MessagePack.GeneratedMessagePackResolver</c>,
/// which is the name every other assembly's generator also picks. That was
/// harmless while this package had no <c>[MessagePackObject]</c> types of its
/// own; <see cref="MessagePackFileHeaderV2"/> moving in gave it one, and
/// <c>SqliteWasmBlazor.Crypto</c> — which references this package and generates
/// its own — then compiles against two types of the same name (CS0436).
/// </remarks>
[GeneratedMessagePackResolver]
internal partial class SqliteWasmMessagePackResolver;
