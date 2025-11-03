/********************************************************
 * ADO.NET 2.0 Data Provider for SQLite Version 3.X
 * Written by Robert Simpson (robert@blackcastlesoft.com)
 *
 * Released to the public domain, use at your own risk!
 ********************************************************/

namespace System.Data.SQLite.Wasm
{
  using System;
  using System.Collections.Generic;
  using System.Runtime.InteropServices;
  using System.Globalization;

  /// <summary>
  /// These constants are used with the sqlite3_create_function() API, et al.
  /// </summary>
  [Flags()]
  public enum SQLiteFunctionFlags
  {
    /// <summary>
    /// Initial flags value, no flags set.
    /// </summary>
    NONE = 0x0,
    /// <summary>
    /// The function uses UTF-8.
    /// </summary>
    SQLITE_UTF8 = 0x1,
    /// <summary>
    /// The function uses little-endian UTF-16.
    /// </summary>
    SQLITE_UTF16LE = 0x2,
    /// <summary>
    /// The function uses big-endian UTF-16.
    /// </summary>
    SQLITE_UTF16BE = 0x3,
    /// <summary>
    /// The function uses UTF-16 in native byte order.
    /// </summary>
    SQLITE_UTF16 = 0x4,
    /// <summary>
    /// Deprecated, do not use.
    /// </summary>
    SQLITE_ANY = 0x5,
    /// <summary>
    /// For sqlite3_create_collation() only, do not use.
    /// </summary>
    SQLITE_UTF16_ALIGNED = 0x8,
    /// <summary>
    /// Used to mask off the flags related to encodings.
    /// </summary>
    ENCODING_MASK = SQLITE_UTF8 | SQLITE_UTF16LE | SQLITE_UTF16BE |
                    SQLITE_UTF16 | SQLITE_ANY | SQLITE_UTF16_ALIGNED, /* 0xF */
    /// <summary>
    /// The new function always gives the same output when the input parameters
    /// are the same. The abs() function is deterministic, for example, but
    /// randomblob() is not. Functions must be deterministic in order to be used
    /// in certain contexts such as with the WHERE clause of partial indexes or
    /// in generated columns. SQLite might also optimize deterministic functions
    /// by factoring them out of inner loops.
    /// </summary>
    SQLITE_DETERMINISTIC = 0x800,
    /// <summary>
    /// The function may only be invoked from top-level SQL, and cannot be used
    /// in VIEWs or TRIGGERs nor in schema structures such as CHECK constraints,
    /// DEFAULT clauses, expression indexes, partial indexes, or generated columns.
    /// The SQLITE_DIRECTONLY flags is a security feature which is recommended for
    /// all application-defined SQL functions, and especially for functions that
    /// have side-effects or that could potentially leak sensitive information.
    /// </summary>
    SQLITE_DIRECTONLY = 0x80000,
    /// <summary>
    /// The function may call sqlite3_value_subtype() to inspect the sub-types of
    /// its arguments. Specifying this flag makes no difference for scalar or
    /// aggregate user functions. However, if it is not specified for a user-defined
    /// window function, then any sub-types belonging to arguments passed to the
    /// window function may be discarded before the window function is called
    /// (i.e. sqlite3_value_subtype() will always return 0).
    /// </summary>
    SQLITE_SUBTYPE = 0x100000,
    /// <summary>
    /// The function is unlikely to cause problems even if misused. An innocuous
    /// function should have no side effects and should not depend on any values
    /// other than its input parameters. The abs() function is an example of an
    /// innocuous function. The load_extension() SQL function is not innocuous
    /// because of its side effects.
    ///
    /// SQLITE_INNOCUOUS is similar to SQLITE_DETERMINISTIC, but is not exactly
    /// the same. The random() function is an example of a function that is
    /// innocuous but not deterministic.
    ///
    /// Some heightened security settings (SQLITE_DBCONFIG_TRUSTED_SCHEMA and
    /// PRAGMA trusted_schema=OFF) disable the use of SQL functions inside views
    /// and triggers and in schema structures such as CHECK constraints, DEFAULT
    /// clauses, expression indexes, partial indexes, and generated columns unless
    /// the function is tagged with SQLITE_INNOCUOUS. Most built-in functions are
    /// innocuous. Developers are advised to avoid using the SQLITE_INNOCUOUS flag
    /// for application-defined functions unless the function has been carefully
    /// audited and found to be free of potentially security-adverse side-effects
    /// and information-leaks.
    /// </summary>
    SQLITE_INNOCUOUS = 0x200000,
    /// <summary>
    /// The SQLITE_RESULT_SUBTYPE flag indicates to SQLite that a function
    /// might call sqlite3_result_subtype() to cause a sub-type to be
    /// associated with its result.  Every function that invokes
    /// sqlite3_result_subtype() should have this property.  If it does not,
    /// then the call to sqlite3_result_subtype() might become a no-op if the
    /// function is used as term in an expression index.  On the other hand,
    /// SQL functions that never invoke sqlite3_result_subtype() should avoid
    /// setting this property, as the purpose of this property is to disable
    /// certain optimizations that are incompatible with subtypes.
    /// </summary>
    SQLITE_RESULT_SUBTYPE = 0x1000000
  }

  /// <summary>
  /// This abstract class is designed to handle user-defined functions easily.  An instance of the derived class is made for each
  /// connection to the database.
  /// </summary>
  /// <remarks>
  /// Although there is one instance of a class derived from SQLiteFunction per database connection, the derived class has no access
  /// to the underlying connection.  This is necessary to deter implementers from thinking it would be a good idea to make database
  /// calls during processing.
  ///
  /// It is important to distinguish between a per-connection instance, and a per-SQL statement context.  One instance of this class
  /// services all SQL statements being stepped through on that connection, and there can be many.  One should never store per-statement
  /// information in member variables of user-defined function classes.
  ///
  /// For aggregate functions, always create and store your per-statement data in the contextData object on the 1st step.  This data will
  /// be automatically freed for you (and Dispose() called if the item supports IDisposable) when the statement completes.
  /// </remarks>
  public abstract class SQLiteFunction : IDisposable
  {
    private class AggregateData
    {
      internal int _stepCount = 1;
      internal int _inverseCount = 1;
      internal object _data;
    }

    /////////////////////////////////////////////////////////////////////////

    /// <summary>
    /// The base connection this function is attached to
    /// </summary>
    internal SQLiteBase              _base;

    /// <summary>
    /// Internal array used to keep track of aggregate function context data
    /// </summary>
    private Dictionary<IntPtr, AggregateData> _contextDataList;

    /// <summary>
    /// The connection flags associated with this object (this should be the
    /// same value as the flags associated with the parent connection object).
    /// </summary>
    private SQLiteConnectionFlags _flags;

    /// <summary>
    /// Holds a reference to the callback function for user functions
    /// </summary>
    private SQLiteCallback  _InvokeFunc;
    /// <summary>
    /// Holds a reference to the callback function for stepping in an aggregate function
    /// </summary>
    private SQLiteCallback  _StepFunc;
    /// <summary>
    /// Holds a reference to the callback function for finalizing an aggregate function
    /// </summary>
    private SQLiteFinalCallback  _FinalFunc;
    /// <summary>
    /// Holds a reference to the callback function for getting a window function value
    /// </summary>
    private SQLiteFinalCallback _ValueFunc;
    /// <summary>
    /// Holds a reference to the callback function for inverse in a window function
    /// </summary>
    private SQLiteCallback _InverseFunc;

    /// <summary>
    /// Holds a reference to the callback function for collating sequences
    /// </summary>
    private SQLiteCollation _CompareFunc;

    private SQLiteCollation _CompareFunc16;

    /// <summary>
    /// Raw parameter pointers for the current callback.  Only valid during a callback.
    /// </summary>
#if !PLATFORM_COMPACTFRAMEWORK
    private IntPtr[] _params;
#else
    private int[] _params;
#endif

    /// <summary>
    /// Current context of the current callback.  Only valid during a callback
    /// </summary>
    internal IntPtr _context;

    /// <summary>
    /// This static dictionary contains all the registered (known) user-defined
    /// functions declared using the proper attributes.  The contained dictionary
    /// values are always null and are not currently used.
    /// </summary>
    private static IDictionary<SQLiteFunctionAttribute, object> _registeredFunctions;

    /// <summary>
    /// Internal constructor, initializes the function's internal variables.
    /// </summary>
    protected SQLiteFunction()
    {
      _contextDataList = new Dictionary<IntPtr, AggregateData>();
    }

    /// <summary>
    /// Constructs an instance of this class using the specified data-type
    /// conversion parameters.
    /// </summary>
    /// <param name="format">
    /// The DateTime format to be used when converting string values to a
    /// DateTime and binding DateTime parameters.
    /// </param>
    /// <param name="kind">
    /// The <see cref="DateTimeKind" /> to be used when creating DateTime
    /// values.
    /// </param>
    /// <param name="formatString">
    /// The format string to be used when parsing and formatting DateTime
    /// values.
    /// </param>
    /// <param name="utf16">
    /// Non-zero to create a UTF-16 data-type conversion context; otherwise,
    /// a UTF-8 data-type conversion context will be created.
    /// </param>
    protected SQLiteFunction(
        SQLiteDateFormats format,
        DateTimeKind kind,
        string formatString,
        bool utf16
        )
        : this()
    {
        if (utf16)
            _base = new SQLite3_UTF16(format, kind, formatString, IntPtr.Zero, null, false);
        else
            _base = new SQLite3(format, kind, formatString, IntPtr.Zero, null, false);
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    #region IDisposable Members
    /// <summary>
    /// Disposes of any active contextData variables that were not automatically cleaned up.  Sometimes this can happen if
    /// someone closes the connection while a DataReader is open.
    /// </summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
    #endregion

    ///////////////////////////////////////////////////////////////////////////////////////////////

    #region IDisposable "Pattern" Members
    private bool disposed;
    private void CheckDisposed() /* throw */
    {
#if THROW_ON_DISPOSED
        if (disposed)
            throw new ObjectDisposedException(typeof(SQLiteFunction).Name);
#endif
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Placeholder for a user-defined disposal routine
    /// </summary>
    /// <param name="disposing">True if the object is being disposed explicitly</param>
    protected virtual void Dispose(bool disposing)
    {
        if (!disposed)
        {
            if (disposing)
            {
                ////////////////////////////////////
                // dispose managed resources here...
                ////////////////////////////////////

                IDisposable disp;

                foreach (KeyValuePair<IntPtr, AggregateData> kv in _contextDataList)
                {
                    disp = kv.Value._data as IDisposable;
                    if (disp != null)
                        disp.Dispose();
                }
                _contextDataList.Clear();
                _contextDataList = null;

                _flags = SQLiteConnectionFlags.None;

                _InvokeFunc = null;
                _StepFunc = null;
                _FinalFunc = null;
                _ValueFunc = null;
                _InverseFunc = null;
                _CompareFunc = null;
                _base = null;
            }

            //////////////////////////////////////
            // release unmanaged resources here...
            //////////////////////////////////////

            disposed = true;
        }
    }
    #endregion

    ///////////////////////////////////////////////////////////////////////////////////////////////

    #region Destructor
    /// <summary>
    /// Cleans up resources associated with the current instance.
    /// </summary>
    ~SQLiteFunction()
    {
        Dispose(false);
    }
    #endregion

    ///////////////////////////////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Returns a reference to the underlying connection's SQLiteConvert class, which can be used to convert
    /// strings and DateTime's into the current connection's encoding schema.
    /// </summary>
    public SQLiteConvert SQLiteConvert
    {
      get
      {
        CheckDisposed();
        return _base;
      }
    }

    /// <summary>
    /// Scalar functions override this method to do their magic.
    /// </summary>
    /// <remarks>
    /// Parameters passed to functions have only an affinity for a certain data type, there is no underlying schema available
    /// to force them into a certain type.  Therefore the only types you will ever see as parameters are
    /// DBNull.Value, Int64, Double, String or byte[] array.
    /// </remarks>
    /// <param name="args">The arguments for the command to process</param>
    /// <returns>You may return most simple types as a return value, null or DBNull.Value to return null, DateTime, or
    /// you may return an Exception-derived class if you wish to return an error to SQLite.  Do not actually throw the error,
    /// just return it!</returns>
    public virtual object Invoke(object[] args)
    {
      CheckDisposed();
      return null;
    }

    /// <summary>
    /// Aggregate functions override this method to do their magic.
    /// </summary>
    /// <remarks>
    /// Typically you'll be updating whatever you've placed in the contextData field and returning as quickly as possible.
    /// </remarks>
    /// <param name="args">The arguments for the command to process</param>
    /// <param name="stepNumber">The 1-based step number.  This is incrememted each time the step method is called.</param>
    /// <param name="contextData">A placeholder for implementers to store contextual data pertaining to the current context.</param>
    public virtual void Step(object[] args, int stepNumber, ref object contextData)
    {
      CheckDisposed();
    }

    /// <summary>
    /// Aggregate functions override this method to finish their aggregate processing.
    /// </summary>
    /// <remarks>
    /// If you implemented your aggregate function properly,
    /// you've been recording and keeping track of your data in the contextData object provided, and now at this stage you should have
    /// all the information you need in there to figure out what to return.
    /// NOTE:  It is possible to arrive here without receiving a previous call to Step(), in which case the contextData will
    /// be null.  This can happen when no rows were returned.  You can either return null, or 0 or some other custom return value
    /// if that is the case.
    /// </remarks>
    /// <param name="contextData">Your own assigned contextData, provided for you so you can return your final results.</param>
    /// <returns>You may return most simple types as a return value, null or DBNull.Value to return null, DateTime, or
    /// you may return an Exception-derived class if you wish to return an error to SQLite.  Do not actually throw the error,
    /// just return it!
    /// </returns>
    public virtual object Final(object contextData)
    {
      CheckDisposed();
      return null;
    }

    /// <summary>
    /// This method is only required window aggregate functions, not legacy aggregate function implementations. It is invoked to
    /// return the current value of the aggregate. Unlike xFinal, the implementation should not delete any context.
    /// </summary>
    /// <param name="contextData">Your own assigned contextData, provided for you so you can return your final results.</param>
    /// <returns>You may return most simple types as a return value, null or DBNull.Value to return null, DateTime, or
    /// you may return an Exception-derived class if you wish to return an error to SQLite.  Do not actually throw the error,
    /// just return it!
    /// </returns>
    public virtual object Value(object contextData)
    {
      CheckDisposed();
      return null;
    }

    /// <summary>
    /// This method is only required for window aggregate functions, not legacy aggregate function implementations. It is invoked
    /// to remove the oldest presently aggregated result of xStep from the current window. The function arguments, if any, are
    /// those passed to xStep for the row being removed.
    /// </summary>
    /// <param name="args">The arguments for the command to process</param>
    /// <param name="stepNumber">The 1-based step number.  This is incrememted each time the step method is called.</param>
    /// <param name="contextData">A placeholder for implementers to store contextual data pertaining to the current context.</param>
    public virtual void Inverse(object[] args, int stepNumber, ref object contextData)
    {
      CheckDisposed();
    }

    /// <summary>
    /// User-defined collating sequences override this method to provide a custom string sorting algorithm.
    /// </summary>
    /// <param name="param1">The first string to compare.</param>
    /// <param name="param2">The second strnig to compare.</param>
    /// <returns>1 if param1 is greater than param2, 0 if they are equal, or -1 if param1 is less than param2.</returns>
    public virtual int Compare(string param1, string param2)
    {
      CheckDisposed();
      return 0;
    }

    /// <summary>
    /// Gets and returns the sub-type associated with the specified function parameter.
    /// </summary>
    /// <param name="index">
    /// The parameter index to check.
    /// </param>
    /// <returns>
    /// The sub-type associated with the specified function parameter.
    /// </returns>
#pragma warning disable 3002
    public uint GetParameterSubType(int index)
    {
      CheckDisposed();
      CheckParameterIndex(index);
      return _base.GetParamValueSubType((IntPtr)_params[index]);
    }
#pragma warning restore 3002

    /// <summary>
    /// Attempts to convert the specified function parameter to numeric and
    /// then gets and returns the new type affinity associated with this
    /// value.
    /// </summary>
    /// <param name="index">
    /// The parameter index to check.
    /// </param>
    /// <returns>
    /// The new type affinity associated with this value.
    /// </returns>
    public TypeAffinity GetParameterNumericType(int index)
    {
      CheckDisposed();
      CheckParameterIndex(index);
      return _base.GetParamValueNumericType((IntPtr)_params[index]);
    }

    /// <summary>
    /// Gets and returns the "no change" flag associated with the specified
    /// function parameter.  This method can only be used while within a call
    /// to the <see cref="ISQLiteNativeModule.xUpdate" /> method of a virtual
    /// table implementation.
    /// </summary>
    /// <param name="index">
    /// The parameter index to check.
    /// </param>
    /// <returns>
    /// Non-zero if the column associated with the function parameter is
    /// unchanged in an UPDATE against a virtual table.
    /// </returns>
    public int GetParameterNoChange(int index)
    {
      CheckDisposed();
      CheckParameterIndex(index);
      return _base.GetParamValueNoChange((IntPtr)_params[index]);
    }

    /// <summary>
    /// Gets and returns the "from bind" flag associated with the specified
    /// function parameter.
    /// </summary>
    /// <param name="index">
    /// The parameter index to check.
    /// </param>
    /// <returns>
    /// Non-zero if the function parameter at the specified index was
    /// originally specified via a bound parameter.
    /// </returns>
    public int GetParameterFromBind(int index)
    {
      CheckDisposed();
      CheckParameterIndex(index);
      return _base.GetParamValueFromBind((IntPtr)_params[index]);
    }

    /// <summary>
    /// Arranges for the specified sub-type value to be associated
    /// with the function result.
    /// </summary>
    /// <param name="value">
    /// The desired sub-type of the function result.
    /// </param>
    public void SetReturnSubType(uint value)
    {
      CheckDisposed();
      _base.ReturnSubType(_context, value);
    }

    /// <summary>
    /// Performs some sanity checks on the index and how it relates
    /// to the current function parameters.
    /// </summary>
    /// <param name="index">
    /// The parameter index to be checked for bounds, etc.
    /// </param>
    private void CheckParameterIndex(int index)
    {
      if (_params == null)
      {
        throw new InvalidOperationException(
          "parameters are unavailable");
      }

      int length = _params.Length;

      if ((index <= 0) || (index >= length))
      {
        throw new ArgumentException(String.Format(
          "parameter index {0} is out-of-bounds",
          index));
      }

#if !PLATFORM_COMPACTFRAMEWORK
      IntPtr value = _params[index];

      if (value == IntPtr.Zero)
#else
      int value = _params[index];

      if (value == 0)
#endif
      {
        throw new ArgumentException(String.Format(
          "parameter {0} value cannot be null",
          index));
      }
    }

    /// <summary>
    /// Converts an IntPtr array of context arguments to an object array containing the resolved parameters the pointers point to.
    /// </summary>
    /// <remarks>
    /// Parameters passed to functions have only an affinity for a certain data type, there is no underlying schema available
    /// to force them into a certain type.  Therefore the only types you will ever see as parameters are
    /// DBNull.Value, Int64, Double, String or byte[] array.
    /// </remarks>
    /// <param name="nArgs">The number of arguments</param>
    /// <param name="argsptr">A pointer to the array of arguments</param>
    /// <param name="paramptrs">Pointer values for the arguments</param>
    /// <returns>An object array of the arguments once they've been converted to .NET values</returns>
    internal object[] ConvertParams(
        int nArgs,
        IntPtr argsptr,
#if !PLATFORM_COMPACTFRAMEWORK
        out IntPtr[] paramptrs
#else
        out int[] paramptrs
#endif
        )
    {
      object[] parms = new object[nArgs];
#if !PLATFORM_COMPACTFRAMEWORK
      IntPtr[] argint = new IntPtr[nArgs];
#else
      int[] argint = new int[nArgs];
#endif
      Marshal.Copy(argsptr, argint, 0, nArgs);
#if !PLATFORM_COMPACTFRAMEWORK
      paramptrs = new IntPtr[nArgs];
#else
      paramptrs = new int[nArgs];
#endif
      Array.Copy(argint, paramptrs, nArgs);

      for (int n = 0; n < nArgs; n++)
      {
        switch (_base.GetParamValueType((IntPtr)argint[n]))
        {
          case TypeAffinity.Null:
            parms[n] = DBNull.Value;
            break;
          case TypeAffinity.Int64:
            parms[n] = _base.GetParamValueInt64((IntPtr)argint[n]);
            break;
          case TypeAffinity.Double:
            parms[n] = _base.GetParamValueDouble((IntPtr)argint[n]);
            break;
          case TypeAffinity.Text:
            parms[n] = _base.GetParamValueText((IntPtr)argint[n]);
            break;
          case TypeAffinity.Blob:
            {
              int x;
              byte[] blob;

              x = (int)_base.GetParamValueBytes((IntPtr)argint[n], 0, null, 0, 0);
              blob = new byte[x];
              _base.GetParamValueBytes((IntPtr)argint[n], 0, blob, 0, x);
              parms[n] = blob;
            }
            break;
          case TypeAffinity.DateTime: // Never happens here but what the heck, maybe it will one day.
            parms[n] = _base.ToDateTime(_base.GetParamValueText((IntPtr)argint[n]));
            break;
        }
      }
      return parms;
    }

    /// <summary>
    /// Takes the return value from Invoke() and Final() and figures out how to return it to SQLite's context.
    /// </summary>
    /// <param name="context">The context the return value applies to</param>
    /// <param name="returnValue">The parameter to return to SQLite</param>
    private void SetReturnValue(IntPtr context, object returnValue)
    {
      if (returnValue == null || returnValue == DBNull.Value)
      {
        _base.ReturnNull(context);
        return;
      }

      Type t = returnValue.GetType();
      if (t == typeof(DateTime))
      {
        _base.ReturnText(context, _base.ToString((DateTime)returnValue));
        return;
      }
      else
      {
        Exception r = returnValue as Exception;

        if (r != null)
        {
          _base.ReturnError(context, r.Message);
          return;
        }
      }

      switch (SQLiteConvert.TypeToAffinity(t, _flags))
      {
        case TypeAffinity.Null:
          _base.ReturnNull(context);
          return;
        case TypeAffinity.Int64:
          _base.ReturnInt64(context, Convert.ToInt64(returnValue, CultureInfo.CurrentCulture));
          return;
        case TypeAffinity.Double:
          _base.ReturnDouble(context, Convert.ToDouble(returnValue, CultureInfo.CurrentCulture));
          return;
        case TypeAffinity.Text:
          _base.ReturnText(context, returnValue.ToString());
          return;
        case TypeAffinity.Blob:
          _base.ReturnBlob(context, (byte[])returnValue);
          return;
      }
    }

    /// <summary>
    /// Internal scalar callback function, which wraps the raw context pointer and calls the virtual Invoke() method.
    /// WARNING: Must not throw exceptions.
    /// </summary>
    /// <param name="context">A raw context pointer</param>
    /// <param name="nArgs">Number of arguments passed in</param>
    /// <param name="argsptr">A pointer to the array of arguments</param>
    internal void ScalarCallback(IntPtr context, int nArgs, IntPtr argsptr)
    {
        try
        {
            _context = context;
            _params = null;

            SetReturnValue(context,
                Invoke(ConvertParams(nArgs, argsptr, out _params))); /* throw */
        }
        catch (Exception e) /* NOTE: Must catch ALL. */
        {
            try
            {
                if (HelperMethods.LogCallbackExceptions(_flags))
                {
                    SQLiteLog.LogMessage(SQLiteBase.COR_E_EXCEPTION,
                        HelperMethods.StringFormat(CultureInfo.CurrentCulture,
                        UnsafeNativeMethods.ExceptionMessageFormat,
                        "Invoke", e)); /* throw */
                }
            }
            catch
            {
                // do nothing.
            }
        }
        finally
        {
            _params = null;
            _context = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Internal collating sequence function, which wraps up the raw string pointers and executes the Compare() virtual function.
    /// WARNING: Must not throw exceptions.
    /// </summary>
    /// <param name="ptr">Not used</param>
    /// <param name="len1">Length of the string pv1</param>
    /// <param name="ptr1">Pointer to the first string to compare</param>
    /// <param name="len2">Length of the string pv2</param>
    /// <param name="ptr2">Pointer to the second string to compare</param>
    /// <returns>Returns -1 if the first string is less than the second.  0 if they are equal, or 1 if the first string is greater
    /// than the second.  Returns 0 if an exception is caught.</returns>
    internal int CompareCallback(IntPtr ptr, int len1, IntPtr ptr1, int len2, IntPtr ptr2)
    {
        try
        {
            return Compare(SQLiteConvert.UTF8ToString(ptr1, len1),
                SQLiteConvert.UTF8ToString(ptr2, len2)); /* throw */
        }
        catch (Exception e) /* NOTE: Must catch ALL. */
        {
            try
            {
                if (HelperMethods.LogCallbackExceptions(_flags))
                {
                    SQLiteLog.LogMessage(SQLiteBase.COR_E_EXCEPTION,
                        HelperMethods.StringFormat(CultureInfo.CurrentCulture,
                        UnsafeNativeMethods.ExceptionMessageFormat,
                        "Compare", e)); /* throw */
                }
            }
            catch
            {
                // do nothing.
            }
        }

        //
        // NOTE: This must be done to prevent the core SQLite library from
        //       using our (invalid) result.
        //
        if ((_base != null) && _base.IsOpen())
            _base.Cancel();

        return 0;
    }

    /// <summary>
    /// Internal collating sequence function, which wraps up the raw string pointers and executes the Compare() virtual function.
    /// WARNING: Must not throw exceptions.
    /// </summary>
    /// <param name="ptr">Not used</param>
    /// <param name="len1">Length of the string pv1</param>
    /// <param name="ptr1">Pointer to the first string to compare</param>
    /// <param name="len2">Length of the string pv2</param>
    /// <param name="ptr2">Pointer to the second string to compare</param>
    /// <returns>Returns -1 if the first string is less than the second.  0 if they are equal, or 1 if the first string is greater
    /// than the second.  Returns 0 if an exception is caught.</returns>
    internal int CompareCallback16(IntPtr ptr, int len1, IntPtr ptr1, int len2, IntPtr ptr2)
    {
        try
        {
            return Compare(SQLite3_UTF16.UTF16ToString(ptr1, len1),
                SQLite3_UTF16.UTF16ToString(ptr2, len2)); /* throw */
        }
        catch (Exception e) /* NOTE: Must catch ALL. */
        {
            try
            {
                if (HelperMethods.LogCallbackExceptions(_flags))
                {
                    SQLiteLog.LogMessage(SQLiteBase.COR_E_EXCEPTION,
                        HelperMethods.StringFormat(CultureInfo.CurrentCulture,
                        UnsafeNativeMethods.ExceptionMessageFormat,
                        "Compare (UTF16)", e)); /* throw */
                }
            }
            catch
            {
                // do nothing.
            }
        }

        //
        // NOTE: This must be done to prevent the core SQLite library from
        //       using our (invalid) result.
        //
        if ((_base != null) && _base.IsOpen())
            _base.Cancel();

        return 0;
    }

    /// <summary>
    /// The internal aggregate Step function callback, which wraps the raw context pointer and calls the virtual Step() method.
    /// WARNING: Must not throw exceptions.
    /// </summary>
    /// <remarks>
    /// This function takes care of doing the lookups and getting the important information put together to call the Step() function.
    /// That includes pulling out the user's contextData and updating it after the call is made.  We use a sorted list for this so
    /// binary searches can be done to find the data.
    /// </remarks>
    /// <param name="context">A raw context pointer</param>
    /// <param name="nArgs">Number of arguments passed in</param>
    /// <param name="argsptr">A pointer to the array of arguments</param>
    internal void StepCallback(IntPtr context, int nArgs, IntPtr argsptr)
    {
        try
        {
            AggregateData data = null;

            if (_base != null)
            {
                IntPtr nAux = _base.AggregateContext(context);

                if ((_contextDataList != null) &&
                    !_contextDataList.TryGetValue(nAux, out data))
                {
                    data = new AggregateData();
                    _contextDataList[nAux] = data;
                }
            }

            if (data == null)
                data = new AggregateData();

            try
            {
                _context = context;
                _params = null;

                Step(ConvertParams(nArgs, argsptr, out _params),
                    data._stepCount, ref data._data); /* throw */
            }
            finally
            {
                data._stepCount++;
            }
        }
        catch (Exception e) /* NOTE: Must catch ALL. */
        {
            try
            {
                if (HelperMethods.LogCallbackExceptions(_flags))
                {
                    SQLiteLog.LogMessage(SQLiteBase.COR_E_EXCEPTION,
                        HelperMethods.StringFormat(CultureInfo.CurrentCulture,
                        UnsafeNativeMethods.ExceptionMessageFormat,
                        "Step", e)); /* throw */
                }
            }
            catch
            {
                // do nothing.
            }
        }
        finally
        {
            _params = null;
            _context = IntPtr.Zero;
        }
    }

    /// <summary>
    /// An internal aggregate Final function callback, which wraps the context pointer and calls the virtual Final() method.
    /// WARNING: Must not throw exceptions.
    /// </summary>
    /// <param name="context">A raw context pointer</param>
    internal void FinalCallback(IntPtr context)
    {
        try
        {
            object obj = null;

            if (_base != null)
            {
                IntPtr n = _base.AggregateContext(context);
                AggregateData aggData;

                if ((_contextDataList != null) &&
                    _contextDataList.TryGetValue(n, out aggData))
                {
                    obj = aggData._data;
                    _contextDataList.Remove(n);
                }
            }

            try
            {
                _context = context;
                _params = null;

                SetReturnValue(context, Final(obj)); /* throw */
            }
            finally
            {
                IDisposable disp = obj as IDisposable;
                if (disp != null) disp.Dispose(); /* throw */
            }
        }
        catch (Exception e) /* NOTE: Must catch ALL. */
        {
            try
            {
                if (HelperMethods.LogCallbackExceptions(_flags))
                {
                    SQLiteLog.LogMessage(SQLiteBase.COR_E_EXCEPTION,
                        HelperMethods.StringFormat(CultureInfo.CurrentCulture,
                        UnsafeNativeMethods.ExceptionMessageFormat,
                        "Final", e)); /* throw */
                }
            }
            catch
            {
                // do nothing.
            }
        }
        finally
        {
            _params = null;
            _context = IntPtr.Zero;
        }
    }

    /// <summary>
    /// An internal aggregate Value function callback, which wraps the context pointer and calls the virtual Value() method.
    /// WARNING: Must not throw exceptions.
    /// </summary>
    /// <param name="context">A raw context pointer</param>
    internal void ValueCallback(IntPtr context)
    {
        try
        {
            object obj = null;

            if (_base != null)
            {
                IntPtr n = _base.AggregateContext(context);
                AggregateData aggData;

                if ((_contextDataList != null) &&
                    _contextDataList.TryGetValue(n, out aggData))
                {
                    obj = aggData._data;
                }
            }

            _context = context;
            _params = null;

            SetReturnValue(context, Value(obj)); /* throw */
        }
        catch (Exception e) /* NOTE: Must catch ALL. */
        {
            try
            {
                if (HelperMethods.LogCallbackExceptions(_flags))
                {
                    SQLiteLog.LogMessage(SQLiteBase.COR_E_EXCEPTION,
                        HelperMethods.StringFormat(CultureInfo.CurrentCulture,
                        UnsafeNativeMethods.ExceptionMessageFormat,
                        "Value", e)); /* throw */
                }
            }
            catch
            {
                // do nothing.
            }
        }
        finally
        {
            _params = null;
            _context = IntPtr.Zero;
        }
    }

    /// <summary>
    /// The internal aggregate Inverse function callback, which wraps the raw context pointer and calls the virtual Inverse() method.
    /// WARNING: Must not throw exceptions.
    /// </summary>
    /// <remarks>
    /// This function takes care of doing the lookups and getting the important information put together to call the Inverse() function.
    /// That includes pulling out the user's contextData and updating it after the call is made.  We use a sorted list for this so
    /// binary searches can be done to find the data.
    /// </remarks>
    /// <param name="context">A raw context pointer</param>
    /// <param name="nArgs">Number of arguments passed in</param>
    /// <param name="argsptr">A pointer to the array of arguments</param>
    internal void InverseCallback(IntPtr context, int nArgs, IntPtr argsptr)
    {
        try
        {
            AggregateData data = null;

            if (_base != null)
            {
                IntPtr nAux = _base.AggregateContext(context);

                if ((_contextDataList != null) &&
                    !_contextDataList.TryGetValue(nAux, out data))
                {
                    //
                    // TODO: This should not be required here since
                    //       Step should have already been called?
                    //
                    data = new AggregateData();
                    _contextDataList[nAux] = data;
                }
            }

            if (data == null)
                data = new AggregateData();

            try
            {
                _context = context;
                _params = null;

                Inverse(ConvertParams(nArgs, argsptr, out _params),
                    data._inverseCount, ref data._data); /* throw */
            }
            finally
            {
                data._inverseCount++;
            }
        }
        catch (Exception e) /* NOTE: Must catch ALL. */
        {
            try
            {
                if (HelperMethods.LogCallbackExceptions(_flags))
                {
                    SQLiteLog.LogMessage(SQLiteBase.COR_E_EXCEPTION,
                        HelperMethods.StringFormat(CultureInfo.CurrentCulture,
                        UnsafeNativeMethods.ExceptionMessageFormat,
                        "Inverse", e)); /* throw */
                }
            }
            catch
            {
                // do nothing.
            }
        }
        finally
        {
            _params = null;
            _context = IntPtr.Zero;
        }
    }

    /// <summary>
    /// Using reflection, enumerate all assemblies in the current appdomain looking for classes that
    /// have a SQLiteFunctionAttribute attribute, and registering them accordingly.
    /// </summary>
#if !PLATFORM_COMPACTFRAMEWORK && !NET_STANDARD_20 && !NET_STANDARD_21
    [Security.Permissions.FileIOPermission(Security.Permissions.SecurityAction.Assert, AllFiles = Security.Permissions.FileIOPermissionAccess.PathDiscovery)]
#endif
    static SQLiteFunction()
    {
      _registeredFunctions = new Dictionary<SQLiteFunctionAttribute, object>();
      try
      {
#if !PLATFORM_COMPACTFRAMEWORK
        //
        // NOTE: If the "No_SQLiteFunctions" environment variable is set,
        //       skip all our special code and simply return.
        //
        if (UnsafeNativeMethods.GetSettingValue("No_SQLiteFunctions", null) != null)
          return;

        SQLiteFunctionAttribute at;
        System.Reflection.Assembly[] arAssemblies = System.AppDomain.CurrentDomain.GetAssemblies();
        int w = arAssemblies.Length;
        System.Reflection.AssemblyName sqlite = System.Reflection.Assembly.GetExecutingAssembly().GetName();

        for (int n = 0; n < w; n++)
        {
          Type[] arTypes;
          bool found = false;
          System.Reflection.AssemblyName[] references;
          try
          {
            // Inspect only assemblies that reference SQLite
            references = arAssemblies[n].GetReferencedAssemblies();
            int t = references.Length;
            for (int z = 0; z < t; z++)
            {
              if (references[z].Name == sqlite.Name)
              {
                found = true;
                break;
              }
            }

            if (found == false)
              continue;

            arTypes = arAssemblies[n].GetTypes();
          }
          catch (Reflection.ReflectionTypeLoadException e)
          {
            arTypes = e.Types;
          }

          int v = arTypes.Length;
          for (int x = 0; x < v; x++)
          {
            if (arTypes[x] == null) continue;

            object[] arAtt = arTypes[x].GetCustomAttributes(typeof(SQLiteFunctionAttribute), false);
            int u = arAtt.Length;
            for (int y = 0; y < u; y++)
            {
              at = arAtt[y] as SQLiteFunctionAttribute;
              if (at != null)
              {
                at.InstanceType = arTypes[x];
                ReplaceFunction(at, null);
              }
            }
          }
        }
#endif
      }
      catch // SQLite provider can continue without being able to find built-in functions
      {
      }
    }

    /// <summary>
    /// Manual method of registering a function.  The type must still have the SQLiteFunctionAttributes in order to work
    /// properly, but this is a workaround for the Compact Framework where enumerating assemblies is not currently supported.
    /// </summary>
    /// <param name="typ">The type of the function to register</param>
    public static void RegisterFunction(Type typ)
    {
        object[] arAtt = typ.GetCustomAttributes(
            typeof(SQLiteFunctionAttribute), false);

        for (int y = 0; y < arAtt.Length; y++)
        {
            SQLiteFunctionAttribute at = arAtt[y] as SQLiteFunctionAttribute;

            if (at == null)
                continue;

            RegisterFunction(
                at.Name, at.Arguments, at.FuncType, at.FuncFlags, typ,
                at.Callback1, at.Callback2, at.Callback3, at.Callback4);
        }
    }

    /// <summary>
    /// Alternative method of registering a function.  This method
    /// does not require the specified type to be annotated with
    /// <see cref="SQLiteFunctionAttribute" />.
    /// </summary>
    /// <param name="name">
    /// The name of the function to register.
    /// </param>
    /// <param name="argumentCount">
    /// The number of arguments accepted by the function.
    /// </param>
    /// <param name="functionType">
    /// The type of SQLite function being registered (e.g. scalar,
    /// aggregate, or collating sequence).
    /// </param>
    /// <param name="instanceType">
    /// The <see cref="Type" /> that actually implements the function.
    /// This will only be used if the <paramref name="callback1" />
    /// and <paramref name="callback2" /> parameters are null.
    /// </param>
    /// <param name="callback1">
    /// The <see cref="Delegate" /> to be used for all calls into the
    /// <see cref="SQLiteFunction.Invoke" />,
    /// <see cref="SQLiteFunction.Step" />,
    /// and <see cref="SQLiteFunction.Compare" /> virtual methods.
    /// </param>
    /// <param name="callback2">
    /// The <see cref="Delegate" /> to be used for all calls into the
    /// <see cref="SQLiteFunction.Final" /> virtual method.  This
    /// parameter is only necessary for aggregate functions.
    /// </param>
    public static void RegisterFunction(
        string name,
        int argumentCount,
        FunctionType functionType,
        Type instanceType,
        Delegate callback1,
        Delegate callback2
        )
    {
        SQLiteFunctionAttribute at = new SQLiteFunctionAttribute(
            name, argumentCount, functionType);

        at.InstanceType = instanceType;
        at.Callback1 = callback1;
        at.Callback2 = callback2;

        ReplaceFunction(at, null);
    }

    /// <summary>
    /// Alternative method of registering a function.  This method
    /// does not require the specified type to be annotated with
    /// <see cref="SQLiteFunctionAttribute" />.
    /// </summary>
    /// <param name="name">
    /// The name of the function to register.
    /// </param>
    /// <param name="argumentCount">
    /// The number of arguments accepted by the function.
    /// </param>
    /// <param name="functionType">
    /// The type of SQLite function being registered (e.g. scalar,
    /// aggregate, or collating sequence).
    /// </param>
    /// <param name="functionFlags">
    /// The extra flags for the function being registered.
    /// </param>
    /// <param name="instanceType">
    /// The <see cref="Type" /> that actually implements the function.
    /// This will only be used if the <paramref name="callback1" />
    /// and <paramref name="callback2" /> parameters are null.
    /// </param>
    /// <param name="callback1">
    /// The <see cref="Delegate" /> to be used for all calls into the
    /// <see cref="SQLiteFunction.Invoke" />,
    /// <see cref="SQLiteFunction.Step" />,
    /// and <see cref="SQLiteFunction.Compare" /> virtual methods.
    /// </param>
    /// <param name="callback2">
    /// The <see cref="Delegate" /> to be used for all calls into the
    /// <see cref="SQLiteFunction.Final" /> virtual method.  This
    /// parameter is only necessary for aggregate functions.
    /// </param>
    /// <param name="callback3">
    /// The <see cref="Delegate" /> to be used for all calls into the
    /// <see cref="SQLiteFunction.Value" /> virtual method.  This
    /// parameter is only necessary for window functions.
    /// </param>
    /// <param name="callback4">
    /// The <see cref="Delegate" /> to be used for all calls into the
    /// <see cref="SQLiteFunction.Inverse" /> virtual method.  This
    /// parameter is only necessary for window functions.
    /// </param>
    public static void RegisterFunction(
        string name,
        int argumentCount,
        FunctionType functionType,
        SQLiteFunctionFlags functionFlags,
        Type instanceType,
        Delegate callback1,
        Delegate callback2,
        Delegate callback3,
        Delegate callback4
        )
    {
        SQLiteFunctionAttribute at = new SQLiteFunctionAttribute(
            name, argumentCount, functionType, functionFlags);

        at.InstanceType = instanceType;
        at.Callback1 = callback1;
        at.Callback2 = callback2;
        at.Callback3 = callback3;
        at.Callback4 = callback4;

        ReplaceFunction(at, null);
    }

    /// <summary>
    /// Replaces a registered function, disposing of the associated (old)
    /// value if necessary.
    /// </summary>
    /// <param name="at">
    /// The attribute that describes the function to replace.
    /// </param>
    /// <param name="newValue">
    /// The new value to use.
    /// </param>
    /// <returns>
    /// Non-zero if an existing registered function was replaced; otherwise,
    /// zero.
    /// </returns>
    private static bool ReplaceFunction(
        SQLiteFunctionAttribute at,
        object newValue
        )
    {
        object oldValue;

        if (_registeredFunctions.TryGetValue(at, out oldValue))
        {
            IDisposable disposable = oldValue as IDisposable;

            if (disposable != null)
            {
                disposable.Dispose();
                disposable = null;
            }

            _registeredFunctions[at] = newValue;
            return true;
        }
        else
        {
            _registeredFunctions.Add(at, newValue);
            return false;
        }
    }

    /// <summary>
    /// Creates a <see cref="SQLiteFunction" /> instance based on the specified
    /// <see cref="SQLiteFunctionAttribute" />.
    /// </summary>
    /// <param name="functionAttribute">
    /// The <see cref="SQLiteFunctionAttribute" /> containing the metadata about
    /// the function to create.
    /// </param>
    /// <param name="function">
    /// The created function -OR- null if the function could not be created.
    /// </param>
    /// <returns>
    /// Non-zero if the function was created; otherwise, zero.
    /// </returns>
    private static bool CreateFunction(
        SQLiteFunctionAttribute functionAttribute,
        out SQLiteFunction function
        )
    {
        if (functionAttribute == null)
        {
            function = null;
            return false;
        }
        else if ((functionAttribute.Callback1 != null) ||
                 (functionAttribute.Callback2 != null) ||
                 (functionAttribute.Callback3 != null) ||
                 (functionAttribute.Callback4 != null))
        {
            function = new SQLiteDelegateFunction(
                functionAttribute.Callback1,
                functionAttribute.Callback2,
                functionAttribute.Callback3,
                functionAttribute.Callback4);

            return true;
        }
        else if (functionAttribute.InstanceType != null)
        {
            function = (SQLiteFunction)Activator.CreateInstance(
                functionAttribute.InstanceType);

            return true;
        }
        else
        {
            function = null;
            return false;
        }
    }

    /// <summary>
    /// Called by the SQLiteBase derived classes, this method binds all registered (known) user-defined functions to a connection.
    /// It is done this way so that all user-defined functions will access the database using the same encoding scheme
    /// as the connection (UTF-8 or UTF-16).
    /// </summary>
    /// <remarks>
    /// The wrapper functions that interop with SQLite will create a unique cookie value, which internally is a pointer to
    /// all the wrapped callback functions.  The interop function uses it to map CDecl callbacks to StdCall callbacks.
    /// </remarks>
    /// <param name="sqlbase">The base object on which the functions are to bind.</param>
    /// <param name="flags">The flags associated with the parent connection object.</param>
    /// <returns>Returns a logical list of functions which the connection should retain until it is closed.</returns>
    internal static IDictionary<SQLiteFunctionAttribute, SQLiteFunction> BindFunctions(
        SQLiteBase sqlbase,
        SQLiteConnectionFlags flags
        )
    {
        IDictionary<SQLiteFunctionAttribute, SQLiteFunction> lFunctions =
            new Dictionary<SQLiteFunctionAttribute, SQLiteFunction>();

        foreach (KeyValuePair<SQLiteFunctionAttribute, object> pair
                in _registeredFunctions)
        {
            SQLiteFunctionAttribute pr = pair.Key;

            if (pr == null)
                continue;

            SQLiteFunction f;

            if (CreateFunction(pr, out f))
            {
                BindFunction(sqlbase, pr, f, flags);
                lFunctions[pr] = f;
            }
            else
            {
                lFunctions[pr] = null;
            }
        }

        return lFunctions;
    }

    /// <summary>
    /// Called by the SQLiteBase derived classes, this method unbinds all registered (known)
    /// functions -OR- all previously bound user-defined functions from a connection.
    /// </summary>
    /// <param name="sqlbase">The base object from which the functions are to be unbound.</param>
    /// <param name="flags">The flags associated with the parent connection object.</param>
    /// <param name="registered">
    /// Non-zero to unbind all registered (known) functions -OR- zero to unbind all functions
    /// currently bound to the connection.
    /// </param>
    /// <returns>Non-zero if all the specified user-defined functions were unbound.</returns>
    internal static bool UnbindAllFunctions(
        SQLiteBase sqlbase,
        SQLiteConnectionFlags flags,
        bool registered
        )
    {
        if (sqlbase == null)
            return false;

        IDictionary<SQLiteFunctionAttribute, SQLiteFunction> lFunctions =
            sqlbase.Functions;

        if (lFunctions == null)
            return false;

        bool result = true;

        if (registered)
        {
            foreach (KeyValuePair<SQLiteFunctionAttribute, object> pair
                    in _registeredFunctions)
            {
                SQLiteFunctionAttribute pr = pair.Key;

                if (pr == null)
                    continue;

                SQLiteFunction f;

                if (!lFunctions.TryGetValue(pr, out f) ||
                    (f == null) ||
                    !UnbindFunction(sqlbase, pr, f, flags))
                {
                    result = false;
                }
            }
        }
        else
        {
            //
            // NOTE: Need to use a copy of the function dictionary in this method
            //       because the dictionary is modified within the UnbindFunction
            //       method, which is called inside the loop.
            //
            lFunctions = new Dictionary<SQLiteFunctionAttribute, SQLiteFunction>(
                lFunctions);

            foreach (KeyValuePair<SQLiteFunctionAttribute, SQLiteFunction> pair
                    in lFunctions)
            {
                SQLiteFunctionAttribute pr = pair.Key;

                if (pr == null)
                    continue;

                SQLiteFunction f = pair.Value;

                if ((f != null) &&
                    UnbindFunction(sqlbase, pr, f, flags))
                {
                    /* IGNORED */
                    sqlbase.Functions.Remove(pr);
                }
                else
                {
                    result = false;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// This function binds a user-defined function to a connection.
    /// </summary>
    /// <param name="sqliteBase">
    /// The <see cref="SQLiteBase" /> object instance associated with the
    /// <see cref="SQLiteConnection" /> that the function should be bound to.
    /// </param>
    /// <param name="functionAttribute">
    /// The <see cref="SQLiteFunctionAttribute"/> object instance containing
    /// the metadata for the function to be bound.
    /// </param>
    /// <param name="function">
    /// The <see cref="SQLiteFunction"/> object instance that implements the
    /// function to be bound.
    /// </param>
    /// <param name="flags">
    /// The flags associated with the parent connection object.
    /// </param>
    internal static void BindFunction(
        SQLiteBase sqliteBase,
        SQLiteFunctionAttribute functionAttribute,
        SQLiteFunction function,
        SQLiteConnectionFlags flags
        )
    {
        if (sqliteBase == null)
            throw new ArgumentNullException("sqliteBase");

        if (functionAttribute == null)
            throw new ArgumentNullException("functionAttribute");

        if (function == null)
            throw new ArgumentNullException("function");

        FunctionType functionType = functionAttribute.FuncType;

        function._base = sqliteBase;
        function._flags = flags;

        function._InvokeFunc = (functionType == FunctionType.Scalar) ?
            new SQLiteCallback(function.ScalarCallback) : null;

        function._StepFunc = (functionType == FunctionType.Aggregate) || (functionType == FunctionType.Window) ?
            new SQLiteCallback(function.StepCallback) : null;

        function._FinalFunc = (functionType == FunctionType.Aggregate) || (functionType == FunctionType.Window) ?
            new SQLiteFinalCallback(function.FinalCallback) : null;

        function._ValueFunc = (functionType == FunctionType.Window) ?
            new SQLiteFinalCallback(function.ValueCallback) : null;

        function._InverseFunc = (functionType == FunctionType.Window) ?
            new SQLiteCallback(function.InverseCallback) : null;

        function._CompareFunc = (functionType == FunctionType.Collation) ?
            new SQLiteCollation(function.CompareCallback) : null;

        function._CompareFunc16 = (functionType == FunctionType.Collation) ?
            new SQLiteCollation(function.CompareCallback16) : null;

        string name = functionAttribute.Name;

        if (functionType != FunctionType.Collation)
        {
            bool needCollSeq = (function is SQLiteFunctionEx);

            sqliteBase.CreateFunction(
                functionType, name, functionAttribute.Arguments,
                functionAttribute.FuncFlags, needCollSeq,
                function._InvokeFunc, function._StepFunc,
                function._FinalFunc, function._ValueFunc,
                function._InverseFunc, true);
        }
        else
        {
            sqliteBase.CreateCollation(
                name, function._CompareFunc, function._CompareFunc16,
                true);
        }
    }

    /// <summary>
    /// This function unbinds a user-defined functions from a connection.
    /// </summary>
    /// <param name="sqliteBase">
    /// The <see cref="SQLiteBase" /> object instance associated with the
    /// <see cref="SQLiteConnection" /> that the function should be bound to.
    /// </param>
    /// <param name="functionAttribute">
    /// The <see cref="SQLiteFunctionAttribute"/> object instance containing
    /// the metadata for the function to be bound.
    /// </param>
    /// <param name="function">
    /// The <see cref="SQLiteFunction"/> object instance that implements the
    /// function to be bound.
    /// </param>
    /// <param name="flags">
    /// The flags associated with the parent connection object.
    /// </param>
    /// <returns>Non-zero if the function was unbound.</returns>
    internal static bool UnbindFunction(
        SQLiteBase sqliteBase,
        SQLiteFunctionAttribute functionAttribute,
        SQLiteFunction function,
        SQLiteConnectionFlags flags /* NOT USED */
        )
    {
        if (sqliteBase == null)
            throw new ArgumentNullException("sqliteBase");

        if (functionAttribute == null)
            throw new ArgumentNullException("functionAttribute");

        if (function == null)
            throw new ArgumentNullException("function");

        FunctionType functionType = functionAttribute.FuncType;
        string name = functionAttribute.Name;

        if (functionType != FunctionType.Collation)
        {
            bool needCollSeq = (function is SQLiteFunctionEx);

            return sqliteBase.CreateFunction(
                functionType, name, functionAttribute.Arguments,
                functionAttribute.FuncFlags, needCollSeq, null,
                null, null, null, null, false) == SQLiteErrorCode.Ok;
        }
        else
        {
            return sqliteBase.CreateCollation(
                name, null, null, false) == SQLiteErrorCode.Ok;
        }
    }
  }

  /////////////////////////////////////////////////////////////////////////////

  /// <summary>
  /// This <see cref="Delegate" /> type is used with the
  /// <see cref="SQLiteDelegateFunction.Invoke" /> method.
  /// </summary>
  /// <param name="param0">
  /// This is always the string literal "Invoke".
  /// </param>
  /// <param name="args">
  /// The arguments for the scalar function.
  /// </param>
  /// <returns>
  /// The result of the scalar function.
  /// </returns>
  public delegate object SQLiteInvokeDelegate(
    string param0,
    object[] args
  );

  /////////////////////////////////////////////////////////////////////////////

  /// <summary>
  /// This <see cref="Delegate" /> type is used with the
  /// <see cref="SQLiteDelegateFunction.Step" /> method.
  /// </summary>
  /// <param name="param0">
  /// This is always the string literal "Step".
  /// </param>
  /// <param name="args">
  /// The arguments for the aggregate function.
  /// </param>
  /// <param name="stepNumber">
  /// The step number (one based).  This is incrememted each time the
  /// <see cref="SQLiteDelegateFunction.Step" /> method is called.
  /// </param>
  /// <param name="contextData">
  /// A placeholder for implementers to store contextual data pertaining
  /// to the current context.
  /// </param>
  public delegate void SQLiteStepDelegate(
    string param0,
    object[] args,
    int stepNumber,
    ref object contextData
  );

  /////////////////////////////////////////////////////////////////////////////

  /// <summary>
  /// This <see cref="Delegate" /> type is used with the
  /// <see cref="SQLiteDelegateFunction.Final" /> method.
  /// </summary>
  /// <param name="param0">
  /// This is always the string literal "Final".
  /// </param>
  /// <param name="contextData">
  /// A placeholder for implementers to store contextual data pertaining
  /// to the current context.
  /// </param>
  /// <returns>
  /// The result of the aggregate function.
  /// </returns>
  public delegate object SQLiteFinalDelegate(
    string param0,
    object contextData
  );

  /////////////////////////////////////////////////////////////////////////////

  /// <summary>
  /// This <see cref="Delegate" /> type is used with the
  /// <see cref="SQLiteDelegateFunction.Compare" /> method.
  /// </summary>
  /// <param name="param0">
  /// This is always the string literal "Compare".
  /// </param>
  /// <param name="param1">
  /// The first string to compare.
  /// </param>
  /// <param name="param2">
  /// The second strnig to compare.
  /// </param>
  /// <returns>
  /// A positive integer if the <paramref name="param1" /> parameter is
  /// greater than the <paramref name="param2" /> parameter, a negative
  /// integer if the <paramref name="param1" /> parameter is less than
  /// the <paramref name="param2" /> parameter, or zero if they are
  /// equal.
  /// </returns>
  public delegate int SQLiteCompareDelegate(
    string param0,
    string param1,
    string param2
  );

  /////////////////////////////////////////////////////////////////////////////

#if !PLATFORM_COMPACTFRAMEWORK
  /// <summary>
  /// This class implements a SQLite function using a <see cref="Delegate" />.
  /// All the virtual methods of the <see cref="SQLiteFunction" /> class are
  /// implemented using calls to the <see cref="SQLiteInvokeDelegate" />,
  /// <see cref="SQLiteStepDelegate" />, <see cref="SQLiteFinalDelegate" />,
  /// and <see cref="SQLiteCompareDelegate" /> strongly typed delegate types
  /// or via the <see cref="Delegate.DynamicInvoke" /> method.
  /// The arguments are presented in the same order they appear in
  /// the associated <see cref="SQLiteFunction" /> methods with one exception:
  /// the first argument is the name of the virtual method being implemented.
  /// </summary>
#else
  /// <summary>
  /// This class implements a SQLite function using a <see cref="Delegate" />.
  /// All the virtual methods of the <see cref="SQLiteFunction" /> class are
  /// implemented using calls to the <see cref="SQLiteInvokeDelegate" />,
  /// <see cref="SQLiteStepDelegate" />, <see cref="SQLiteFinalDelegate" />,
  /// and <see cref="SQLiteCompareDelegate" /> strongly typed delegate types.
  /// The arguments are presented in the same order they appear in
  /// the associated <see cref="SQLiteFunction" /> methods with one exception:
  /// the first argument is the name of the virtual method being implemented.
  /// </summary>
#endif
  public class SQLiteDelegateFunction : SQLiteFunction
  {
      #region Private Constants
      /// <summary>
      /// This error message is used by the overridden virtual methods when
      /// a required <see cref="Delegate" /> property (e.g.
      /// <see cref="Callback1" /> or <see cref="Callback2" />) has not been
      /// set.
      /// </summary>
      private const string NoCallbackError = "No \"{0}\" callback is set.";

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// This error message is used by the overridden <see cref="Compare" />
      /// method when the result does not have a type of <see cref="Int32" />.
      /// </summary>
      private const string ResultInt32Error = "\"{0}\" result must be Int32.";
      #endregion

      /////////////////////////////////////////////////////////////////////////

      #region Public Constructors
      /// <summary>
      /// Constructs an empty instance of this class.
      /// </summary>
      public SQLiteDelegateFunction()
          : this(null, null, null, null)
      {
          // do nothing.
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// Constructs an instance of this class using the specified
      /// <see cref="Delegate" /> as the <see cref="SQLiteFunction" />
      /// implementation.
      /// </summary>
      /// <param name="callback1">
      /// The <see cref="Delegate" /> to be used for all calls into the
      /// <see cref="Invoke" />, <see cref="Step" />, and
      /// <see cref="Compare" /> virtual methods needed by the
      /// <see cref="SQLiteFunction" /> base class.
      /// </param>
      /// <param name="callback2">
      /// The <see cref="Delegate" /> to be used for all calls into the
      /// <see cref="Final" /> virtual methods needed by the
      /// <see cref="SQLiteFunction" /> base class.
      /// </param>
      public SQLiteDelegateFunction(
          Delegate callback1,
          Delegate callback2
          )
      {
          this.callback1 = callback1;
          this.callback2 = callback2;
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// Constructs an instance of this class using the specified
      /// <see cref="Delegate" /> as the <see cref="SQLiteFunction" />
      /// implementation.
      /// </summary>
      /// <param name="callback1">
      /// The <see cref="Delegate" /> to be used for all calls into the
      /// <see cref="Invoke" />, <see cref="Step" />, and
      /// <see cref="Compare" /> virtual methods needed by the
      /// <see cref="SQLiteFunction" /> base class.
      /// </param>
      /// <param name="callback2">
      /// The <see cref="Delegate" /> to be used for all calls into the
      /// <see cref="Final" /> virtual methods needed by the
      /// <see cref="SQLiteFunction" /> base class.
      /// </param>
      /// <param name="callback3">
      /// The <see cref="Delegate" /> to be used for all calls into the
      /// <see cref="Value" /> virtual method needed by the
      /// <see cref="SQLiteFunction" /> base class.
      /// </param>
      /// <param name="callback4">
      /// The <see cref="Delegate" /> to be used for all calls into the
      /// <see cref="Inverse" /> virtual method needed by the
      /// <see cref="SQLiteFunction" /> base class.
      /// </param>
      public SQLiteDelegateFunction(
          Delegate callback1,
          Delegate callback2,
          Delegate callback3,
          Delegate callback4
          )
      {
          this.callback1 = callback1;
          this.callback2 = callback2;
          this.callback3 = callback3;
          this.callback4 = callback4;
      }
      #endregion

      /////////////////////////////////////////////////////////////////////////

      #region Protected Methods
      /// <summary>
      /// Returns the list of arguments for the <see cref="Invoke" /> method,
      /// as an <see cref="Array" /> of <see cref="Object" />.  The first
      /// argument is always the literal string "Invoke".
      /// </summary>
      /// <param name="args">
      /// The original arguments received by the <see cref="Invoke" /> method.
      /// </param>
      /// <param name="earlyBound">
      /// Non-zero if the returned arguments are going to be used with the
      /// <see cref="SQLiteInvokeDelegate" /> type; otherwise, zero.
      /// </param>
      /// <returns>
      /// The arguments to pass to the configured <see cref="Delegate" />.
      /// </returns>
      protected virtual object[] GetInvokeArgs(
          object[] args,
          bool earlyBound
          )
      {
          object[] newArgs = new object[] { "Invoke", args };

          if (!earlyBound)
              newArgs = new object[] { newArgs }; // WRAP

          return newArgs;
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// Returns the list of arguments for the <see cref="Step" /> method,
      /// as an <see cref="Array" /> of <see cref="Object" />.  The first
      /// argument is always the literal string "Step".
      /// </summary>
      /// <param name="args">
      /// The original arguments received by the <see cref="Step" /> method.
      /// </param>
      /// <param name="stepNumber">
      /// The step number (one based).  This is incrememted each time the
      /// <see cref="Step" /> method is called.
      /// </param>
      /// <param name="contextData">
      /// A placeholder for implementers to store contextual data pertaining
      /// to the current context.
      /// </param>
      /// <param name="earlyBound">
      /// Non-zero if the returned arguments are going to be used with the
      /// <see cref="SQLiteStepDelegate" /> type; otherwise, zero.
      /// </param>
      /// <returns>
      /// The arguments to pass to the configured <see cref="Delegate" />.
      /// </returns>
      protected virtual object[] GetStepArgs(
          object[] args,
          int stepNumber,
          object contextData,
          bool earlyBound
          )
      {
          object[] newArgs = new object[] {
              "Step", args, stepNumber, contextData
          };

          if (!earlyBound)
              newArgs = new object[] { newArgs }; // WRAP

          return newArgs;
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// Updates the output arguments for the <see cref="Step" /> method,
      /// using an <see cref="Array" /> of <see cref="Object" />.  The first
      /// argument is always the literal string "Step".  Currently, only the
      /// <paramref name="contextData" /> parameter is updated.
      /// </summary>
      /// <param name="args">
      /// The original arguments received by the <see cref="Step" /> method.
      /// </param>
      /// <param name="contextData">
      /// A placeholder for implementers to store contextual data pertaining
      /// to the current context.
      /// </param>
      /// <param name="earlyBound">
      /// Non-zero if the returned arguments are going to be used with the
      /// <see cref="SQLiteStepDelegate" /> type; otherwise, zero.
      /// </param>
      /// <returns>
      /// The arguments to pass to the configured <see cref="Delegate" />.
      /// </returns>
      protected virtual void UpdateStepArgs(
          object[] args,
          ref object contextData,
          bool earlyBound
          )
      {
          object[] newArgs;

          if (earlyBound)
              newArgs = args;
          else
              newArgs = args[0] as object[];

          if (newArgs == null)
              return;

          contextData = newArgs[newArgs.Length - 1];
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// Returns the list of arguments for the <see cref="Final" /> method,
      /// as an <see cref="Array" /> of <see cref="Object" />.  The first
      /// argument is always the literal string "Final".
      /// </summary>
      /// <param name="contextData">
      /// A placeholder for implementers to store contextual data pertaining
      /// to the current context.
      /// </param>
      /// <param name="earlyBound">
      /// Non-zero if the returned arguments are going to be used with the
      /// <see cref="SQLiteFinalDelegate" /> type; otherwise, zero.
      /// </param>
      /// <returns>
      /// The arguments to pass to the configured <see cref="Delegate" />.
      /// </returns>
      protected virtual object[] GetFinalArgs(
          object contextData,
          bool earlyBound
          )
      {
          object[] newArgs = new object[] { "Final", contextData };

          if (!earlyBound)
              newArgs = new object[] { newArgs }; // WRAP

          return newArgs;
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// Returns the list of arguments for the <see cref="Value" /> method,
      /// as an <see cref="Array" /> of <see cref="Object" />.  The first
      /// argument is always the literal string "Value".
      /// </summary>
      /// <param name="contextData">
      /// A placeholder for implementers to store contextual data pertaining
      /// to the current context.
      /// </param>
      /// <param name="earlyBound">
      /// Non-zero if the returned arguments are going to be used with the
      /// <see cref="SQLiteFinalDelegate" /> type; otherwise, zero.
      /// </param>
      /// <returns>
      /// The arguments to pass to the configured <see cref="Delegate" />.
      /// </returns>
      protected virtual object[] GetValueArgs(
          object contextData,
          bool earlyBound
          )
      {
          object[] newArgs = new object[] { "Value", contextData };

          if (!earlyBound)
              newArgs = new object[] { newArgs }; // WRAP

          return newArgs;
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// Returns the list of arguments for the <see cref="Inverse" /> method,
      /// as an <see cref="Array" /> of <see cref="Object" />.  The first
      /// argument is always the literal string "Inverse".
      /// </summary>
      /// <param name="args">
      /// The original arguments received by the <see cref="Inverse" /> method.
      /// </param>
      /// <param name="stepNumber">
      /// The step number (one based).  This is incrememted each time the
      /// <see cref="Inverse" /> method is called.
      /// </param>
      /// <param name="contextData">
      /// A placeholder for implementers to store contextual data pertaining
      /// to the current context.
      /// </param>
      /// <param name="earlyBound">
      /// Non-zero if the returned arguments are going to be used with the
      /// <see cref="SQLiteStepDelegate" /> type; otherwise, zero.
      /// </param>
      /// <returns>
      /// The arguments to pass to the configured <see cref="Delegate" />.
      /// </returns>
      protected virtual object[] GetInverseArgs(
          object[] args,
          int stepNumber,
          object contextData,
          bool earlyBound
          )
      {
          object[] newArgs = new object[] {
              "Inverse", args, stepNumber, contextData
          };

          if (!earlyBound)
              newArgs = new object[] { newArgs }; // WRAP

          return newArgs;
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// Updates the output arguments for the <see cref="Inverse" /> method,
      /// using an <see cref="Array" /> of <see cref="Object" />.  The first
      /// argument is always the literal string "Inverse".  Currently, only the
      /// <paramref name="contextData" /> parameter is updated.
      /// </summary>
      /// <param name="args">
      /// The original arguments received by the <see cref="Inverse" /> method.
      /// </param>
      /// <param name="contextData">
      /// A placeholder for implementers to store contextual data pertaining
      /// to the current context.
      /// </param>
      /// <param name="earlyBound">
      /// Non-zero if the returned arguments are going to be used with the
      /// <see cref="SQLiteStepDelegate" /> type; otherwise, zero.
      /// </param>
      /// <returns>
      /// The arguments to pass to the configured <see cref="Delegate" />.
      /// </returns>
      protected virtual void UpdateInverseArgs(
          object[] args,
          ref object contextData,
          bool earlyBound
          )
      {
          object[] newArgs;

          if (earlyBound)
              newArgs = args;
          else
              newArgs = args[0] as object[];

          if (newArgs == null)
              return;

          contextData = newArgs[newArgs.Length - 1];
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// Returns the list of arguments for the <see cref="Compare" /> method,
      /// as an <see cref="Array" /> of <see cref="Object" />.  The first
      /// argument is always the literal string "Compare".
      /// </summary>
      /// <param name="param1">
      /// The first string to compare.
      /// </param>
      /// <param name="param2">
      /// The second strnig to compare.
      /// </param>
      /// <param name="earlyBound">
      /// Non-zero if the returned arguments are going to be used with the
      /// <see cref="SQLiteCompareDelegate" /> type; otherwise, zero.
      /// </param>
      /// <returns>
      /// The arguments to pass to the configured <see cref="Delegate" />.
      /// </returns>
      protected virtual object[] GetCompareArgs(
          string param1,
          string param2,
          bool earlyBound
          )
      {
          object[] newArgs = new object[] { "Compare", param1, param2 };

          if (!earlyBound)
              newArgs = new object[] { newArgs }; // WRAP

          return newArgs;
      }
      #endregion

      /////////////////////////////////////////////////////////////////////////

      #region Public Properties
      private Delegate callback1;
      /// <summary>
      /// The <see cref="Delegate" /> to be used for all calls into the
      /// <see cref="Invoke" />, <see cref="Step" />, and
      /// <see cref="Compare" /> virtual methods needed by the
      /// <see cref="SQLiteFunction" /> base class.
      /// </summary>
      public virtual Delegate Callback1
      {
          get { return callback1; }
          set { callback1 = value; }
      }

      /////////////////////////////////////////////////////////////////////////

      private Delegate callback2;
      /// <summary>
      /// The <see cref="Delegate" /> to be used for all calls into the
      /// <see cref="Final" /> virtual methods needed by the
      /// <see cref="SQLiteFunction" /> base class.
      /// </summary>
      public virtual Delegate Callback2
      {
          get { return callback2; }
          set { callback2 = value; }
      }

      /////////////////////////////////////////////////////////////////////////

      private Delegate callback3;
      /// <summary>
      /// The <see cref="Delegate" /> to be used for all calls into the
      /// <see cref="Value" /> virtual methods needed by the
      /// <see cref="SQLiteFunction" /> base class.
      /// </summary>
      public virtual Delegate Callback3
      {
          get { return callback3; }
          set { callback3 = value; }
      }

      /////////////////////////////////////////////////////////////////////////

      private Delegate callback4;
      /// <summary>
      /// The <see cref="Delegate" /> to be used for all calls into the
      /// <see cref="Inverse" /> virtual methods needed by the
      /// <see cref="SQLiteFunction" /> base class.
      /// </summary>
      public virtual Delegate Callback4
      {
          get { return callback4; }
          set { callback4 = value; }
      }
      #endregion

      /////////////////////////////////////////////////////////////////////////

      #region System.Data.SQLite.SQLiteFunction Overrides
      /// <summary>
      /// This virtual method is the implementation for scalar functions.
      /// See the <see cref="SQLiteFunction.Invoke" /> method for more
      /// details.
      /// </summary>
      /// <param name="args">
      /// The arguments for the scalar function.
      /// </param>
      /// <returns>
      /// The result of the scalar function.
      /// </returns>
      public override object Invoke(
          object[] args /* in */
          )
      {
          if (callback1 == null)
          {
              throw new InvalidOperationException(
                  HelperMethods.StringFormat(
                  CultureInfo.CurrentCulture,
                  NoCallbackError, "Invoke"));
          }

          SQLiteInvokeDelegate invokeDelegate =
              callback1 as SQLiteInvokeDelegate;

          if (invokeDelegate != null)
          {
              return invokeDelegate.Invoke(
                  "Invoke", args); /* throw */
          }
          else
          {
#if !PLATFORM_COMPACTFRAMEWORK
              return callback1.DynamicInvoke(
                  GetInvokeArgs(args, false)); /* throw */
#else
              throw new NotImplementedException();
#endif
          }
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// This virtual method is part of the implementation for aggregate
      /// functions.  See the <see cref="SQLiteFunction.Step" /> method
      /// for more details.
      /// </summary>
      /// <param name="args">
      /// The arguments for the aggregate function.
      /// </param>
      /// <param name="stepNumber">
      /// The step number (one based).  This is incrememted each time the
      /// <see cref="Step" /> method is called.
      /// </param>
      /// <param name="contextData">
      /// A placeholder for implementers to store contextual data pertaining
      /// to the current context.
      /// </param>
      public override void Step(
          object[] args,         /* in */
          int stepNumber,        /* in */
          ref object contextData /* in, out */
          )
      {
          if (callback1 == null)
          {
              throw new InvalidOperationException(
                  HelperMethods.StringFormat(
                  CultureInfo.CurrentCulture,
                  NoCallbackError, "Step"));
          }

          SQLiteStepDelegate stepDelegate =
              callback1 as SQLiteStepDelegate;

          if (stepDelegate != null)
          {
              stepDelegate.Invoke(
                  "Step", args, stepNumber,
                  ref contextData); /* throw */
          }
          else
          {
#if !PLATFORM_COMPACTFRAMEWORK
              object[] newArgs = GetStepArgs(
                  args, stepNumber, contextData, false);

              /* IGNORED */
              callback1.DynamicInvoke(newArgs); /* throw */

              UpdateStepArgs(newArgs, ref contextData, false);
#else
              throw new NotImplementedException();
#endif
          }
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// This virtual method is part of the implementation for aggregate
      /// functions.  See the <see cref="SQLiteFunction.Final" /> method
      /// for more details.
      /// </summary>
      /// <param name="contextData">
      /// A placeholder for implementers to store contextual data pertaining
      /// to the current context.
      /// </param>
      /// <returns>
      /// The result of the aggregate function.
      /// </returns>
      public override object Final(
          object contextData /* in */
          )
      {
          if (callback2 == null)
          {
              throw new InvalidOperationException(
                  HelperMethods.StringFormat(
                  CultureInfo.CurrentCulture,
                  NoCallbackError, "Final"));
          }

          SQLiteFinalDelegate finalDelegate =
              callback2 as SQLiteFinalDelegate;

          if (finalDelegate != null)
          {
              return finalDelegate.Invoke(
                  "Final", contextData); /* throw */
          }
          else
          {
#if !PLATFORM_COMPACTFRAMEWORK
              return callback2.DynamicInvoke(GetFinalArgs(
                  contextData, false)); /* throw */
#else
              throw new NotImplementedException();
#endif
          }
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// This virtual method is part of the implementation for aggregate
      /// functions.  See the <see cref="SQLiteFunction.Value" /> method
      /// for more details.
      /// </summary>
      /// <param name="contextData">
      /// A placeholder for implementers to store contextual data pertaining
      /// to the current context.
      /// </param>
      /// <returns>
      /// The result of the aggregate function.
      /// </returns>
      public override object Value(
          object contextData /* in */
          )
      {
          if (callback3 == null)
          {
              throw new InvalidOperationException(
                  HelperMethods.StringFormat(
                  CultureInfo.CurrentCulture,
                  NoCallbackError, "Value"));
          }

          SQLiteFinalDelegate valueDelegate =
              callback3 as SQLiteFinalDelegate;

          if (valueDelegate != null)
          {
              return valueDelegate.Invoke(
                  "Value", contextData); /* throw */
          }
          else
          {
#if !PLATFORM_COMPACTFRAMEWORK
              return callback3.DynamicInvoke(GetValueArgs(
                  contextData, false)); /* throw */
#else
              throw new NotImplementedException();
#endif
          }
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// This virtual method is part of the implementation for aggregate
      /// functions.  See the <see cref="SQLiteFunction.Inverse" /> method
      /// for more details.
      /// </summary>
      /// <param name="args">
      /// The arguments for the aggregate function.
      /// </param>
      /// <param name="stepNumber">
      /// The step number (one based).  This is incrememted each time the
      /// <see cref="Inverse" /> method is called.
      /// </param>
      /// <param name="contextData">
      /// A placeholder for implementers to store contextual data pertaining
      /// to the current context.
      /// </param>
      public override void Inverse(
          object[] args,         /* in */
          int stepNumber,        /* in */
          ref object contextData /* in, out */
          )
      {
          if (callback4 == null)
          {
              throw new InvalidOperationException(
                  HelperMethods.StringFormat(
                  CultureInfo.CurrentCulture,
                  NoCallbackError, "Inverse"));
          }

          SQLiteStepDelegate inverseDelegate =
              callback4 as SQLiteStepDelegate;

          if (inverseDelegate != null)
          {
              inverseDelegate.Invoke(
                  "Inverse", args, stepNumber,
                  ref contextData); /* throw */
          }
          else
          {
#if !PLATFORM_COMPACTFRAMEWORK
              object[] newArgs = GetInverseArgs(
                  args, stepNumber, contextData, false);

              /* IGNORED */
              callback4.DynamicInvoke(newArgs); /* throw */

              UpdateInverseArgs(newArgs, ref contextData, false);
#else
              throw new NotImplementedException();
#endif
          }
      }

      /////////////////////////////////////////////////////////////////////////

      /// <summary>
      /// This virtual method is part of the implementation for collating
      /// sequences.  See the <see cref="SQLiteFunction.Compare" /> method
      /// for more details.
      /// </summary>
      /// <param name="param1">
      /// The first string to compare.
      /// </param>
      /// <param name="param2">
      /// The second strnig to compare.
      /// </param>
      /// <returns>
      /// A positive integer if the <paramref name="param1" /> parameter is
      /// greater than the <paramref name="param2" /> parameter, a negative
      /// integer if the <paramref name="param1" /> parameter is less than
      /// the <paramref name="param2" /> parameter, or zero if they are
      /// equal.
      /// </returns>
      public override int Compare(
          string param1, /* in */
          string param2  /* in */
          )
      {
          if (callback1 == null)
          {
              throw new InvalidOperationException(
                  HelperMethods.StringFormat(
                  CultureInfo.CurrentCulture,
                  NoCallbackError, "Compare"));
          }

          SQLiteCompareDelegate compareDelegate =
              callback1 as SQLiteCompareDelegate;

          if (compareDelegate != null)
          {
              return compareDelegate.Invoke(
                  "Compare", param1, param2); /* throw */
          }
          else
          {
#if !PLATFORM_COMPACTFRAMEWORK
              object result = callback1.DynamicInvoke(GetCompareArgs(
                  param1, param2, false)); /* throw */

              if (result is int)
                  return (int)result;

              throw new InvalidOperationException(
                  HelperMethods.StringFormat(
                  CultureInfo.CurrentCulture,
                  ResultInt32Error, "Compare"));
#else
              throw new NotImplementedException();
#endif
          }
      }
      #endregion
  }

  /////////////////////////////////////////////////////////////////////////////

  /// <summary>
  /// Extends SQLiteFunction and allows an inherited class to obtain the collating sequence associated with a function call.
  /// </summary>
  /// <remarks>
  /// User-defined functions can call the GetCollationSequence() method in this class and use it to compare strings and char arrays.
  /// </remarks>
  public class SQLiteFunctionEx : SQLiteFunction
  {
    /// <summary>
    /// Obtains the collating sequence in effect for the given function.
    /// </summary>
    /// <returns></returns>
    protected CollationSequence GetCollationSequence()
    {
      return _base.GetCollationSequence(this, _context);
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    #region IDisposable "Pattern" Members
    private bool disposed;
    private void CheckDisposed() /* throw */
    {
#if THROW_ON_DISPOSED
        if (disposed)
            throw new ObjectDisposedException(typeof(SQLiteFunctionEx).Name);
#endif
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Cleans up resources (native and managed) associated with the current instance.
    /// </summary>
    /// <param name="disposing">
    /// Zero when being disposed via garbage collection; otherwise, non-zero.
    /// </param>
    protected override void Dispose(bool disposing)
    {
        try
        {
            if (!disposed)
            {
                //if (disposing)
                //{
                //    ////////////////////////////////////
                //    // dispose managed resources here...
                //    ////////////////////////////////////
                //}

                //////////////////////////////////////
                // release unmanaged resources here...
                //////////////////////////////////////
            }
        }
        finally
        {
            base.Dispose(disposing);

            //
            // NOTE: Everything should be fully disposed at this point.
            //
            disposed = true;
        }
    }
    #endregion
  }

  /// <summary>
  /// The type of user-defined function to declare
  /// </summary>
  public enum FunctionType
  {
    /// <summary>
    /// Scalar functions are designed to be called and return a result immediately.  Examples include ABS(), Upper(), Lower(), etc.
    /// </summary>
    Scalar = 0,
    /// <summary>
    /// Aggregate functions are designed to accumulate data until the end of a call and then return a result gleaned from the accumulated data.
    /// Examples include SUM(), COUNT(), AVG(), etc.
    /// </summary>
    Aggregate = 1,
    /// <summary>
    /// Collating sequences are used to sort textual data in a custom manner, and appear in an ORDER BY clause.  Typically text in an ORDER BY is
    /// sorted using a straight case-insensitive comparison function.  Custom collating sequences can be used to alter the behavior of text sorting
    /// in a user-defined manner.
    /// </summary>
    Collation = 2,
    /// <summary>
    /// Window functions are designed to apply aggregate and ranking functions over a particular set of rows.
    /// </summary>
    Window = 3
  }

  /// <summary>
  /// An internal callback delegate declaration.
  /// </summary>
  /// <param name="context">Raw native context pointer for the user function.</param>
  /// <param name="argc">Total number of arguments to the user function.</param>
  /// <param name="argv">Raw native pointer to the array of raw native argument pointers.</param>
#if !PLATFORM_COMPACTFRAMEWORK
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
  public delegate void SQLiteCallback(IntPtr context, int argc, IntPtr argv);
  /// <summary>
  /// An internal final callback delegate declaration.
  /// </summary>
  /// <param name="context">Raw context pointer for the user function</param>
#if !PLATFORM_COMPACTFRAMEWORK
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
  internal delegate void SQLiteFinalCallback(IntPtr context);
  /// <summary>
  /// An internal destroy callback delegate declaration.
  /// </summary>
  /// <param name="pUserData">Raw pointer to the data to free.</param>
#if !PLATFORM_COMPACTFRAMEWORK
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
  internal delegate void SQLiteDestroyCallback(IntPtr pUserData);
  /// <summary>
  /// Internal callback delegate for implementing collating sequences
  /// </summary>
  /// <param name="puser">Not used</param>
  /// <param name="len1">Length of the string pv1</param>
  /// <param name="pv1">Pointer to the first string to compare</param>
  /// <param name="len2">Length of the string pv2</param>
  /// <param name="pv2">Pointer to the second string to compare</param>
  /// <returns>Returns -1 if the first string is less than the second.  0 if they are equal, or 1 if the first string is greater
  /// than the second.</returns>
#if !PLATFORM_COMPACTFRAMEWORK
  [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
#endif
  internal delegate int SQLiteCollation(IntPtr puser, int len1, IntPtr pv1, int len2, IntPtr pv2);

  /// <summary>
  /// The type of collating sequence
  /// </summary>
  public enum CollationTypeEnum
  {
    /// <summary>
    /// The built-in BINARY collating sequence
    /// </summary>
    Binary = 1,
    /// <summary>
    /// The built-in NOCASE collating sequence
    /// </summary>
    NoCase = 2,
    /// <summary>
    /// The built-in REVERSE collating sequence
    /// </summary>
    Reverse = 3,
    /// <summary>
    /// A custom user-defined collating sequence
    /// </summary>
    Custom = 0,
  }

  /// <summary>
  /// The encoding type the collation sequence uses
  /// </summary>
  public enum CollationEncodingEnum
  {
    /// <summary>
    /// The collation sequence is UTF8
    /// </summary>
    UTF8 = 1,
    /// <summary>
    /// The collation sequence is UTF16 little-endian
    /// </summary>
    UTF16LE = 2,
    /// <summary>
    /// The collation sequence is UTF16 big-endian
    /// </summary>
    UTF16BE = 3,
  }

  /// <summary>
  /// A struct describing the collating sequence a function is executing in
  /// </summary>
  public struct CollationSequence
  {
    /// <summary>
    /// The name of the collating sequence
    /// </summary>
    public string Name;
    /// <summary>
    /// The type of collating sequence
    /// </summary>
    public CollationTypeEnum Type;

    /// <summary>
    /// The text encoding of the collation sequence
    /// </summary>
    public CollationEncodingEnum Encoding;

    /// <summary>
    /// Context of the function that requested the collating sequence
    /// </summary>
    internal SQLiteFunction _func;

    /// <summary>
    /// Calls the base collating sequence to compare two strings
    /// </summary>
    /// <param name="s1">The first string to compare</param>
    /// <param name="s2">The second string to compare</param>
    /// <returns>-1 if s1 is less than s2, 0 if s1 is equal to s2, and 1 if s1 is greater than s2</returns>
    public int Compare(string s1, string s2)
    {
      return _func._base.ContextCollateCompare(Encoding, _func._context, s1, s2);
    }

    /// <summary>
    /// Calls the base collating sequence to compare two character arrays
    /// </summary>
    /// <param name="c1">The first array to compare</param>
    /// <param name="c2">The second array to compare</param>
    /// <returns>-1 if c1 is less than c2, 0 if c1 is equal to c2, and 1 if c1 is greater than c2</returns>
    public int Compare(char[] c1, char[] c2)
    {
      return _func._base.ContextCollateCompare(Encoding, _func._context, c1, c2);
    }
  }
}
