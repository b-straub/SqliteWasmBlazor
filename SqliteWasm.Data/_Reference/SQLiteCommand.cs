/********************************************************
 * ADO.NET 2.0 Data Provider for SQLite Version 3.X
 * Written by Robert Simpson (robert@blackcastlesoft.com)
 *
 * Released to the public domain, use at your own risk!
 ********************************************************/

namespace System.Data.SQLite.Wasm
{
  using System;
  using System.Data;
  using System.Data.Common;
  using System.Diagnostics;
  using System.Collections.Generic;
  using System.ComponentModel;
  using System.Globalization;
  using System.Text;

  /// <summary>
  /// SQLite implementation of DbCommand.
  /// </summary>
#if !PLATFORM_COMPACTFRAMEWORK
  [Designer("SQLite.Designer.SQLiteCommandDesigner, SQLite.Designer, Version=" + SQLite3.DesignerVersion + ", Culture=neutral, PublicKeyToken=db937bc2d44ff139"), ToolboxItem(true)]
#endif
  public sealed class SQLiteCommand : DbCommand, ICloneable
  {
    /// <summary>
    /// These are the extra command behavior flags that should be used for all calls
    /// into the <see cref="ExecuteNonQuery()" />, <see cref="ExecuteScalar()" />,
    /// and <see cref="ExecuteReader()" /> methods.
    /// </summary>
    public static CommandBehavior? GlobalCommandBehaviors = null;

    /// <summary>
    /// The default connection string to be used when creating a temporary
    /// connection to execute a command via the static
    /// <see cref="Execute(string,SQLiteExecuteType,string,object[])" /> or
    /// <see cref="Execute(string,SQLiteExecuteType,CommandBehavior,string,object[])" />
    /// methods.
    /// </summary>
    internal static readonly string DefaultConnectionString = "Data Source=:memory:;";

    /// <summary>
    /// The command text this command is based on
    /// </summary>
    private string _commandText;
    /// <summary>
    /// The connection the command is associated with
    /// </summary>
    private SQLiteConnection _cnn;
    /// <summary>
    /// The version of the connection the command is associated with
    /// </summary>
    private int _version;
    /// <summary>
    /// Indicates whether or not a DataReader is active on the command.
    /// </summary>
    private WeakReference _activeReader;
    /// <summary>
    /// The timeout for the command, kludged because SQLite doesn't support per-command timeout values
    /// </summary>
    internal int _commandTimeout;
    /// <summary>
    /// The maximum amount of time to sleep when retrying a call to prepare or step for the current command.
    /// </summary>
    internal int _maximumSleepTime;
    /// <summary>
    /// Designer support
    /// </summary>
    private bool _designTimeVisible;
    /// <summary>
    /// Used by DbDataAdapter to determine updating behavior
    /// </summary>
    private UpdateRowSource _updateRowSource;
    /// <summary>
    /// The collection of parameters for the command
    /// </summary>
    private SQLiteParameterCollection _parameterCollection;
    /// <summary>
    /// The SQL command text, broken into individual SQL statements as they are executed
    /// </summary>
    internal List<SQLiteStatement> _statementList;
    /// <summary>
    /// Unprocessed SQL text that has not been executed
    /// </summary>
    internal string _remainingText;
    /// <summary>
    /// Transaction associated with this command
    /// </summary>
    private SQLiteTransaction _transaction;

    static SQLiteCommand()
    {
        InitializeGlobalCommandBehaviors();
    }

    ///<overloads>
    /// Constructs a new SQLiteCommand
    /// </overloads>
    /// <summary>
    /// Default constructor
    /// </summary>
    public SQLiteCommand() :this(null, null)
    {
    }

    /// <summary>
    /// Initializes the command with the given command text
    /// </summary>
    /// <param name="commandText">The SQL command text</param>
    public SQLiteCommand(string commandText)
      : this(commandText, null, null)
    {
    }

    /// <summary>
    /// Initializes the command with the given SQL command text and attach the command to the specified
    /// connection.
    /// </summary>
    /// <param name="commandText">The SQL command text</param>
    /// <param name="connection">The connection to associate with the command</param>
    public SQLiteCommand(string commandText, SQLiteConnection connection)
      : this(commandText, connection, null)
    {
    }

    /// <summary>
    /// Initializes the command and associates it with the specified connection.
    /// </summary>
    /// <param name="connection">The connection to associate with the command</param>
    public SQLiteCommand(SQLiteConnection connection)
      : this(null, connection, null)
    {
    }

    private SQLiteCommand(SQLiteCommand source) : this(source.CommandText, source.Connection, source.Transaction)
    {
      CommandTimeout = source.CommandTimeout;
      DesignTimeVisible = source.DesignTimeVisible;
      UpdatedRowSource = source.UpdatedRowSource;

      foreach (SQLiteParameter param in source._parameterCollection)
      {
        Parameters.Add(param.Clone());
      }
    }

    /// <summary>
    /// Initializes a command with the given SQL, connection and transaction
    /// </summary>
    /// <param name="commandText">The SQL command text</param>
    /// <param name="connection">The connection to associate with the command</param>
    /// <param name="transaction">The transaction the command should be associated with</param>
    public SQLiteCommand(string commandText, SQLiteConnection connection, SQLiteTransaction transaction)
    {
      _commandTimeout = 30;
      _maximumSleepTime = 150;
      _parameterCollection = new SQLiteParameterCollection(this);
      _designTimeVisible = true;
      _updateRowSource = UpdateRowSource.None;

      if (commandText != null)
        CommandText = commandText;

      if (connection != null)
      {
        DbConnection = connection;
        _commandTimeout = connection.DefaultTimeout;
        _maximumSleepTime = connection.DefaultMaximumSleepTime;
      }

      if (transaction != null)
        Transaction = transaction;

      if (SQLiteConnection.CanOnChanged(connection, false))
      {
          SQLiteConnection.OnChanged(connection, new ConnectionEventArgs(
              SQLiteConnectionEventType.NewCommand, null, transaction, this,
              null, null, null, null));
      }
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    [Conditional("CHECK_STATE")]
    internal static void Check(SQLiteCommand command)
    {
        if (command == null)
            throw new ArgumentNullException("command");

        command.CheckDisposed();
        SQLiteConnection.Check(command._cnn);
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    #region IDisposable "Pattern" Members
    private bool disposed;
    private void CheckDisposed() /* throw */
    {
#if THROW_ON_DISPOSED
        if (disposed)
            throw new ObjectDisposedException(typeof(SQLiteCommand).Name);
#endif
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Disposes of the command and clears all member variables
    /// </summary>
    /// <param name="disposing">Whether or not the class is being explicitly or implicitly disposed</param>
    protected override void Dispose(bool disposing)
    {
        if (SQLiteConnection.CanOnChanged(_cnn, false))
        {
            SQLiteConnection.OnChanged(
                _cnn, new ConnectionEventArgs(disposing ?
                    SQLiteConnectionEventType.DisposingCommand :
                    SQLiteConnectionEventType.FinalizingCommand,
                null, _transaction, this, null, null, null,
                new object[] { disposing, disposed }));
        }

        bool skippedDispose = false;

        try
        {
            if (!disposed)
            {
                if (disposing)
                {
                    ////////////////////////////////////
                    // dispose managed resources here...
                    ////////////////////////////////////

                    //
                    // NOTE: If there is an active reader on
                    //       us, let it perform the statement
                    //       cleanup.
                    //
                    SQLiteDataReader reader = null;

                    if (_activeReader != null)
                    {
                        try
                        {
                            reader = _activeReader.Target as SQLiteDataReader;
                        }
                        catch (InvalidOperationException)
                        {
                            // do nothing.
                        }
                    }

                    if (reader != null)
                    {
                        reader._disposeCommand = true;

                        if (HelperMethods.HasFlags(reader._flags,
                                SQLiteConnectionFlags.AggressiveDisposal))
                        {
                            //
                            // HACK: Copy the statement list to our active reader,
                            //       after adding a reference to each one of the
                            //       valid statements.
                            //
                            if (_statementList != null)
                            {
                                List<SQLiteStatement> statements =
                                    new List<SQLiteStatement>();

                                foreach (SQLiteStatement statement in _statementList)
                                {
                                    if (statement == null) continue;
                                    statement.AddReference();
                                    statements.Add(statement);
                                }

                                reader._statementList = statements;
                            }
                            else
                            {
                                reader._statementList = null;
                            }
                        }

                        _activeReader = null;
                        skippedDispose = true;
                        return;
                    }

                    Connection = null;
                    _parameterCollection.Clear();
                    _commandText = null;
                }

                //////////////////////////////////////
                // release unmanaged resources here...
                //////////////////////////////////////
            }
        }
        finally
        {
            if (!skippedDispose)
            {
                base.Dispose(disposing);

                //
                // NOTE: Everything should be fully disposed at this point.
                //
                disposed = true;

                if (SQLiteConnection.CanOnChanged(_cnn, false))
                {
                    SQLiteConnection.OnChanged(
                        _cnn, new ConnectionEventArgs(disposing ?
                            SQLiteConnectionEventType.DisposedCommand :
                            SQLiteConnectionEventType.FinalizedCommand,
                        null, _transaction, this, null, null, null,
                        new object[] { disposing, disposed }));
                }
            }
        }
    }
    #endregion

    ///////////////////////////////////////////////////////////////////////////////////////////////

    internal static SQLiteConnectionFlags GetFlags(
        SQLiteCommand command
        )
    {
        try
        {
            if (command != null)
            {
                SQLiteConnection cnn = command._cnn;

                if (cnn != null)
                    return cnn.Flags;
            }
        }
        catch (ObjectDisposedException)
        {
            // do nothing.
        }

        return SQLiteConnectionFlags.Default;
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    internal static int GetPrepareRetries(
        SQLiteCommand command
        )
    {
        try
        {
            if (command != null)
            {
                SQLiteConnection cnn = command._cnn;

                if (cnn != null)
                    return cnn.PrepareRetries;
            }
        }
        catch (ObjectDisposedException)
        {
            // do nothing.
        }

        return SQLiteConnection.DefaultPrepareRetries;
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    internal static int GetStepRetries(
        SQLiteCommand command
        )
    {
        try
        {
            if (command != null)
            {
                SQLiteConnection cnn = command._cnn;

                if (cnn != null)
                    return cnn.StepRetries;
            }
        }
        catch (ObjectDisposedException)
        {
            // do nothing.
        }

        return SQLiteConnection.DefaultStepRetries;
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    internal static int GetMaximumSleepTime(
        SQLiteCommand command
        )
    {
        try
        {
            if (command != null)
                return command._maximumSleepTime;
        }
        catch (ObjectDisposedException)
        {
            // do nothing.
        }

        return SQLiteConnection.DefaultConnectionMaximumSleepTime;
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Modifies the specified <see cref="CommandBehavior" /> to include
    /// the global command behavior flags, if any.
    /// </summary>
    /// <param name="behavior">
    /// The <see cref="CommandBehavior" /> as it was originally passed into
    /// the <see cref="ExecuteNonQuery()" />, <see cref="ExecuteScalar()" />,
    /// or <see cref="ExecuteReader()" /> methods.
    /// </param>
    private void MaybeAddGlobalCommandBehaviors(
        ref CommandBehavior behavior /* in, out */
        )
    {
        CommandBehavior? globalBehaviors = GlobalCommandBehaviors;

        if (globalBehaviors != null)
            behavior |= (CommandBehavior)globalBehaviors;
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    private static void InitializeGlobalCommandBehaviors()
    {
        string value = UnsafeNativeMethods.GetSettingValue(
            "SQLite_GlobalCommandBehaviors", null);

        if (value != null)
        {
            CommandBehavior? behavior;
            string error = null;

            behavior = CombineBehaviors(
                GlobalCommandBehaviors, value, out error);

            if (behavior != null)
            {
                GlobalCommandBehaviors = behavior;
            }
#if !NET_COMPACT_20 && TRACE_WARNING
            else
            {
                HelperMethods.Trace(HelperMethods.StringFormat(
                    CultureInfo.CurrentCulture,
                    "WARNING: Could not initialize global command behaviors: {0}",
                    error), TraceCategory.Warning);
            }
#endif
        }
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    private void DisposeStatements()
    {
        DisposeStatements(true, ref _statementList);
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    internal static void DisposeStatements(
        bool force,
        ref List<SQLiteStatement> statements
        )
    {
        if (statements == null) return;

        int count = statements.Count;

        for (int index = 0; index < count; index++)
        {
            SQLiteStatement statement = statements[index];

            if (statement == null) continue;

            if ((statement.RemoveReference() <= 0) || force)
                statement.Dispose();
        }

        statements.Clear();
        statements = null;
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    private void ClearDataReader()
    {
        if (_activeReader != null)
        {
            SQLiteDataReader reader = null;

            try
            {
                reader = _activeReader.Target as SQLiteDataReader;
            }
            catch (InvalidOperationException)
            {
                // do nothing.
            }

            if (reader != null)
                reader.Close(); /* Dispose */

            _activeReader = null;
        }
    }

    ///////////////////////////////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Clears and destroys all statements currently prepared
    /// </summary>
    internal void ClearCommands()
    {
      ClearDataReader();
      DisposeStatements();

      _parameterCollection.Unbind();
    }

    /// <summary>
    /// Builds an array of prepared statements for each complete SQL statement in the command text
    /// </summary>
    internal SQLiteStatement BuildNextCommand()
    {
        SQLiteStatement stmt = null;

        try
        {
            if ((_cnn != null) && (_cnn._sql != null))
            {
                if (_statementList == null)
                    _remainingText = _commandText;

                stmt = _cnn._sql.Prepare(_cnn, this, _remainingText, (_statementList == null) ? null : _statementList[_statementList.Count - 1], (uint)(_commandTimeout * 1000), ref _remainingText);

                if (stmt != null)
                {
                    stmt.AddReference();
                    stmt._command = this;

                    if (_statementList == null)
                        _statementList = new List<SQLiteStatement>();

                    _statementList.Add(stmt);

                    _parameterCollection.MapParameters(stmt);
                    stmt.BindParameters();
                }
            }
            return stmt;
        }
        catch (Exception)
        {
            if (stmt != null)
            {
                if ((_statementList != null) &&
                    _statementList.Contains(stmt))
                {
                    _statementList.Remove(stmt);
                }

                if ((stmt.RemoveReference() <= 0) ||
                    !HelperMethods.HasFlags(
                        SQLiteConnection.GetFlags(_cnn),
                        SQLiteConnectionFlags.AggressiveDisposal))
                {
                    stmt.Dispose();
                }
            }

            // If we threw an error compiling the statement, we cannot continue on so set the remaining text to null.
            _remainingText = null;

            throw;
        }
    }

    internal SQLiteStatement GetStatement(int index)
    {
      // Haven't built any statements yet
      if (_statementList == null) return BuildNextCommand();

      // If we're at the last built statement and want the next unbuilt statement, then build it
      if (index >= _statementList.Count)
      {
        if (String.IsNullOrEmpty(_remainingText) == false) return BuildNextCommand();
        else return null; // No more commands
      }

      SQLiteStatement stmt = _statementList[index];
      stmt.BindParameters();

      return stmt;
    }

    /// <summary>
    /// Not implemented
    /// </summary>
    public override void Cancel()
    {
      CheckDisposed();

      if (_activeReader != null)
      {
        SQLiteDataReader reader = _activeReader.Target as SQLiteDataReader;
        if (reader != null)
          reader.Cancel();
      }
    }

    /// <summary>
    /// The SQL command text associated with the command
    /// </summary>
#if !PLATFORM_COMPACTFRAMEWORK
    [DefaultValue(""), RefreshProperties(RefreshProperties.All), Editor("Microsoft.VSDesigner.Data.SQL.Design.SqlCommandTextEditor, Microsoft.VSDesigner, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", "System.Drawing.Design.UITypeEditor, System.Drawing, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
#endif
    public override string CommandText
    {
      get
      {
        CheckDisposed();

        return _commandText;
      }
      set
      {
        CheckDisposed();

        string newCommandText = value;

        if (SQLiteConnection.CanOnChanged(_cnn, false))
        {
            ConnectionEventArgs previewEventArgs = new ConnectionEventArgs(
                SQLiteConnectionEventType.SqlStringPreview,
                null, null, null, null, null, null, null, null);

            previewEventArgs.Result = newCommandText;
            SQLiteConnection.OnChanged(_cnn, previewEventArgs);
            newCommandText = previewEventArgs.Result;
        }

        if (_commandText == newCommandText) return;

        if (_activeReader != null && _activeReader.IsAlive)
        {
          throw new InvalidOperationException("Cannot set CommandText while a DataReader is active");
        }

        ClearCommands();
        _commandText = newCommandText;

        if (_cnn == null) return;
      }
    }

    /// <summary>
    /// The amount of time to wait for the connection to become available before erroring out
    /// </summary>
#if !PLATFORM_COMPACTFRAMEWORK
    [DefaultValue((int)30)]
#endif
    public override int CommandTimeout
    {
      get
      {
        CheckDisposed();
        return _commandTimeout;
      }
      set
      {
        CheckDisposed();
        _commandTimeout = value;
      }
    }

    /// <summary>
    /// The maximum amount of time to sleep when retrying a call to prepare or step for the
    /// current command.
    /// </summary>
#if !PLATFORM_COMPACTFRAMEWORK
    [DefaultValue((int)150)]
#endif
    public int MaximumSleepTime
    {
      get
      {
        CheckDisposed();
        return _maximumSleepTime;
      }
      set
      {
        CheckDisposed();
        _maximumSleepTime = value;
      }
    }

    /// <summary>
    /// The type of the command.  SQLite only supports CommandType.Text
    /// </summary>
#if !PLATFORM_COMPACTFRAMEWORK
    [RefreshProperties(RefreshProperties.All), DefaultValue(CommandType.Text)]
#endif
    public override CommandType CommandType
    {
      get
      {
        CheckDisposed();
        return CommandType.Text;
      }
      set
      {
        CheckDisposed();

        if (value != CommandType.Text)
        {
          throw new NotSupportedException();
        }
      }
    }

    /// <summary>
    /// Forwards to the local CreateParameter() function
    /// </summary>
    /// <returns></returns>
    protected override DbParameter CreateDbParameter()
    {
      return CreateParameter();
    }

    /// <summary>
    /// Create a new parameter
    /// </summary>
    /// <returns></returns>
    public new SQLiteParameter CreateParameter()
    {
      CheckDisposed();
      return new SQLiteParameter(this);
    }

    /// <summary>
    /// The connection associated with this command
    /// </summary>
#if !PLATFORM_COMPACTFRAMEWORK
    [DefaultValue((string)null), Editor("Microsoft.VSDesigner.Data.Design.DbConnectionEditor, Microsoft.VSDesigner, Version=8.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a", "System.Drawing.Design.UITypeEditor, System.Drawing, Version=2.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a")]
#endif
    public new SQLiteConnection Connection
    {
      get { CheckDisposed(); return _cnn; }
      set
      {
        CheckDisposed();

        if (_activeReader != null && _activeReader.IsAlive)
          throw new InvalidOperationException("Cannot set Connection while a DataReader is active");

        if (Object.ReferenceEquals(_cnn, value))
          return;

        ClearCommands();

        _cnn = value;

        if (_cnn != null)
          _version = _cnn._version;
      }
    }

    /// <summary>
    /// Forwards to the local Connection property
    /// </summary>
    protected override DbConnection DbConnection
    {
      get
      {
        return Connection;
      }
      set
      {
        Connection = (SQLiteConnection)value;
      }
    }

    /// <summary>
    /// Returns the SQLiteParameterCollection for the given command
    /// </summary>
#if !PLATFORM_COMPACTFRAMEWORK
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Content)]
#endif
    public new SQLiteParameterCollection Parameters
    {
      get { CheckDisposed(); return _parameterCollection; }
    }

    /// <summary>
    /// Forwards to the local Parameters property
    /// </summary>
    protected override DbParameterCollection DbParameterCollection
    {
      get
      {
        return Parameters;
      }
    }

    /// <summary>
    /// The transaction associated with this command.  SQLite only supports one transaction per connection, so this property forwards to the
    /// command's underlying connection.
    /// </summary>
#if !PLATFORM_COMPACTFRAMEWORK
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
#endif
    public new SQLiteTransaction Transaction
    {
      get { CheckDisposed(); return _transaction; }
      set
      {
        CheckDisposed();

        if (_cnn != null)
        {
          if (_activeReader != null && _activeReader.IsAlive)
            throw new InvalidOperationException("Cannot set Transaction while a DataReader is active");

          if (value != null)
          {
            if (value._cnn != _cnn)
              throw new ArgumentException("Transaction is not associated with the command's connection");
          }
          _transaction = value;
        }
        else
        {
          if (value != null) Connection = value.Connection;
          _transaction = value;
        }
      }
    }

    /// <summary>
    /// Forwards to the local Transaction property
    /// </summary>
    protected override DbTransaction DbTransaction
    {
      get
      {
        return Transaction;
      }
      set
      {
        Transaction = (SQLiteTransaction)value;
      }
    }

    /// <summary>
    /// Verifies that all SQL queries associated with the current command text
    /// can be successfully compiled.  A <see cref="SQLiteException" /> will be
    /// raised if any errors occur.
    /// </summary>
    public void VerifyOnly()
    {
        CheckDisposed();

        SQLiteConnection connection = _cnn;
        SQLiteConnection.Check(connection); /* throw */
        SQLiteBase sqlBase = connection._sql;

        if ((connection == null) || (sqlBase == null))
            throw new SQLiteException("invalid or unusable connection");

        List<SQLiteStatement> statements = null;
        SQLiteStatement currentStatement = null;

        try
        {
            string text = _commandText;
            uint timeout = (uint)(_commandTimeout * 1000);
            SQLiteStatement previousStatement = null;

            while ((text != null) && (text.Length > 0))
            {
                currentStatement = sqlBase.Prepare(
                    connection, this, text, previousStatement,
                    timeout, ref text); /* throw */

                previousStatement = currentStatement;

                if (currentStatement != null)
                {
                    if (statements == null)
                        statements = new List<SQLiteStatement>();

                    statements.Add(currentStatement);
                    currentStatement = null;
                }

                if (text == null) continue;
                text = text.Trim();
            }
        }
        finally
        {
            if (currentStatement != null)
            {
                currentStatement.Dispose();
                currentStatement = null;
            }

            if (statements != null)
            {
                foreach (SQLiteStatement statement in statements)
                {
                    if (statement == null)
                        continue;

                    statement.Dispose();
                }

                statements.Clear();
                statements = null;
            }
        }
    }

    /// <summary>
    /// This function ensures there are no active readers, that we have a valid connection,
    /// that the connection is open, that all statements are prepared and all parameters are assigned
    /// in preparation for allocating a data reader.
    /// </summary>
    private void InitializeForReader()
    {
      if (_activeReader != null && _activeReader.IsAlive)
        throw new InvalidOperationException("DataReader already active on this command");

      if (_cnn == null)
        throw new InvalidOperationException("No connection associated with this command");

      if (_cnn.State != ConnectionState.Open)
        throw new InvalidOperationException("Database is not open");

      // If the version of the connection has changed, clear out any previous commands before starting
      if (_cnn._version != _version)
      {
        _version = _cnn._version;
        ClearCommands();
      }

      // Map all parameters for statements already built
      _parameterCollection.MapParameters(null);

      //// Set the default command timeout
      //_cnn._sql.SetTimeout(_commandTimeout * 1000);
    }

    /// <summary>
    /// Creates a new SQLiteDataReader to execute/iterate the array of SQLite prepared statements
    /// </summary>
    /// <param name="behavior">The behavior the data reader should adopt</param>
    /// <returns>Returns a SQLiteDataReader object</returns>
    protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
    {
      return ExecuteReader(behavior);
    }

    /// <summary>
    /// This method creates a new connection, executes the query using the given
    /// execution type, closes the connection, and returns the results.  If the
    /// connection string is null, a temporary in-memory database connection will
    /// be used.
    /// </summary>
    /// <param name="commandText">
    /// The text of the command to be executed.
    /// </param>
    /// <param name="executeType">
    /// The execution type for the command.  This is used to determine which method
    /// of the command object to call, which then determines the type of results
    /// returned, if any.
    /// </param>
    /// <param name="connectionString">
    /// The connection string to the database to be opened, used, and closed.  If
    /// this parameter is null, a temporary in-memory database will be used.
    /// </param>
    /// <param name="args">
    /// The SQL parameter values to be used when building the command object to be
    /// executed, if any.
    /// </param>
    /// <returns>
    /// The results of the query -OR- null if no results were produced from the
    /// given execution type.
    /// </returns>
    public static object Execute(
        string commandText,
        SQLiteExecuteType executeType,
        string connectionString,
        params object[] args
        )
    {
        return Execute(
            commandText, executeType, CommandBehavior.Default,
            connectionString, args);
    }

    /// <summary>
    /// This method creates a new connection, executes the query using the given
    /// execution type and command behavior, closes the connection unless a data
    /// reader is created, and returns the results.  If the connection string is
    /// null, a temporary in-memory database connection will be used.
    /// </summary>
    /// <param name="commandText">
    /// The text of the command to be executed.
    /// </param>
    /// <param name="executeType">
    /// The execution type for the command.  This is used to determine which method
    /// of the command object to call, which then determines the type of results
    /// returned, if any.
    /// </param>
    /// <param name="commandBehavior">
    /// The command behavior flags for the command.
    /// </param>
    /// <param name="connectionString">
    /// The connection string to the database to be opened, used, and closed.  If
    /// this parameter is null, a temporary in-memory database will be used.
    /// </param>
    /// <param name="args">
    /// The SQL parameter values to be used when building the command object to be
    /// executed, if any.
    /// </param>
    /// <returns>
    /// The results of the query -OR- null if no results were produced from the
    /// given execution type.
    /// </returns>
    public static object Execute(
        string commandText,
        SQLiteExecuteType executeType,
        CommandBehavior commandBehavior,
        string connectionString,
        params object[] args
        )
    {
        SQLiteConnection connection = null;

        try
        {
            if (connectionString == null)
                connectionString = DefaultConnectionString;

            using (connection = new SQLiteConnection(connectionString))
            {
                connection.Open();

                using (SQLiteCommand command = connection.CreateCommand())
                {
                    command.CommandText = commandText;

                    if (args != null)
                    {
                        foreach (object arg in args)
                        {
                            SQLiteParameter parameter = arg as SQLiteParameter;

                            if (parameter == null)
                            {
                                parameter = command.CreateParameter();
                                parameter.DbType = DbType.Object;
                                parameter.Value = arg;
                            }

                            command.Parameters.Add(parameter);
                        }
                    }

                    switch (executeType)
                    {
                        case SQLiteExecuteType.None:
                            {
                                //
                                // NOTE: Do nothing.
                                //
                                break;
                            }
                        case SQLiteExecuteType.NonQuery:
                            {
                                return command.ExecuteNonQuery(commandBehavior);
                            }
                        case SQLiteExecuteType.Scalar:
                            {
                                return command.ExecuteScalar(commandBehavior);
                            }
                        case SQLiteExecuteType.Reader:
                            {
                                bool success = true;

                                try
                                {
                                    //
                                    // NOTE: The CloseConnection flag is being added here.
                                    //       This should force the returned data reader to
                                    //       close the connection when it is disposed.  In
                                    //       order to prevent the containing using block
                                    //       from disposing the connection prematurely,
                                    //       the innermost finally block sets the internal
                                    //       no-disposal flag to true.  The outer finally
                                    //       block will reset the internal no-disposal flag
                                    //       to false so that the data reader will be able
                                    //       to (eventually) dispose of the connection.
                                    //
                                    return command.ExecuteReader(
                                        commandBehavior | CommandBehavior.CloseConnection);
                                }
                                catch
                                {
                                    success = false;
                                    throw;
                                }
                                finally
                                {
                                    //
                                    // NOTE: If an exception was not thrown, that can only
                                    //       mean the data reader was successfully created
                                    //       and now owns the connection.  Therefore, set
                                    //       the internal no-disposal flag (temporarily)
                                    //       in order to exit the containing using block
                                    //       without disposing it.
                                    //
                                    if (success)
                                        connection._noDispose = true;
                                }
                            }
                    }
                }
            }
        }
        finally
        {
            //
            // NOTE: Now that the using block has been exited, reset the
            //       internal disposal flag for the connection.  This is
            //       always done if the connection was created because
            //       it will be harmless whether or not the data reader
            //       now owns it.
            //
            if (connection != null)
                connection._noDispose = false;
        }

        return null;
    }

    /// <summary>
    /// This method executes a query using the given execution type and command
    /// behavior and returns the results.
    /// </summary>
    /// <param name="commandText">
    /// The text of the command to be executed.
    /// </param>
    /// <param name="executeType">
    /// The execution type for the command.  This is used to determine which method
    /// of the command object to call, which then determines the type of results
    /// returned, if any.
    /// </param>
    /// <param name="commandBehavior">
    /// The command behavior flags for the command.
    /// </param>
    /// <param name="connection">
    /// The connection used to create and execute the command.
    /// </param>
    /// <param name="args">
    /// The SQL parameter values to be used when building the command object to be
    /// executed, if any.
    /// </param>
    /// <returns>
    /// The results of the query -OR- null if no results were produced from the
    /// given execution type.
    /// </returns>
    public static object Execute(
        string commandText,
        SQLiteExecuteType executeType,
        CommandBehavior commandBehavior,
        SQLiteConnection connection,
        params object[] args
        )
    {
        SQLiteConnection.Check(connection);

        using (SQLiteCommand command = connection.CreateCommand())
        {
            command.CommandText = commandText;

            if (args != null)
            {
                foreach (object arg in args)
                {
                    SQLiteParameter parameter = arg as SQLiteParameter;

                    if (parameter == null)
                    {
                        parameter = command.CreateParameter();
                        parameter.DbType = DbType.Object;
                        parameter.Value = arg;
                    }

                    command.Parameters.Add(parameter);
                }
            }

            switch (executeType)
            {
                case SQLiteExecuteType.None:
                    {
                        //
                        // NOTE: Do nothing.
                        //
                        break;
                    }
                case SQLiteExecuteType.NonQuery:
                    {
                        return command.ExecuteNonQuery(commandBehavior);
                    }
                case SQLiteExecuteType.Scalar:
                    {
                        return command.ExecuteScalar(commandBehavior);
                    }
                case SQLiteExecuteType.Reader:
                    {
                        return command.ExecuteReader(commandBehavior);
                    }
            }
        }

        return null;
    }

    /// <summary>
    /// Overrides the default behavior to return a SQLiteDataReader specialization class
    /// </summary>
    /// <param name="behavior">The flags to be associated with the reader.</param>
    /// <returns>A SQLiteDataReader</returns>
    public new SQLiteDataReader ExecuteReader(CommandBehavior behavior)
    {
      CheckDisposed();
      SQLiteConnection.Check(_cnn);
      InitializeForReader();

      MaybeAddGlobalCommandBehaviors(ref behavior);

      SQLiteDataReader rd = new SQLiteDataReader(this, behavior);
      _activeReader = new WeakReference(rd, false);

      return rd;
    }

    /// <summary>
    /// Overrides the default behavior of DbDataReader to return a specialized SQLiteDataReader class
    /// </summary>
    /// <returns>A SQLiteDataReader</returns>
    public new SQLiteDataReader ExecuteReader()
    {
      CheckDisposed();
      SQLiteConnection.Check(_cnn);
      return ExecuteReader(CommandBehavior.Default);
    }

    /// <summary>
    /// Called by the SQLiteDataReader when the data reader is closed.
    /// </summary>
    internal void ResetDataReader()
    {
      _activeReader = null;
    }

    /// <summary>
    /// Execute the command and return the number of rows inserted/updated affected by it.
    /// </summary>
    /// <returns>The number of rows inserted/updated affected by it.</returns>
    public override int ExecuteNonQuery()
    {
        CheckDisposed();
        SQLiteConnection.Check(_cnn);
        return ExecuteNonQuery(CommandBehavior.Default);
    }

    /// <summary>
    /// Execute the command and return the number of rows inserted/updated affected by it.
    /// </summary>
    /// <param name="behavior">The flags to be associated with the reader.</param>
    /// <returns>The number of rows inserted/updated affected by it.</returns>
    public int ExecuteNonQuery(
        CommandBehavior behavior
        )
    {
      CheckDisposed();
      SQLiteConnection.Check(_cnn);

      MaybeAddGlobalCommandBehaviors(ref behavior);

      using (SQLiteDataReader reader = ExecuteReader(behavior |
          CommandBehavior.SingleRow | CommandBehavior.SingleResult))
      {
        //
        // BUGFIX: See PrivateMaybeReadRemaining comments.
        //
        PrivateMaybeReadRemaining(reader, behavior);

        while (reader.NextResult()) ;
        return reader.RecordsAffected;
      }
    }

    ///////////////////////////////////////////////////////////////////////

    /// <summary>
    /// This integer value is used with <see cref="CommandBehavior" />
    /// values.  When set, extra <see cref="SQLiteDataReader.Read" />
    /// calls are not performed within the <see cref="ExecuteScalar()" />
    /// methods for write transactions.  This value should be used with
    /// extreme care because it can cause unusual behavior.  It is
    /// intended for use only by legacy applications that rely on the
    /// old, incorrect behavior.
    /// </summary>
    public const CommandBehavior SkipExtraReads =
        (CommandBehavior)0x10000000;

    /// <summary>
    /// This integer value is used with <see cref="CommandBehavior" />
    /// values.  When set, extra <see cref="SQLiteDataReader.Read" />
    /// calls are performed within the <see cref="ExecuteScalar()" />
    /// methods for all transactions.  This value should be used with
    /// extreme care because it can cause unusual behavior.
    /// </summary>
    public const CommandBehavior ForceExtraReads =
        (CommandBehavior)0x20000000;

    /// <summary>
    /// Checks to see if the extra <see cref="SQLiteDataReader.Read" />
    /// calls within the <see cref="ExecuteScalar()" /> methods should
    /// be skipped.
    /// </summary>
    /// <param name="behavior">
    /// The behavior flags, exactly as they were passed into the
    /// <see cref="ExecuteScalar()" /> methods.
    /// </param>
    /// <returns>
    /// Non-zero if the extra reads should be skipped; otherwise, zero.
    /// </returns>
    private bool ShouldSkipExtraReads(
        CommandBehavior behavior /* in */
        )
    {
        return (behavior & SkipExtraReads) == SkipExtraReads;
    }

    /// <summary>
    /// Checks to see if the extra <see cref="SQLiteDataReader.Read" />
    /// calls within the <see cref="ExecuteScalar()" /> methods should
    /// be forced.
    /// </summary>
    /// <param name="behavior">
    /// The behavior flags, exactly as they were passed into the
    /// <see cref="ExecuteScalar()" /> methods.
    /// </param>
    /// <returns>
    /// Non-zero if the extra reads should be forced; otherwise, zero.
    /// </returns>
    private bool ShouldForceExtraReads(
        CommandBehavior behavior /* in */
        )
    {
        return (behavior & ForceExtraReads) == ForceExtraReads;
    }

    ///////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Attempts to combine an original <see cref="CommandBehavior" />
    /// value with a list of new <see cref="CommandBehavior" /> values.
    /// </summary>
    /// <param name="behavior">
    /// The original <see cref="CommandBehavior" /> value, if any.  If
    /// this value is null, a suitable default value will be used.
    /// </param>
    /// <param name="flags">
    /// The list of new <see cref="CommandBehavior" /> values delimited
    /// by spaces or commas.  Each value may have an optional prefix, a
    /// '+' or '-' sign.  If the prefix is a '+', the value is added to
    /// the original <see cref="CommandBehavior" /> value.  If the
    /// prefix is a '-', the value is removed from the original
    /// <see cref="CommandBehavior" /> value.  In addition to the values
    /// formally defined for <see cref="CommandBehavior" />, the extra
    /// values "SkipExtraReads" and "ForceExtraReads" are recognized.
    /// Other extra values may be added in the future.
    /// </param>
    /// <param name="error">
    /// Upon success, this will be set to null.  Upon failure, this will
    /// be set to an appropriate error message.
    /// </param>
    /// <returns>
    /// The resulting <see cref="CommandBehavior" /> value -OR- null
    /// if it cannot be determined due to an error -OR- null if the
    /// original <see cref="CommandBehavior" /> value and list of new
    /// <see cref="CommandBehavior" /> value are both null.  The way to
    /// differentiate between these two null return scenarios is to
    /// check the error message against null.  If the error message is
    /// not null, an error was encountered; otherwise, the null was the
    /// natural return value.
    /// </returns>
    public static CommandBehavior? CombineBehaviors(
        CommandBehavior? behavior, /* in: OPTIONAL */
        string flags,              /* in: OPTIONAL */
        out string error           /* out */
        )
    {
        error = null;

        if (String.IsNullOrEmpty(flags))
            return behavior;

        string[] parts = flags.Split(' ', ',');

        if (parts == null)
        {
            error = "could not split flags into parts";
            return null;
        }

        CommandBehavior localBehavior = (behavior != null) ?
            (CommandBehavior)behavior : (CommandBehavior)0;

        int length = parts.Length;
        bool add = true;

        for (int index = 0; index < length; index++)
        {
            string part = parts[index];

            if (part != null)
                part = part.Trim();

            if (String.IsNullOrEmpty(part))
                continue;

            char subPart = part[0];

            if ((subPart == '+') || (subPart == '-'))
            {
                add = (subPart == '+');
                part = part.Substring(1).Trim();
            }

            if (String.IsNullOrEmpty(part))
                continue;

            object enumValue;

            if (String.Equals(
                    part, "SkipExtraReads",
                    StringComparison.OrdinalIgnoreCase))
            {
                enumValue = SkipExtraReads;
            }
            else if (String.Equals(
                    part, "ForceExtraReads",
                    StringComparison.OrdinalIgnoreCase))
            {
                enumValue = ForceExtraReads;
            }
            else
            {
                try
                {
                    enumValue = Enum.Parse( /* CLRv2+ */
                        typeof(CommandBehavior), part,
                        true); /* throw */
                }
                catch (Exception e)
                {
                    error = e.ToString();
                    return null;
                }
            }

            CommandBehavior flag = (CommandBehavior)enumValue;

            if (add)
                localBehavior |= flag;
            else
                localBehavior &= ~flag;
        }

        return localBehavior;
    }

    ///////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Checks if extra calls to the <see cref="SQLiteDataReader.Read" />
    /// method are necessary.  If so, it attempts to perform them.  If
    /// not, nothing is done.
    /// </summary>
    /// <param name="reader">
    /// The data reader instance as it was received from one of the
    /// <see cref="ExecuteReader()" /> methods.
    /// </param>
    /// <param name="behavior">
    /// The original command behavior flags as passed into one of the
    /// query execution methods.
    /// </param>
    /// <returns>
    /// The number of extra calls to <see cref="SQLiteDataReader.Read" />
    /// that were performed -OR- negative one to indicate they were not
    /// enabled.
    /// </returns>
    public int MaybeReadRemaining(
        SQLiteDataReader reader, /* in */
        CommandBehavior behavior /* in */
        )
    {
        CheckDisposed();

        return PrivateMaybeReadRemaining(reader, behavior);
    }

    ///////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Checks if extra calls to the <see cref="SQLiteDataReader.Read" />
    /// method are necessary.  If so, it attempts to perform them.  If
    /// not, nothing is done.
    /// </summary>
    /// <param name="reader">
    /// The data reader instance as it was received from one of the
    /// <see cref="ExecuteReader()" /> methods.
    /// </param>
    /// <param name="behavior">
    /// The original command behavior flags as passed into one of the
    /// query execution methods.
    /// </param>
    /// <returns>
    /// The number of extra calls to <see cref="SQLiteDataReader.Read" />
    /// that were performed -OR- negative one to indicate they were not
    /// enabled.
    /// </returns>
    private int PrivateMaybeReadRemaining(
        SQLiteDataReader reader, /* in */
        CommandBehavior behavior /* in */
        )
    {
        //
        // BUGFIX: There are SQL statements that cause a write transaction
        //         to be started and that always require more than one step
        //         to be successfully completed, e.g. INSERT with RETURNING
        //         clause.  Therefore, if there is a write transaction in
        //         progress, keep stepping until done unless forbidden from
        //         doing so by the caller.
        //
        if (!ShouldSkipExtraReads(behavior) &&
            (ShouldForceExtraReads(behavior) ||
            MatchTransactionState(SQLiteTransactionState.SQLITE_TXN_WRITE)))
        {
            int count = 0;

            while (reader.PrivateRead(true))
                count++;

            return count;
        }

        return -1;
    }

    ///////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Checks transaction state of the associated database connection.
    /// </summary>
    /// <param name="transactionState">
    /// The desired transaction state.
    /// </param>
    /// <returns>
    /// Non-zero if current transaction state of the associated database
    /// connection matches the desired transaction state.
    /// </returns>
    private bool MatchTransactionState(
        SQLiteTransactionState transactionState /* in */
        )
    {
        //
        // NOTE: The underlying sqlite3_txn_state() core library API
        //       is not available until release 3.34.1.
        //
        if (UnsafeNativeMethods.sqlite3_libversion_number() < 3034001)
            return false;

        SQLiteConnection cnn = _cnn;

        if (cnn == null)
            return false;

        SQLiteBase sql = _cnn._sql;

        if (sql == null)
            return false;

        return sql.GetTransactionState(null) == transactionState;
    }

    ///////////////////////////////////////////////////////////////////////

    /// <summary>
    /// Execute the command and return the first column of the first row of the resultset
    /// (if present), or null if no resultset was returned.
    /// </summary>
    /// <returns>The first column of the first row of the first resultset from the query.</returns>
    public override object ExecuteScalar()
    {
      CheckDisposed();
      SQLiteConnection.Check(_cnn);
      return ExecuteScalar(CommandBehavior.Default);
    }

    /// <summary>
    /// Execute the command and return the first column of the first row of the resultset
    /// (if present), or null if no resultset was returned.
    /// </summary>
    /// <param name="behavior">The flags to be associated with the reader.</param>
    /// <returns>The first column of the first row of the first resultset from the query.</returns>
    public object ExecuteScalar(
        CommandBehavior behavior
        )
    {
      CheckDisposed();
      SQLiteConnection.Check(_cnn);

      MaybeAddGlobalCommandBehaviors(ref behavior);

      object result = null;

      using (SQLiteDataReader reader = ExecuteReader(behavior |
          CommandBehavior.SingleRow | CommandBehavior.SingleResult))
      {
        if (reader.PrivateRead(false))
        {
          if (reader.FieldCount > 0)
            result = reader[0];

          //
          // BUGFIX: See PrivateMaybeReadRemaining comments.
          //
          PrivateMaybeReadRemaining(reader, behavior);
        }
      }

      return result;
    }

    /// <summary>
    /// This method resets all the prepared statements held by this instance
    /// back to their initial states, ready to be re-executed.
    /// </summary>
    public void Reset()
    {
        CheckDisposed();
        SQLiteConnection.Check(_cnn);

        Reset(true, false);
    }

    /// <summary>
    /// This method resets all the prepared statements held by this instance
    /// back to their initial states, ready to be re-executed.
    /// </summary>
    /// <param name="clearBindings">
    /// Non-zero if the parameter bindings should be cleared as well.
    /// </param>
    /// <param name="ignoreErrors">
    /// If this is zero, a <see cref="SQLiteException" /> may be thrown for
    /// any unsuccessful return codes from the native library; otherwise, a
    /// <see cref="SQLiteException" /> will only be thrown if the connection
    /// or its state is invalid.
    /// </param>
    public void Reset(
        bool clearBindings,
        bool ignoreErrors
        )
    {
        CheckDisposed();
        SQLiteConnection.Check(_cnn);

        if (clearBindings && (_parameterCollection != null))
            _parameterCollection.Unbind();

        ClearDataReader();

        if (_statementList == null)
            return;

        SQLiteBase sqlBase = _cnn._sql;
        SQLiteErrorCode rc;

        foreach (SQLiteStatement item in _statementList)
        {
            if (item == null)
                continue;

            SQLiteStatementHandle stmt = item._sqlite_stmt;

            if (stmt == null)
                continue;

            rc = sqlBase.Reset(item);

            if ((rc == SQLiteErrorCode.Ok) && clearBindings &&
                (SQLite3.SQLiteVersionNumber >= 3003007))
            {
                rc = UnsafeNativeMethods.sqlite3_clear_bindings(stmt);
            }

            if (!ignoreErrors && (rc != SQLiteErrorCode.Ok))
                throw new SQLiteException(rc, sqlBase.GetLastError());
        }
    }

    /// <summary>
    /// Does nothing.  Commands are prepared as they are executed the first time, and kept in prepared state afterwards.
    /// </summary>
    public override void Prepare()
    {
      CheckDisposed();
      SQLiteConnection.Check(_cnn);
    }

    /// <summary>
    /// Sets the method the SQLiteCommandBuilder uses to determine how to update inserted or updated rows in a DataTable.
    /// </summary>
    [DefaultValue(UpdateRowSource.None)]
    public override UpdateRowSource UpdatedRowSource
    {
      get
      {
        CheckDisposed();
        return _updateRowSource;
      }
      set
      {
        CheckDisposed();
        _updateRowSource = value;
      }
    }

    /// <summary>
    /// Determines if the command is visible at design time.  Defaults to True.
    /// </summary>
#if !PLATFORM_COMPACTFRAMEWORK
    [DesignOnly(true), Browsable(false), DefaultValue(true), EditorBrowsable(EditorBrowsableState.Never)]
#endif
    public override bool DesignTimeVisible
    {
      get
      {
        CheckDisposed();
        return _designTimeVisible;
      }
      set
      {
        CheckDisposed();

        _designTimeVisible = value;
#if !PLATFORM_COMPACTFRAMEWORK
        TypeDescriptor.Refresh(this);
#endif
      }
    }

    /// <summary>
    /// Clones a command, including all its parameters
    /// </summary>
    /// <returns>A new SQLiteCommand with the same commandtext, connection and parameters</returns>
    public object Clone()
    {
      CheckDisposed();
      return new SQLiteCommand(this);
    }

    /// <summary>
    /// This method attempts to build and return a string containing diagnostic
    /// information for use by the test suite.  This method should not be used
    /// by application code.  It is designed for use by the test suite only and
    /// may be modified or removed at any time.
    /// </summary>
    /// <returns>
    /// A string containing information for use by the test suite -OR- null if
    /// that information is not available.
    /// </returns>
    public string GetDiagnostics()
    {
        CheckDisposed();

        StringBuilder builder = new StringBuilder();

        if (_statementList != null)
        {
            for (int index = 0; index < _statementList.Count; index++)
            {
                SQLiteStatement statement = _statementList[index];

                if (statement == null)
                    continue;

                builder.AppendFormat(CultureInfo.CurrentCulture,
                    "#{0}, sql = {{{1}}}", index,
                    statement._sqlStatement);

                if (statement._prepareSchemaRetries > 0)
                {
                    builder.AppendFormat(CultureInfo.CurrentCulture,
                        ", prepareSchemaRetries = {0}",
                        statement._prepareSchemaRetries);
                }

                if (statement._prepareLockRetries > 0)
                {
                    builder.AppendFormat(CultureInfo.CurrentCulture,
                        ", prepareLockRetries = {0}",
                        statement._prepareLockRetries);
                }

                if (statement._stepSchemaRetries > 0)
                {
                    builder.AppendFormat(CultureInfo.CurrentCulture,
                        ", stepSchemaRetries = {0}",
                        statement._stepSchemaRetries);
                }

                if (statement._stepLockRetries > 0)
                {
                    builder.AppendFormat(CultureInfo.CurrentCulture,
                        ", stepLockRetries = {0}",
                        statement._stepLockRetries);
                }

#if !NET_COMPACT_20
                builder.AppendLine();
#else
                builder.AppendFormat(CultureInfo.CurrentCulture,
                    "\r\n");
#endif
            }
        }

        return builder.ToString();
    }
  }
}