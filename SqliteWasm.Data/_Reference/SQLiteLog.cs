/********************************************************
 * ADO.NET 2.0 Data Provider for SQLite Version 3.X
 * Written by Robert Simpson (robert@blackcastlesoft.com)
 *
 * Released to the public domain, use at your own risk!
 ********************************************************/

#if PLATFORM_COMPACTFRAMEWORK
using EventWaitHandle = System.Threading.ManualResetEvent;
#endif

namespace System.Data.SQLite.Wasm
{
    using System;
    using System.Data.Common;
    using System.Diagnostics;
    using System.Globalization;
    using System.Threading;

    /// <summary>
    /// Event data for logging event handlers.
    /// </summary>
    public class LogEventArgs : EventArgs
    {
        /// <summary>
        /// The error code.  The type of this object value should be
        /// <see cref="Int32" /> or <see cref="SQLiteErrorCode" />.
        /// </summary>
        public readonly object ErrorCode;

        /// <summary>
        /// SQL statement text as the statement first begins executing
        /// </summary>
        public readonly string Message;

        /// <summary>
        /// Extra data associated with this event, if any.
        /// </summary>
        public readonly object Data;

        /// <summary>
        /// Constructs the object.
        /// </summary>
        /// <param name="pUserData">Should be null.</param>
        /// <param name="errorCode">
        /// The error code.  The type of this object value should be
        /// <see cref="Int32" /> or <see cref="SQLiteErrorCode" />.
        /// </param>
        /// <param name="message">The error message, if any.</param>
        /// <param name="data">The extra data, if any.</param>
        internal LogEventArgs(
            IntPtr pUserData,
            object errorCode,
            string message,
            object data
            )
        {
            ErrorCode = errorCode;
            Message = message;
            Data = data;
        }
    }

    ///////////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Raised when a log event occurs.
    /// </summary>
    /// <param name="sender">The current connection</param>
    /// <param name="e">Event arguments of the trace</param>
    public delegate void SQLiteLogEventHandler(object sender, LogEventArgs e);

    ///////////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Manages the SQLite custom logging functionality and the associated
    /// callback for the whole process.
    /// </summary>
    public static class SQLiteLog
    {
        #region Private Constants
        /// <summary>
        /// Maximum number of milliseconds a non-primary thread should wait
        /// for the <see cref="PrivateInitialize" /> method to be completed.
        /// </summary>
        private const int _initializeTimeout = 1000;
        #endregion

        ///////////////////////////////////////////////////////////////////////

        #region Private Data
        /// <summary>
        /// Object used to synchronize access to the static instance data
        /// for this class.
        /// </summary>
        private static object syncRoot = new object();

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// This will be signaled when the <see cref="PrivateInitialize" />
        /// method has been completed.
        /// </summary>
        private static EventWaitHandle _initializeEvent;

        ///////////////////////////////////////////////////////////////////////

#if !PLATFORM_COMPACTFRAMEWORK
        /// <summary>
        /// Member variable to store the AppDomain.DomainUnload event handler.
        /// </summary>
        private static EventHandler _domainUnload;
#endif

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Member variable to store the application log handler to call.
        /// </summary>
        private static event SQLiteLogEventHandler _handlers;

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// The default log event handler.
        /// </summary>
        private static SQLiteLogEventHandler _defaultHandler;

        ///////////////////////////////////////////////////////////////////////

#if !USE_INTEROP_DLL || !INTEROP_LOG
        /// <summary>
        /// The log callback passed to native SQLite engine.  This must live
        /// as long as the SQLite library has a pointer to it.
        /// </summary>
        private static SQLiteLogCallback _callback;

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// The base SQLite object to interop with.
        /// </summary>
        private static SQLiteBase _sql;
#endif

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// The number of times that the <see cref="Initialize(string)" />
        /// method has been called when the logging subystem was actually
        /// eligible to be initialized (i.e. without the "No_SQLiteLog"
        /// environment variable being set).
        /// </summary>
        private static int _initializeCallCount;

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// The number of times that the <see cref="Uninitialize()" /> method
        /// has been called.
        /// </summary>
        private static int _uninitializeCallCount;

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// The number of times that the <see cref="Initialize(string)" />
        /// method has been completed (i.e. without the "No_SQLiteLog"
        /// environment variable being set).
        /// </summary>
        private static int _initializeDoneCount;

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// This will be non-zero if an attempt was already made to initialize
        /// the (managed) logging subsystem.
        /// </summary>
        private static int _attemptedInitialize;

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// This will be non-zero if logging is currently enabled.
        /// </summary>
        private static bool _enabled;
        #endregion

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Creates the <see cref="EventWaitHandle" /> that will be used to
        /// signal completion of the <see cref="PrivateInitialize" /> method,
        /// if necessary, and then returns it.
        /// </summary>
        /// <returns>
        /// The <see cref="EventWaitHandle" /> that will be used to signal
        /// completion of the <see cref="PrivateInitialize" /> method.
        /// </returns>
        private static EventWaitHandle CreateAndOrGetTheEvent()
        {
            bool created = false;
            EventWaitHandle newEvent = null;

            try
            {
                EventWaitHandle oldEvent;

                oldEvent = Interlocked.CompareExchange(
                    ref _initializeEvent, null, null);

                if (oldEvent == null)
                {
                    newEvent = new ManualResetEvent(false);

                    oldEvent = Interlocked.CompareExchange(
                        ref _initializeEvent, newEvent, null);
                }

                if (oldEvent == null)
                {
                    oldEvent = newEvent;
                    created = true;
                }

                return oldEvent;
            }
            finally
            {
                if (!created && (newEvent != null))
                {
                    newEvent.Close();
                    newEvent = null;
                }
            }
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// This method checks to see if the SQLite core library allows its
        /// native logging callback to be changed when it has already been
        /// initialized, either explicitly or implicitly.
        /// </summary>
        /// <returns>
        /// Non-zero if the SQLite core library initialization state can be
        /// safely ignored when setting up logging; otherwise, zero.
        /// </returns>
        private static bool CanIgnoreIsInitialized()
        {
            try
            {
                if (UnsafeNativeMethods.sqlite3_libversion_number() >= 3042000)
                    return true;
            }
            catch
            {
                // do nothing.
            }

            return false;
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Initializes the SQLite logging facilities.
        /// </summary>
        public static void Initialize()
        {
            Initialize(null);
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Initializes the SQLite logging facilities -OR- waits for the
        /// SQLite logging facilities to be initialized by another thread.
        /// </summary>
        /// <param name="className">
        /// The name of the managed class that called this method.  This
        /// parameter may be null.
        /// </param>
        internal static void Initialize(
            string className
            )
        {
            //
            // NOTE: See if the logging subsystem has been totally disabled.
            //       If so, do nothing.
            //
            if (UnsafeNativeMethods.GetSettingValue(
                    "No_SQLiteLog", null) != null)
            {
                return;
            }

            ///////////////////////////////////////////////////////////////////

            //
            // NOTE: Before doing anything else, see if this method was
            //       fully completed before.  If so, do nothing.
            //
            EventWaitHandle theEvent = CreateAndOrGetTheEvent();

            if (Interlocked.Increment(ref _initializeDoneCount) == 1)
            {
                bool success = false;

                try
                {
                    success = PrivateInitialize(className);
                }
                finally
                {
                    if (theEvent != null)
                        theEvent.Set();

                    if (!success)
                        Interlocked.Decrement(ref _initializeDoneCount);
                }
            }
            else
            {
                Interlocked.Decrement(ref _initializeDoneCount);
            }

            if ((theEvent != null) &&
                !theEvent.WaitOne(_initializeTimeout, false))
            {
#if !NET_COMPACT_20
                HelperMethods.Trace(HelperMethods.StringFormat(
                    CultureInfo.CurrentCulture,
                    "TIMED OUT ({0}) waiting for logging subsystem",
                    _initializeTimeout), TraceCategory.Log);
#endif
            }
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Initializes the SQLite logging facilities.
        /// </summary>
        /// <param name="className">
        /// The name of the managed class that called this method.  This
        /// parameter may be null.
        /// </param>
        /// <returns>
        /// Non-zero if everything was fully initialized successfully.
        /// </returns>
        private static bool PrivateInitialize(
            string className
            )
        {
            //
            // NOTE: Keep track of exactly how many times this method is
            //       called (i.e. per-AppDomain, of course).
            //
            Interlocked.Increment(ref _initializeCallCount);

            ///////////////////////////////////////////////////////////////////

            //
            // NOTE: First, check if the managed logging subsystem is always
            //       supposed to at least attempt to initialize itself.  In
            //       order to do this, several fairly complex steps must be
            //       taken, including calling a P/Invoke (interop) method;
            //       therefore, by default, attempt to perform these steps
            //       once.
            //
            if (UnsafeNativeMethods.GetSettingValue(
                    "Initialize_SQLiteLog", null) == null)
            {
                if (Interlocked.Increment(ref _attemptedInitialize) > 1)
                {
                    Interlocked.Decrement(ref _attemptedInitialize);
                    return false;
                }
            }

            ///////////////////////////////////////////////////////////////////

            //
            // BUFXIX: Cannot initialize the logging interface if the SQLite
            //         core library has already been initialized anywhere in
            //         the process (see ticket [2ce0870fad]).
            //
            if (!CanIgnoreIsInitialized() && SQLite3.StaticIsInitialized())
                return false;

            ///////////////////////////////////////////////////////////////////

#if !PLATFORM_COMPACTFRAMEWORK
            //
            // BUGFIX: To avoid nasty situations where multiple AppDomains are
            //         attempting to initialize and/or shutdown what is really
            //         a shared native resource (i.e. the SQLite core library
            //         is loaded per-process and has only one logging callback,
            //         not one per-AppDomain, which it knows nothing about),
            //         prevent all non-default AppDomains from registering a
            //         log handler unless the "Force_SQLiteLog" environment
            //         variable is used to manually override this safety check.
            //
            if (!AppDomain.CurrentDomain.IsDefaultAppDomain() &&
                UnsafeNativeMethods.GetSettingValue("Force_SQLiteLog", null) == null)
            {
                return false;
            }
#endif

            ///////////////////////////////////////////////////////////////////

            lock (syncRoot)
            {
                //
                // BUFXIX: In order to prevent a subtle race condition within
                //         this method, check (again) if the core library has
                //         already been initialized anywhere in the process,
                //         this time while holding the lock.
                //
                if (!CanIgnoreIsInitialized() && SQLite3.StaticIsInitialized())
                    return false;

                ///////////////////////////////////////////////////////////////

#if !PLATFORM_COMPACTFRAMEWORK
                //
                // NOTE: Add an event handler for the DomainUnload event so
                //       that we can unhook our logging managed function
                //       pointer from the native SQLite code prior to it
                //       being invalidated.
                //
                // BUGFIX: Make sure this event handler is only added one
                //         time (per-AppDomain).
                //
                if (_domainUnload == null)
                {
                    _domainUnload = new EventHandler(DomainUnload);
                    AppDomain.CurrentDomain.DomainUnload += _domainUnload;
                }
#endif

                ///////////////////////////////////////////////////////////////

#if USE_INTEROP_DLL && INTEROP_LOG
                //
                // NOTE: Attempt to setup interop assembly log callback.
                //       This may fail, e.g. if the SQLite core library
                //       has somehow been initialized.  An exception will
                //       be raised in that case.
                //
                SQLiteErrorCode rc = SQLite3.ConfigureLogForInterop(
                    className);

                if (rc != SQLiteErrorCode.Ok)
                {
                    throw new SQLiteException(rc,
                        "Failed to configure interop assembly logging.");
                }
#else
                //
                // NOTE: Create an instance of the SQLite wrapper class.
                //
                if (_sql == null)
                {
                    _sql = new SQLite3(
                        SQLiteDateFormats.Default, DateTimeKind.Unspecified,
                        null, IntPtr.Zero, null, false);
                }

                //
                // NOTE: Create a single "global" (i.e. per-process) callback
                //       to register with SQLite.  This callback will pass the
                //       event on to any registered handler.  We only want to
                //       do this once.
                //
                if (_callback == null)
                {
                    _callback = new SQLiteLogCallback(LogCallback);

                    SQLiteErrorCode rc = _sql.SetLogCallback(_callback);

                    if (rc != SQLiteErrorCode.Ok)
                    {
                        _callback = null; /* UNDO */

                        throw new SQLiteException(rc,
                            "Failed to configure managed assembly logging.");
                    }
                }
#endif

                ///////////////////////////////////////////////////////////////

                //
                // NOTE: Logging is enabled by default unless the configuration
                //       setting "Disable_SQLiteLog" is present.
                //
                if (UnsafeNativeMethods.GetSettingValue(
                        "Disable_SQLiteLog", null) == null)
                {
                    _enabled = true;
                }

                ///////////////////////////////////////////////////////////////

                //
                // NOTE: For now, always setup the default log event handler.
                //
                AddDefaultHandler();
            }

            ///////////////////////////////////////////////////////////////////

            return true;
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Uninitializes the SQLite logging facilities.
        /// </summary>
        public static void Uninitialize()
        {
            Uninitialize(null, false);
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Uninitializes the SQLite logging facilities.
        /// </summary>
        /// <param name="className">
        /// The name of the managed class that called this method.  This
        /// parameter may be null.
        /// </param>
        /// <param name="shutdown">
        /// Non-zero if the native SQLite library should be shutdown prior
        /// to attempting to unset its logging callback.
        /// </param>
        internal static void Uninitialize(
            string className,
            bool shutdown
            )
        {
            //
            // NOTE: Keep track of exactly how many times this method is
            //       called (i.e. per-AppDomain, of course).
            //
            Interlocked.Increment(ref _uninitializeCallCount);

            ///////////////////////////////////////////////////////////////////

            lock (syncRoot)
            {
                //
                // NOTE: Remove the default log event handler.
                //
                RemoveDefaultHandler();

                ///////////////////////////////////////////////////////////////

                //
                // NOTE: Disable logging.  If necessary, it can be re-enabled
                //       later by the Initialize method.
                //
                _enabled = false;

                ///////////////////////////////////////////////////////////////

#if USE_INTEROP_DLL && INTEROP_LOG
                //
                // NOTE: Attempt to unset interop assembly log callback.
                //       This may fail, e.g. if the SQLite core library
                //       has somehow been initialized.  An exception will
                //       be raised in that case.
                //
                SQLiteErrorCode rc = SQLite3.UnConfigureLogForInterop(
                    className);

                if (rc != SQLiteErrorCode.Ok)
                {
                    throw new SQLiteException(rc,
                        "Failed to unconfigure interop assembly logging.");
                }
#else
                //
                // BUGBUG: This may cause serious problems if other AppDomains
                //         have any open SQLite connections; however, there is
                //         currently no way around this limitation.
                //
                if (_sql != null)
                {
                    SQLiteErrorCode rc;

                    if (shutdown)
                    {
                        rc = _sql.Shutdown();

                        if (rc != SQLiteErrorCode.Ok)
                            throw new SQLiteException(rc,
                                "Failed to shutdown interface.");
                    }

                    rc = _sql.SetLogCallback(null);

                    if (rc != SQLiteErrorCode.Ok)
                        throw new SQLiteException(rc,
                            "Failed to shutdown logging.");
                }

                ///////////////////////////////////////////////////////////////

                //
                // BUGFIX: Make sure to reset the callback for next time.  This
                //         must be done after it has been succesfully removed
                //         as logging callback by the SQLite core library as we
                //         cannot allow native code to refer to a delegate that
                //         has been garbage collected.
                //
                if (_callback != null)
                {
                    _callback = null;
                }
#endif

                ///////////////////////////////////////////////////////////////

#if !PLATFORM_COMPACTFRAMEWORK
                //
                // NOTE: Remove the event handler for the DomainUnload event
                //       that we added earlier.
                //
                if (_domainUnload != null)
                {
                    AppDomain.CurrentDomain.DomainUnload -= _domainUnload;
                    _domainUnload = null;
                }
#endif

                ///////////////////////////////////////////////////////////////

                //
                // NOTE: Reset the event for this AppDomain so that future
                //       (non-primary) threads will once again wait for the
                //       PrivateInitialize method to be completed.
                //
                EventWaitHandle theEvent = CreateAndOrGetTheEvent();

                if (theEvent != null)
                    theEvent.Reset();

            }
        }

        ///////////////////////////////////////////////////////////////////////

#if !PLATFORM_COMPACTFRAMEWORK
        /// <summary>
        /// Handles the AppDomain being unloaded.
        /// </summary>
        /// <param name="sender">Should be null.</param>
        /// <param name="e">The data associated with this event.</param>
        private static void DomainUnload(
            object sender,
            EventArgs e
            )
        {
            Uninitialize(null, true);
        }
#endif

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// This event is raised whenever SQLite raises a logging event.
        /// Note that this should be set as one of the first things in the
        /// application.
        /// </summary>
        public static event SQLiteLogEventHandler Log
        {
            add
            {
                lock (syncRoot)
                {
                    // Remove any copies of this event handler from registered
                    // list.  This essentially means that a handler will be
                    // called only once no matter how many times it is added.
                    _handlers -= value;

                    // Add this to the list of event handlers.
                    _handlers += value;
                }
            }
            remove
            {
                lock (syncRoot)
                {
                    _handlers -= value;
                }
            }
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// If this property is true, logging is enabled; otherwise, logging is
        /// disabled.  When logging is disabled, no logging events will fire.
        /// </summary>
        public static bool Enabled
        {
            get { lock (syncRoot) { return InternalEnabled; } }
            set { lock (syncRoot) { InternalEnabled = value; } }
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// If this property is true, logging is enabled; otherwise, logging is
        /// disabled.  When logging is disabled, no logging events will fire.
        /// For internal use only.
        /// </summary>
        internal static bool InternalEnabled
        {
            get { return _enabled; }
            set { _enabled = value; }
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Log a message to all the registered log event handlers without going
        /// through the SQLite library.
        /// </summary>
        /// <param name="message">The message to be logged.</param>
        public static void LogMessage(
            string message
            )
        {
            LogMessage(null, message);
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Log a message to all the registered log event handlers without going
        /// through the SQLite library.
        /// </summary>
        /// <param name="errorCode">The SQLite error code.</param>
        /// <param name="message">The message to be logged.</param>
        public static void LogMessage(
            SQLiteErrorCode errorCode,
            string message
            )
        {
            LogMessage((object)errorCode, message);
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Log a message to all the registered log event handlers without going
        /// through the SQLite library.
        /// </summary>
        /// <param name="errorCode">The integer error code.</param>
        /// <param name="message">The message to be logged.</param>
        public static void LogMessage(
            int errorCode,
            string message
            )
        {
            LogMessage((object)errorCode, message);
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Log a message to all the registered log event handlers without going
        /// through the SQLite library.
        /// </summary>
        /// <param name="errorCode">
        /// The error code.  The type of this object value should be
        /// System.Int32 or SQLiteErrorCode.
        /// </param>
        /// <param name="message">The message to be logged.</param>
        private static void LogMessage(
            object errorCode,
            string message
            )
        {
            bool enabled;
            SQLiteLogEventHandler handlers;

            lock (syncRoot)
            {
                if (_enabled && (_handlers != null))
                {
                    enabled = true;
                    handlers = _handlers.Clone() as SQLiteLogEventHandler;
                }
                else
                {
                    enabled = false;
                    handlers = null;
                }
            }

            if (enabled && (handlers != null))
                handlers(null, new LogEventArgs(
                    IntPtr.Zero, errorCode, message, null));
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Creates and initializes the default log event handler.
        /// </summary>
        private static void InitializeDefaultHandler()
        {
            lock (syncRoot)
            {
                if (_defaultHandler == null)
                    _defaultHandler = new SQLiteLogEventHandler(LogEventHandler);
            }
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Adds the default log event handler to the list of handlers.
        /// </summary>
        public static void AddDefaultHandler()
        {
            lock (syncRoot)
            {
                InitializeDefaultHandler();
                Log += _defaultHandler;
            }
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Removes the default log event handler from the list of handlers.
        /// </summary>
        public static void RemoveDefaultHandler()
        {
            lock (syncRoot)
            {
                InitializeDefaultHandler();
                Log -= _defaultHandler;
            }
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Internal proxy function that calls any registered application log
        /// event handlers.
        ///
        /// WARNING: This method is used more-or-less directly by native code,
        ///          do not modify its type signature.
        /// </summary>
        /// <param name="pUserData">
        /// The extra data associated with this message, if any.
        /// </param>
        /// <param name="errorCode">
        /// The error code associated with this message.
        /// </param>
        /// <param name="pMessage">
        /// The message string to be logged.
        /// </param>
        private static void LogCallback(
            IntPtr pUserData,
            int errorCode,
            IntPtr pMessage
            )
        {
            bool enabled;
            SQLiteLogEventHandler handlers;

            lock (syncRoot)
            {
                if (_enabled && (_handlers != null))
                {
                    enabled = true;
                    handlers = _handlers.Clone() as SQLiteLogEventHandler;
                }
                else
                {
                    enabled = false;
                    handlers = null;
                }
            }

            if (enabled && (handlers != null))
                handlers(null, new LogEventArgs(pUserData, errorCode,
                    SQLiteBase.UTF8ToString(pMessage, -1), null));
        }

        ///////////////////////////////////////////////////////////////////////

        /// <summary>
        /// Default logger.  Currently, uses the Trace class (i.e. sends events
        /// to the current trace listeners, if any).
        /// </summary>
        /// <param name="sender">Should be null.</param>
        /// <param name="e">The data associated with this event.</param>
        private static void LogEventHandler(
            object sender,
            LogEventArgs e
            )
        {
#if !NET_COMPACT_20
            if (e == null)
                return;

            string message = e.Message;

            if (message == null)
            {
                message = "<null>";
            }
            else
            {
                message = message.Trim();

                if (message.Length == 0)
                    message = "<empty>";
            }

            object errorCode = e.ErrorCode;
            string type = "error";

            if ((errorCode is SQLiteErrorCode) || (errorCode is int))
            {
                SQLiteErrorCode rc = (SQLiteErrorCode)(int)errorCode;

                rc &= SQLiteErrorCode.NonExtendedMask;

                if (rc == SQLiteErrorCode.Ok)
                {
                    type = "message";
                }
                else if (rc == SQLiteErrorCode.Notice)
                {
                    type = "notice";
                }
                else if (rc == SQLiteErrorCode.Warning)
                {
                    type = "warning";
                }
                else if ((rc == SQLiteErrorCode.Row) ||
                    (rc == SQLiteErrorCode.Done))
                {
                    type = "data";
                }
            }
            else if (errorCode == null)
            {
                type = "trace";
            }

            if ((errorCode != null) &&
                !Object.ReferenceEquals(errorCode, String.Empty))
            {
                HelperMethods.Trace(HelperMethods.StringFormat(
                    CultureInfo.CurrentCulture, "SQLite {0} ({1}): {2}",
                    type, errorCode, message), TraceCategory.Log);
            }
            else
            {
                HelperMethods.Trace(HelperMethods.StringFormat(
                    CultureInfo.CurrentCulture, "SQLite {0}: {1}",
                    type, message), TraceCategory.Log);
            }
#endif
        }
    }
}
