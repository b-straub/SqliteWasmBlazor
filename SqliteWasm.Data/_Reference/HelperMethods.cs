/********************************************************
 * ADO.NET 2.0 Data Provider for SQLite Version 3.X
 * Written by Joe Mistachkin (joe@mistachkin.com)
 *
 * Released to the public domain, use at your own risk!
 ********************************************************/

using System;
using System.Diagnostics;
using System.Globalization;

#if NET_40 || NET_45 || NET_451 || NET_452 || NET_46 || NET_461 || NET_462 || NET_47 || NET_471 || NET_472 || NET_48 || NET_STANDARD_20 || NET_STANDARD_21
using System.Runtime.CompilerServices;
#endif

using System.Text;

namespace System.Data.SQLite.Wasm
{
    #region Private Enumerations
    /// <summary>
    /// These are the possible operations used to mutate a flags enumeration
    /// value.
    /// </summary>
    internal enum FlagsOperation
    {
        None = 0x0,
        Add = 0x1,
        Remove = 0x2,
        Set = 0x4,

        Default = Add
    }

    ///////////////////////////////////////////////////////////////////////////
    /// <summary>
    /// These are the trace categories used by various components within this
    /// library.
    /// </summary>
    [Flags()]
    internal enum TraceCategory
    {
        None = 0x0,

        Self = 0x1000,
        Log = 0x2000,
        Connection = 0x4000,
        Detection = 0x8000,
        Handle = 0x10000,
        Preload = 0x20000,
        Shared = 0x40000,
        Statement = 0x80000,
        Warning = 0x100000,
        Verify = 0x200000,
        Complain = 0x400000,
        Exception = 0x800000,
        Crash = 0x1000000,
        Timing = 0x2000000,

        All = Self | Log | Connection | Detection | Handle |
              Preload | Shared | Statement | Warning | Verify |
              Complain | Exception | Crash | Timing,

        Default = Self | Log | Preload | Shared | Warning |
                  Verify | Complain | Exception | Crash | Timing
    }
    #endregion

    ///////////////////////////////////////////////////////////////////////////

    #region Helper Methods Static Class
    /// <summary>
    /// This static class provides some methods that are shared between the
    /// native library pre-loader and other classes.
    /// </summary>
    internal static partial class HelperMethods
    {
        #region Private Constants
        private const string DisplayNullObject = "<nullObject>";
        private const string DisplayEmptyString = "<emptyString>";
        private const string DisplayStringFormat = "\"{0}\"";

        ///////////////////////////////////////////////////////////////////////

        private const string DisplayNullArray = "<nullArray>";
        private const string DisplayEmptyArray = "<emptyArray>";

        ///////////////////////////////////////////////////////////////////////

        private const string TraceDateTimeFormat = "yyyy-MM-dd HH:mm:ss.fffffff";

        ///////////////////////////////////////////////////////////////////////

        private const char ArrayOpen = '[';
        private const string ElementSeparator = ", ";
        private const char ArrayClose = ']';

        ///////////////////////////////////////////////////////////////////////

        private static readonly char[] SpaceChars = {
            '\t', '\n', '\r', '\v', '\f', ' '
        };

        ///////////////////////////////////////////////////////////////////////

        private static readonly char[] SeparatorChars = {
            '\t', '\n', ' ', ','
        };
        #endregion

        ///////////////////////////////////////////////////////////////////////

        #region Private Data
        /// <summary>
        /// This lock is used to protect the static <see cref="isMono" /> and
        /// <see cref="isDotNetCore" /> fields.
        /// </summary>
        private static readonly object staticSyncRoot = new object();

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// This type is only present when running on Mono.
        /// </summary>
        private static readonly string MonoRuntimeType = "Mono.Runtime";

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// This type is only present when running on .NET Core.
        /// </summary>
        private static readonly string DotNetCoreLibType = "System.CoreLib";

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Keeps track of whether we are running on Mono.  Initially null, it
        /// is set by the <see cref="IsMono" /> method on its first call.
        /// Later, it is returned verbatim by the <see cref="IsMono" /> method.
        /// </summary>
        private static bool? isMono = null;

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Keeps track of whether we are running on .NET Core.  Initially null,
        /// it is set by the <see cref="IsDotNetCore" /> method on its first
        /// call.  Later, it is returned verbatim by the
        /// <see cref="IsDotNetCore" /> method.
        /// </summary>
        private static bool? isDotNetCore = null;

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Keeps track of whether we successfully invoked the
        /// <see cref="Debugger.Break" /> method.  Initially null, it is set by
        /// the <see cref="MaybeBreakIntoDebugger" /> method on its first call.
        /// </summary>
        private static bool? debuggerBreak = null;

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// These are the currently enabled categories of trace messages.  If a
        /// trace message belongs to one of these categories, it will be emitted;
        /// otherwise, it will be silently dropped.
        /// </summary>
        private static TraceCategory traceCategories = TraceCategory.Default;

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// This boolean flag will be non-zero when the enabled trace categories
        /// have been setup.
        /// </summary>
        private static bool traceCategoriesSet = false;
        #endregion

        ///////////////////////////////////////////////////////////////////////

        #region Private Methods
        /// <summary>
        /// Determines the ID of the current process.  Only used for debugging.
        /// </summary>
        /// <returns>
        /// The ID of the current process -OR- zero if it cannot be determined.
        /// </returns>
        private static int GetProcessId()
        {
            Process process = Process.GetCurrentProcess();

            if (process == null)
                return 0;

            return process.Id;
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Determines whether or not this assembly is running on Mono.
        /// </summary>
        /// <returns>
        /// Non-zero if this assembly is running on Mono.
        /// </returns>
        private static bool IsMono()
        {
            try
            {
                lock (staticSyncRoot)
                {
                    if (isMono == null)
                        isMono = (Type.GetType(MonoRuntimeType) != null);

                    return (bool)isMono;
                }
            }
            catch
            {
                // do nothing.
            }

            return false;
        }

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Determines if the specified trace category is enabled.  Disabled
        /// trace categories will not have any associated messages emitted.
        /// </summary>
        /// <param name="category">
        /// The trace category to check.
        /// </param>
        /// <returns>
        /// Non-zero if the specified trace category is enabled.
        /// </returns>
#if NET_45 || NET_451 || NET_452 || NET_46 || NET_461 || NET_462 || NET_47 || NET_471 || NET_472 || NET_48 || NET_STANDARD_20 || NET_STANDARD_21
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
#endif
        private static bool IsTraceCategoryEnabled(
            TraceCategory category
            )
        {
            return (traceCategories & category) == category; /* NO-LOCK */
        }
        #endregion

        ///////////////////////////////////////////////////////////////////////

        #region Internal Methods
        /// <summary>
        /// Resets the cached value for the "PreLoadSQLite_BreakIntoDebugger"
        /// configuration setting.
        /// </summary>
        internal static void ResetBreakIntoDebugger()
        {
            lock (staticSyncRoot)
            {
                debuggerBreak = null;
            }
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// If the "PreLoadSQLite_BreakIntoDebugger" configuration setting is
        /// present (e.g. via the environment), give the interactive user an
        /// opportunity to attach a debugger to the current process; otherwise,
        /// do nothing.
        /// </summary>
        /// <param name="enabled">
        /// Non-zero if the caller detected "PreLoadSQLite_BreakIntoDebugger"
        /// configuration setting.
        /// </param>
        internal static void MaybeBreakIntoDebugger(
            bool enabled
            )
        {
            lock (staticSyncRoot)
            {
                if (debuggerBreak != null)
                    return;
            }

            if (enabled)
            {
                //
                // NOTE: Attempt to use the Console in order to prompt the
                //       interactive user (if any).  This may fail for any
                //       number of reasons.  Even in those cases, we still
                //       want to issue the actual request to break into the
                //       debugger.
                //
                try
                {
                    Console.WriteLine(StringFormat(
                        CultureInfo.CurrentCulture,
                        "Attach a debugger to process {0} " +
                        "and press any key to continue.",
                        GetProcessId()));

#if PLATFORM_COMPACTFRAMEWORK
                    Console.ReadLine();
#else
                    Console.ReadKey();
#endif
                }
#if !NET_COMPACT_20 && TRACE_SHARED
                catch (Exception e)
#else
                catch (Exception)
#endif
                {
#if !NET_COMPACT_20 && TRACE_SHARED
                    try
                    {
                        Trace(StringFormat(
                            CultureInfo.CurrentCulture,
                            "Failed to issue debugger prompt, " +
                            "{0} may be unusable: {1}",
                            typeof(Console), e),
                            TraceCategory.Shared); /* throw */
                    }
                    catch
                    {
                        // do nothing.
                    }
#endif
                }

                ///////////////////////////////////////////////////////////////

                try
                {
                    Debugger.Break();

                    lock (staticSyncRoot)
                    {
                        debuggerBreak = true;
                    }
                }
                catch
                {
                    lock (staticSyncRoot)
                    {
                        debuggerBreak = false;
                    }

                    throw;
                }
            }
            else
            {
                //
                // BUGFIX: There is (almost) no point in checking for the
                //         associated configuration setting repeatedly.
                //         Prevent that here by setting the cached value
                //         to false.
                //
                lock (staticSyncRoot)
                {
                    debuggerBreak = false;
                }
            }
        }

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Determines the ID of the current thread.  Only used for debugging.
        /// </summary>
        /// <returns>
        /// The ID of the current thread -OR- zero if it cannot be determined.
        /// </returns>
        internal static int GetThreadId()
        {
#if !PLATFORM_COMPACTFRAMEWORK
            return AppDomain.GetCurrentThreadId();
#else
            return 0;
#endif
        }

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Determines if the current process is running on one of the Windows
        /// [sub-]platforms.
        /// </summary>
        /// <returns>
        /// Non-zero when running on Windows; otherwise, zero.
        /// </returns>
        internal static bool IsWindows()
        {
            PlatformID platformId = Environment.OSVersion.Platform;

            if ((platformId == PlatformID.Win32S) ||
                (platformId == PlatformID.Win32Windows) ||
                (platformId == PlatformID.Win32NT) ||
                (platformId == PlatformID.WinCE))
            {
                return true;
            }

            return false;
        }

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// This is a wrapper around the
        /// <see cref="String.Format(IFormatProvider,String,Object[])" />
        /// method.  On Mono, it has to call the method overload without the
        /// <see cref="IFormatProvider" /> parameter, due to a bug in Mono.
        /// </summary>
        /// <param name="provider">
        /// This is used for culture-specific formatting.
        /// </param>
        /// <param name="format">
        /// The format string.
        /// </param>
        /// <param name="args">
        /// An array the objects to format.
        /// </param>
        /// <returns>
        /// The resulting string.
        /// </returns>
        internal static string StringFormat(
            IFormatProvider provider,
            string format,
            params object[] args
            )
        {
            if (IsMono())
                return String.Format(format, args);
            else
                return String.Format(provider, format, args);
        }

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Determines whether or not this assembly is running on .NET Core.
        /// </summary>
        /// <returns>
        /// Non-zero if this assembly is running on .NET Core.
        /// </returns>
        internal static bool IsDotNetCore()
        {
            try
            {
                lock (staticSyncRoot)
                {
                    if (isDotNetCore == null)
                    {
                        isDotNetCore = (Type.GetType(
                            DotNetCoreLibType) != null);
                    }

                    return (bool)isDotNetCore;
                }
            }
            catch
            {
                // do nothing.
            }

            return false;
        }

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Attempts to determine which trace categories should be enabled based
        /// on the specified string value, which must be in the following general
        /// format:
        ///
        ///     [prefix1][name1][separator1] ... [prefixN][nameN][separatorN]
        ///
        /// The [prefix] portion of each name may be ommited.  When present, it
        /// must be one of the following characters: '+' (plus), '-' (minus),
        /// or '=' (equals).  The [name] is case insensitive and must be one of
        /// names defined in the <see cref="TraceCategory" /> enumeration.  The
        /// [separator] portions are required and must be one of the following
        /// characters: '\t' (horizontal tab), '\n' (line feed), ' ' (space),
        /// or ',' (comma).
        ///
        /// Initially, any [name] will be added to the mask of enabled trace
        /// categories; however, when [prefix] is present, it may alter this
        /// behavior, e.g. the '-' character is used to remove an enabled trace
        /// category and the '=' character is used to reset the mask of enabled
        /// trace categories.  Finally, the '+' character is used to restore the
        /// initial behavior of adding to the mask of enabled trace categories.
        /// </summary>
        /// <param name="value">
        /// The string containing one or more trace categories to enable.  In
        /// general, this value will come from a configuration file -OR- the
        /// process environment.
        /// </param>
        /// <returns>
        /// Mask of enable trace categories -OR- null to indicate an error was
        /// encountered during processing.
        /// </returns>
        internal static TraceCategory? ParseTraceCategories(
            string value /* in */
            )
        {
            if (String.IsNullOrEmpty(value))
                return null;

#if !PLATFORM_COMPACTFRAMEWORK
            string[] parts = value.Split(SeparatorChars,
                StringSplitOptions.RemoveEmptyEntries);
#else
            string[] parts = value.Split(SeparatorChars);
#endif

            if (parts == null)
                return null;

            int length = parts.Length;

            if (length == 0)
                return null;

            TraceCategory categories = TraceCategory.Default;
            FlagsOperation operation = FlagsOperation.Default;

            for (int index = 0; index < length; index++)
            {
                string part = parts[index];

                if (String.IsNullOrEmpty(part))
                    continue;

                part = part.Trim();

                if (String.IsNullOrEmpty(part))
                    continue;

                switch (part[0])
                {
                    case '+':
                        {
                            operation = FlagsOperation.Add;
                            part = part.Substring(1);
                            break;
                        }
                    case '-':
                        {
                            operation = FlagsOperation.Remove;
                            part = part.Substring(1);
                            break;
                        }
                    case '=':
                        {
                            operation = FlagsOperation.Set;
                            part = part.Substring(1);
                            break;
                        }
                }

                part = part.Trim();

                if (String.IsNullOrEmpty(part))
                    continue;

                TraceCategory? category = null;

                try
                {
                    category = (TraceCategory)Enum.Parse(
                        typeof(TraceCategory), part, true); /* throw */
                }
                catch (Exception)
                {
                    // do nothing.
                }

                if (category != null)
                {
                    switch (operation)
                    {
                        case FlagsOperation.Add:
                            {
                                categories |= (TraceCategory)category;
                                break;
                            }
                        case FlagsOperation.Remove:
                            {
                                categories &= ~(TraceCategory)category;
                                break;
                            }
                        case FlagsOperation.Set:
                            {
                                categories = (TraceCategory)category;
                                break;
                            }
                    }
                }
            }

            return categories;
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Determines if the enabled trace categories have already been set.
        /// </summary>
        /// <returns>
        /// Non-zero if the enabled trace categories have already been set;
        /// otherwise, zero.
        /// </returns>
        internal static bool AreTraceCategoriesSet()
        {
            return traceCategoriesSet;
        }

        ///////////////////////////////////////////////////////////////////////
        /// <summary>
        /// Changes the set of trace categories that are enabled.  Disabled
        /// trace categories will not have any associated messages emitted.
        /// </summary>
        /// <param name="categories">
        /// The trace categories to enable.  Any trace categories not present
        /// in this mask will be disabled.
        /// </param>
        internal static void SetTraceCategories(
            TraceCategory categories
            )
        {
            traceCategories = categories; /* NO-LOCK */
            traceCategoriesSet = true; /* NO-LOCK */

#if !NET_COMPACT_20
            Trace(String.Format(
                "Trace categories overridden to: {0}", categories),
                TraceCategory.Self);
#endif
        }

        ///////////////////////////////////////////////////////////////////////

#if !NET_COMPACT_20
        /// <summary>
        /// Attempts to emit a message to the tracing subsystem via the
        /// <see cref="System.Diagnostics.Trace.WriteLine(String)" /> method.
        /// </summary>
        /// <param name="message">
        /// The message to emit.
        /// </param>
        /// <param name="category">
        /// The category to which the message belongs.
        /// </param>
        [Conditional("TRACE")]
        internal static void Trace(
            string message,
            TraceCategory category
            )
        {
            if (IsTraceCategoryEnabled(category))
            {
                System.Diagnostics.Trace.WriteLine(
                    String.Format("[{0}] System.Data.SQLite ({1}): {2}",
                    DateTime.Now.ToString(TraceDateTimeFormat), category,
                    message));
            }
        }
#endif

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Attempts to convert an object value to a string suitable for display
        /// to humans.
        /// </summary>
        /// <param name="value">
        /// The object value to create a string representation of.
        /// </param>
        /// <returns>
        /// A string representation of the <paramref name="value" /> parameter
        /// -OR- null if it cannot be determined.
        /// </returns>
        internal static string ToDisplayString(
            object value
            )
        {
            if (value == null)
                return DisplayNullObject;

            string stringValue = value.ToString();

            if (stringValue.Length == 0)
                return DisplayEmptyString;

            if (stringValue.IndexOfAny(SpaceChars) < 0)
                return stringValue;

            return StringFormat(
                CultureInfo.InvariantCulture, DisplayStringFormat,
                stringValue);
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Attempts to convert an array value to a string suitable for display
        /// to humans.
        /// </summary>
        /// <param name="array">
        /// The array value to create a string representation of.
        /// </param>
        /// <returns>
        /// A string representation of the <paramref name="array" /> parameter
        /// -OR- null if it cannot be determined.
        /// </returns>
        internal static string ToDisplayString(
            Array array
            )
        {
            if (array == null)
                return DisplayNullArray;

            if (array.Length == 0)
                return DisplayEmptyArray;

            StringBuilder result = new StringBuilder();

            foreach (object value in array)
            {
                if (result.Length > 0)
                    result.Append(ElementSeparator);

                result.Append(ToDisplayString(value));
            }

            if (result.Length > 0)
            {
#if PLATFORM_COMPACTFRAMEWORK
                result.Insert(0, ArrayOpen.ToString());
#else
                result.Insert(0, ArrayOpen);
#endif

                result.Append(ArrayClose);
            }

            return result.ToString();
        }
        #endregion
    }
    #endregion
}
