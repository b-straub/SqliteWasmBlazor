// Minimal SQLite stub for browser WASM
// All database operations go through JavaScript worker bridge
// This provides native symbols to satisfy P/Invoke requirements

#include <stdint.h>
#include <string.h>
#include <stdlib.h>

// SQLite result codes
#define SQLITE_OK           0
#define SQLITE_ERROR        1
#define SQLITE_NOMEM        7
#define SQLITE_MISUSE       21
#define SQLITE_ROW          100
#define SQLITE_DONE         101

// SQLite types
#define SQLITE_INTEGER  1
#define SQLITE_FLOAT    2
#define SQLITE_TEXT     3
#define SQLITE_BLOB     4
#define SQLITE_NULL     5

// Opaque structure - just a placeholder
typedef struct sqlite3 sqlite3;

typedef struct sqlite3_stmt sqlite3_stmt;
typedef struct sqlite3_value sqlite3_value;
typedef struct sqlite3_context sqlite3_context;
typedef struct sqlite3_backup sqlite3_backup;
typedef struct sqlite3_blob sqlite3_blob;
typedef struct sqlite3_snapshot sqlite3_snapshot;
typedef struct sqlite3_vfs sqlite3_vfs;
typedef int64_t sqlite3_int64;
typedef uint64_t sqlite3_uint64;

//=============================================================================
// Version Information
//=============================================================================

const char* sqlite3_libversion(void) {
    return "3.50.4";
}

const char* sqlite3_sourceid(void) {
    return "stub-wasm-worker-bridge-2025";
}

int sqlite3_libversion_number(void) {
    return 3050004;
}

int sqlite3_threadsafe(void) {
    return 1;
}

//=============================================================================
// Initialization
//=============================================================================

int sqlite3_initialize(void) {
    return SQLITE_OK;
}

int sqlite3_shutdown(void) {
    return SQLITE_OK;
}

int sqlite3_config(int op, ...) {
    return SQLITE_OK;
}

//=============================================================================
// Database Connection
//=============================================================================

int sqlite3_open(const char *filename, sqlite3 **ppDb) {
    *ppDb = NULL;
    return SQLITE_MISUSE;
}

int sqlite3_open_v2(const char *filename, sqlite3 **ppDb, int flags, const char *zVfs) {
    // Return dummy non-NULL pointer (operations go through worker bridge)
    *ppDb = (sqlite3*)1;
    return SQLITE_OK;
}

int sqlite3_open16(const void *filename, sqlite3 **ppDb) {
    *ppDb = NULL;
    return SQLITE_MISUSE;
}

int sqlite3_close(sqlite3 *db) {
    return SQLITE_MISUSE;
}

int sqlite3_close_v2(sqlite3 *db) {
    return SQLITE_OK;
}

int sqlite3_db_config(sqlite3 *db, int op, ...) {
    return SQLITE_OK;
}

//=============================================================================
// Error Handling
//=============================================================================

int sqlite3_errcode(sqlite3 *db) {
    return SQLITE_MISUSE;
}

int sqlite3_extended_errcode(sqlite3 *db) {
    return SQLITE_MISUSE;
}

const char *sqlite3_errmsg(sqlite3 *db) {
    return "stub provider - operation not supported";
}

const void *sqlite3_errmsg16(sqlite3 *db) {
    static const uint16_t msg[] = {'s','t','u','b',0};
    return msg;
}

const char *sqlite3_errstr(int rc) {
    return "stub provider";
}

int sqlite3_extended_result_codes(sqlite3 *db, int onoff) {
    return SQLITE_OK;
}

//=============================================================================
// SQL Execution
//=============================================================================

int sqlite3_exec(sqlite3 *db, const char *sql, int (*callback)(void*,int,char**,char**), void *arg, char **errmsg) {
    return SQLITE_MISUSE;
}

//=============================================================================
// Statement Preparation
//=============================================================================

int sqlite3_prepare_v2(sqlite3 *db, const char *zSql, int nByte, sqlite3_stmt **ppStmt, const char **pzTail) {
    *ppStmt = NULL;
    if (pzTail) *pzTail = NULL;
    return SQLITE_MISUSE;
}

int sqlite3_prepare_v3(sqlite3 *db, const char *zSql, int nByte, unsigned int prepFlags, sqlite3_stmt **ppStmt, const char **pzTail) {
    *ppStmt = NULL;
    if (pzTail) *pzTail = NULL;
    return SQLITE_MISUSE;
}

int sqlite3_prepare16_v2(sqlite3 *db, const void *zSql, int nByte, sqlite3_stmt **ppStmt, const void **pzTail) {
    *ppStmt = NULL;
    if (pzTail) *pzTail = NULL;
    return SQLITE_MISUSE;
}

//=============================================================================
// Statement Execution
//=============================================================================

int sqlite3_step(sqlite3_stmt *pStmt) {
    return SQLITE_DONE;
}

int sqlite3_reset(sqlite3_stmt *pStmt) {
    return SQLITE_OK;
}

int sqlite3_finalize(sqlite3_stmt *pStmt) {
    return SQLITE_OK;
}

int sqlite3_clear_bindings(sqlite3_stmt *pStmt) {
    return SQLITE_OK;
}

const char *sqlite3_sql(sqlite3_stmt *pStmt) {
    return "";
}

//=============================================================================
// Parameter Binding
//=============================================================================

int sqlite3_bind_parameter_count(sqlite3_stmt *pStmt) {
    return 0;
}

int sqlite3_bind_parameter_index(sqlite3_stmt *pStmt, const char *zName) {
    return 0;
}

const char *sqlite3_bind_parameter_name(sqlite3_stmt *pStmt, int i) {
    return NULL;
}

int sqlite3_bind_blob(sqlite3_stmt *pStmt, int i, const void *zData, int nData, void(*xDel)(void*)) {
    return SQLITE_MISUSE;
}

int sqlite3_bind_blob64(sqlite3_stmt *pStmt, int i, const void *zData, sqlite3_uint64 nData, void(*xDel)(void*)) {
    return SQLITE_MISUSE;
}

int sqlite3_bind_double(sqlite3_stmt *pStmt, int i, double rValue) {
    return SQLITE_MISUSE;
}

int sqlite3_bind_int(sqlite3_stmt *pStmt, int i, int iValue) {
    return SQLITE_MISUSE;
}

int sqlite3_bind_int64(sqlite3_stmt *pStmt, int i, sqlite3_int64 iValue) {
    return SQLITE_MISUSE;
}

int sqlite3_bind_null(sqlite3_stmt *pStmt, int i) {
    return SQLITE_MISUSE;
}

int sqlite3_bind_text(sqlite3_stmt *pStmt, int i, const char *zData, int nData, void(*xDel)(void*)) {
    return SQLITE_MISUSE;
}

int sqlite3_bind_text16(sqlite3_stmt *pStmt, int i, const void *zData, int nData, void(*xDel)(void*)) {
    return SQLITE_MISUSE;
}

int sqlite3_bind_text64(sqlite3_stmt *pStmt, int i, const char *zData, sqlite3_uint64 nData, void(*xDel)(void*), unsigned char enc) {
    return SQLITE_MISUSE;
}

int sqlite3_bind_value(sqlite3_stmt *pStmt, int i, const sqlite3_value *pValue) {
    return SQLITE_MISUSE;
}

int sqlite3_bind_zeroblob(sqlite3_stmt *pStmt, int i, int n) {
    return SQLITE_MISUSE;
}

int sqlite3_bind_zeroblob64(sqlite3_stmt *pStmt, int i, sqlite3_uint64 n) {
    return SQLITE_MISUSE;
}

//=============================================================================
// Result Column Access
//=============================================================================

int sqlite3_column_count(sqlite3_stmt *pStmt) {
    return 0;
}

const char *sqlite3_column_name(sqlite3_stmt *pStmt, int N) {
    return "";
}

const void *sqlite3_column_name16(sqlite3_stmt *pStmt, int N) {
    static const uint16_t empty[] = {0};
    return empty;
}

const char *sqlite3_column_database_name(sqlite3_stmt *pStmt, int N) {
    return "";
}

const char *sqlite3_column_table_name(sqlite3_stmt *pStmt, int N) {
    return "";
}

const char *sqlite3_column_origin_name(sqlite3_stmt *pStmt, int N) {
    return "";
}

const char *sqlite3_column_decltype(sqlite3_stmt *pStmt, int N) {
    return "";
}

int sqlite3_column_type(sqlite3_stmt *pStmt, int iCol) {
    return SQLITE_NULL;
}

const void *sqlite3_column_blob(sqlite3_stmt *pStmt, int iCol) {
    return NULL;
}

int sqlite3_column_bytes(sqlite3_stmt *pStmt, int iCol) {
    return 0;
}

int sqlite3_column_bytes16(sqlite3_stmt *pStmt, int iCol) {
    return 0;
}

double sqlite3_column_double(sqlite3_stmt *pStmt, int iCol) {
    return 0.0;
}

int sqlite3_column_int(sqlite3_stmt *pStmt, int iCol) {
    return 0;
}

sqlite3_int64 sqlite3_column_int64(sqlite3_stmt *pStmt, int iCol) {
    return 0;
}

const unsigned char *sqlite3_column_text(sqlite3_stmt *pStmt, int iCol) {
    return (const unsigned char*)"";
}

const void *sqlite3_column_text16(sqlite3_stmt *pStmt, int iCol) {
    static const uint16_t empty[] = {0};
    return empty;
}

sqlite3_value *sqlite3_column_value(sqlite3_stmt *pStmt, int iCol) {
    return NULL;
}

//=============================================================================
// Database Changes
//=============================================================================

int sqlite3_changes(sqlite3 *db) {
    return 0;
}

sqlite3_int64 sqlite3_last_insert_rowid(sqlite3 *db) {
    return 0;
}

int sqlite3_total_changes(sqlite3 *db) {
    return 0;
}

void sqlite3_interrupt(sqlite3 *db) {
}

//=============================================================================
// Memory Management
//=============================================================================

void *sqlite3_malloc(int n) {
    return NULL;
}

void *sqlite3_malloc64(sqlite3_uint64 n) {
    return NULL;
}

void *sqlite3_realloc(void *pOld, int n) {
    return NULL;
}

void *sqlite3_realloc64(void *pOld, sqlite3_uint64 n) {
    return NULL;
}

void sqlite3_free(void *p) {
}

sqlite3_uint64 sqlite3_msize(void *p) {
    return 0;
}

//=============================================================================
// Backup API
//=============================================================================

sqlite3_backup *sqlite3_backup_init(sqlite3 *pDest, const char *zDestName, sqlite3 *pSource, const char *zSourceName) {
    return NULL;
}

int sqlite3_backup_step(sqlite3_backup *p, int nPage) {
    return SQLITE_DONE;
}

int sqlite3_backup_finish(sqlite3_backup *p) {
    return SQLITE_OK;
}

int sqlite3_backup_remaining(sqlite3_backup *p) {
    return 0;
}

int sqlite3_backup_pagecount(sqlite3_backup *p) {
    return 0;
}

//=============================================================================
// Blob I/O
//=============================================================================

int sqlite3_blob_open(sqlite3 *db, const char *zDb, const char *zTable, const char *zColumn, sqlite3_int64 iRow, int flags, sqlite3_blob **ppBlob) {
    *ppBlob = NULL;
    return SQLITE_MISUSE;
}

int sqlite3_blob_close(sqlite3_blob *pBlob) {
    return SQLITE_OK;
}

int sqlite3_blob_bytes(sqlite3_blob *pBlob) {
    return 0;
}

int sqlite3_blob_read(sqlite3_blob *pBlob, void *z, int n, int iOffset) {
    return SQLITE_MISUSE;
}

int sqlite3_blob_write(sqlite3_blob *pBlob, const void *z, int n, int iOffset) {
    return SQLITE_MISUSE;
}

int sqlite3_blob_reopen(sqlite3_blob *pBlob, sqlite3_int64 iRow) {
    return SQLITE_MISUSE;
}

//=============================================================================
// Custom Functions (minimal stubs)
//=============================================================================

int sqlite3_create_function(sqlite3 *db, const char *zFunc, int nArg, int eTextRep, void *pApp,
                             void (*xFunc)(sqlite3_context*,int,sqlite3_value**),
                             void (*xStep)(sqlite3_context*,int,sqlite3_value**),
                             void (*xFinal)(sqlite3_context*)) {
    return SQLITE_OK;
}

int sqlite3_create_function_v2(sqlite3 *db, const char *zFunc, int nArg, int eTextRep, void *pApp,
                                void (*xFunc)(sqlite3_context*,int,sqlite3_value**),
                                void (*xStep)(sqlite3_context*,int,sqlite3_value**),
                                void (*xFinal)(sqlite3_context*),
                                void (*xDestroy)(void*)) {
    return SQLITE_OK;
}

void *sqlite3_user_data(sqlite3_context *pCtx) {
    return NULL;
}

void sqlite3_result_blob(sqlite3_context *pCtx, const void *z, int n, void(*xDel)(void*)) {
}

void sqlite3_result_double(sqlite3_context *pCtx, double rVal) {
}

void sqlite3_result_error(sqlite3_context *pCtx, const char *z, int n) {
}

void sqlite3_result_int(sqlite3_context *pCtx, int iVal) {
}

void sqlite3_result_int64(sqlite3_context *pCtx, sqlite3_int64 iVal) {
}

void sqlite3_result_null(sqlite3_context *pCtx) {
}

void sqlite3_result_text(sqlite3_context *pCtx, const char *z, int n, void(*xDel)(void*)) {
}

//=============================================================================
// VFS (just return NULL/error)
//=============================================================================

sqlite3_vfs *sqlite3_vfs_find(const char *zVfsName) {
    return NULL;
}

int sqlite3_vfs_register(sqlite3_vfs *pVfs, int makeDflt) {
    return SQLITE_OK;
}

int sqlite3_vfs_unregister(sqlite3_vfs *pVfs) {
    return SQLITE_OK;
}

//=============================================================================
// Misc Functions
//=============================================================================

int sqlite3_busy_timeout(sqlite3 *db, int ms) {
    return SQLITE_OK;
}

void *sqlite3_aggregate_context(sqlite3_context *pCtx, int nBytes) {
    return NULL;
}

void sqlite3_progress_handler(sqlite3 *db, int nOps, int(*xProgress)(void*), void *pArg) {
}

int sqlite3_get_autocommit(sqlite3 *db) {
    return 1;
}

sqlite3 *sqlite3_db_handle(sqlite3_stmt *pStmt) {
    return NULL;
}

const char *sqlite3_db_filename(sqlite3 *db, const char *zDbName) {
    return NULL;
}

int sqlite3_limit(sqlite3 *db, int id, int newVal) {
    return 0;
}

//=============================================================================
// Additional Functions (Missing from initial stub)
//=============================================================================

int sqlite3_aggregate_count(sqlite3_context *pCtx) {
    return 0;
}

void *sqlite3_commit_hook(sqlite3 *db, int(*xCallback)(void*), void *pArg) {
    return NULL;
}

const char *sqlite3_compileoption_get(int N) {
    return NULL;
}

int sqlite3_compileoption_used(const char *zOptName) {
    return 0;
}

int sqlite3_complete(const char *sql) {
    return 1;
}

int sqlite3_create_collation(sqlite3 *db, const char *zName, int eTextRep, void *pArg, int(*xCompare)(void*,int,const void*,int,const void*)) {
    return SQLITE_OK;
}

int sqlite3_data_count(sqlite3_stmt *pStmt) {
    return 0;
}

int sqlite3_db_readonly(sqlite3 *db, const char *zDbName) {
    return -1;
}

int sqlite3_db_status(sqlite3 *db, int op, int *pCur, int *pHiwtr, int resetFlg) {
    if (pCur) *pCur = 0;
    if (pHiwtr) *pHiwtr = 0;
    return SQLITE_OK;
}

int sqlite3_deserialize(sqlite3 *db, const char *zSchema, unsigned char *pData, sqlite3_int64 szDb, sqlite3_int64 szBuf, unsigned mFlags) {
    return SQLITE_MISUSE;
}

int sqlite3_enable_load_extension(sqlite3 *db, int onoff) {
    return SQLITE_OK;
}

int sqlite3_enable_shared_cache(int enable) {
    return SQLITE_OK;
}

int sqlite3_file_control(sqlite3 *db, const char *zDbName, int op, void *pArg) {
    return SQLITE_MISUSE;
}

sqlite3_int64 sqlite3_hard_heap_limit64(sqlite3_int64 N) {
    return 0;
}

int sqlite3_keyword_count(void) {
    return 0;
}

int sqlite3_keyword_name(int i, const char **pzName, int *pnName) {
    return SQLITE_ERROR;
}

int sqlite3_load_extension(sqlite3 *db, const char *zFile, const char *zProc, char **pzErrMsg) {
    return SQLITE_ERROR;
}

void sqlite3_log(int iErrCode, const char *zFormat, ...) {
}

sqlite3_int64 sqlite3_memory_highwater(int resetFlag) {
    return 0;
}

sqlite3_int64 sqlite3_memory_used(void) {
    return 0;
}

void *sqlite3_rollback_hook(sqlite3 *db, void(*xCallback)(void*), void *pArg) {
    return NULL;
}

unsigned char *sqlite3_serialize(sqlite3 *db, const char *zSchema, sqlite3_int64 *piSize, unsigned int mFlags) {
    if (piSize) *piSize = 0;
    return NULL;
}

int sqlite3_set_authorizer(sqlite3 *db, int (*xAuth)(void*,int,const char*,const char*,const char*,const char*), void *pUserData) {
    return SQLITE_OK;
}

int sqlite3_sleep(int ms) {
    return 0;
}

sqlite3_int64 sqlite3_soft_heap_limit64(sqlite3_int64 N) {
    return 0;
}

int sqlite3_status(int op, int *pCurrent, int *pHighwater, int resetFlag) {
    if (pCurrent) *pCurrent = 0;
    if (pHighwater) *pHighwater = 0;
    return SQLITE_OK;
}

int sqlite3_status64(int op, sqlite3_int64 *pCurrent, sqlite3_int64 *pHighwater, int resetFlag) {
    if (pCurrent) *pCurrent = 0;
    if (pHighwater) *pHighwater = 0;
    return SQLITE_OK;
}

int sqlite3_stmt_busy(sqlite3_stmt *pStmt) {
    return 0;
}

int sqlite3_stmt_readonly(sqlite3_stmt *pStmt) {
    return 1;
}

int sqlite3_stmt_status(sqlite3_stmt *pStmt, int op, int resetFlg) {
    return 0;
}

int sqlite3_table_column_metadata(sqlite3 *db, const char *zDbName, const char *zTableName, const char *zColumnName, char const **pzDataType, char const **pzCollSeq, int *pNotNull, int *pPrimaryKey, int *pAutoinc) {
    return SQLITE_ERROR;
}

void *sqlite3_trace(sqlite3 *db, void(*xTrace)(void*,const char*), void *pArg) {
    return NULL;
}

void *sqlite3_update_hook(sqlite3 *db, void(*xCallback)(void*,int,char const*,char const*,sqlite3_int64), void *pArg) {
    return NULL;
}

int sqlite3_wal_autocheckpoint(sqlite3 *db, int N) {
    return SQLITE_OK;
}

int sqlite3_wal_checkpoint(sqlite3 *db, const char *zDb) {
    return SQLITE_OK;
}

int sqlite3_wal_checkpoint_v2(sqlite3 *db, const char *zDb, int eMode, int *pnLog, int *pnCkpt) {
    if (pnLog) *pnLog = 0;
    if (pnCkpt) *pnCkpt = 0;
    return SQLITE_OK;
}


//=============================================================================
// sqlite3_value_* Functions
//=============================================================================

const void *sqlite3_value_blob(sqlite3_value *pVal) {
    return NULL;
}

int sqlite3_value_bytes(sqlite3_value *pVal) {
    return 0;
}

double sqlite3_value_double(sqlite3_value *pVal) {
    return 0.0;
}

int sqlite3_value_int(sqlite3_value *pVal) {
    return 0;
}

sqlite3_int64 sqlite3_value_int64(sqlite3_value *pVal) {
    return 0;
}

const unsigned char *sqlite3_value_text(sqlite3_value *pVal) {
    return (const unsigned char*)"";
}

int sqlite3_value_type(sqlite3_value *pVal) {
    return SQLITE_NULL;
}

//=============================================================================
// sqlite3_result_* Additional Functions
//=============================================================================

void sqlite3_result_error_code(sqlite3_context *pCtx, int errCode) {
}

void sqlite3_result_error_nomem(sqlite3_context *pCtx) {
}

void sqlite3_result_error_toobig(sqlite3_context *pCtx) {
}

void sqlite3_result_zeroblob(sqlite3_context *pCtx, int n) {
}

//=============================================================================
// Snapshot Functions
//=============================================================================

int sqlite3_snapshot_cmp(sqlite3_snapshot *p1, sqlite3_snapshot *p2) {
    return 0;
}

void sqlite3_snapshot_free(sqlite3_snapshot *pSnapshot) {
}

int sqlite3_snapshot_get(sqlite3 *db, const char *zDb, sqlite3_snapshot **ppSnapshot) {
    *ppSnapshot = NULL;
    return SQLITE_ERROR;
}

int sqlite3_snapshot_open(sqlite3 *db, const char *zDb, sqlite3_snapshot *pSnapshot) {
    return SQLITE_ERROR;
}

int sqlite3_snapshot_recover(sqlite3 *db, const char *zDb) {
    return SQLITE_ERROR;
}

//=============================================================================
// Miscellaneous Additional Functions
//=============================================================================

sqlite3_stmt *sqlite3_next_stmt(sqlite3 *pDb, sqlite3_stmt *pStmt) {
    return NULL;
}

void *sqlite3_profile(sqlite3 *db, void(*xProfile)(void*,const char*,sqlite3_uint64), void *pArg) {
    return NULL;
}

int sqlite3_stmt_isexplain(sqlite3_stmt *pStmt) {
    return 0;
}

int sqlite3_stricmp(const char *zLeft, const char *zRight) {
    return 0;
}

int sqlite3_strnicmp(const char *zLeft, const char *zRight, int N) {
    return 0;
}

