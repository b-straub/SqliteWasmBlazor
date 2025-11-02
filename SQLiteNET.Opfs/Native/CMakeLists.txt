cmake_minimum_required(VERSION 3.15)
project(sqlite_tracking C)

# Emscripten-specific settings
if(EMSCRIPTEN)
    message(STATUS "Building for Emscripten/WASM")

    # Emscripten compiler flags for SQLite
    set(CMAKE_C_FLAGS "${CMAKE_C_FLAGS} \
        -O3 \
        -DSQLITE_OMIT_LOAD_EXTENSION \
        -DSQLITE_DISABLE_LFS \
        -DSQLITE_ENABLE_FTS3 \
        -DSQLITE_ENABLE_FTS3_PARENTHESIS \
        -DSQLITE_ENABLE_FTS4 \
        -DSQLITE_ENABLE_FTS5 \
        -DSQLITE_ENABLE_JSON1 \
        -DSQLITE_ENABLE_RTREE \
        -DSQLITE_ENABLE_EXPLAIN_COMMENTS \
        -DSQLITE_ENABLE_DBPAGE_VTAB \
        -DSQLITE_ENABLE_STMTVTAB \
        -DSQLITE_ENABLE_DBSTAT_VTAB \
        -DSQLITE_ENABLE_BYTECODE_VTAB \
        -DSQLITE_ENABLE_OFFSET_SQL_FUNC \
        -DSQLITE_OMIT_DEPRECATED \
        -DSQLITE_OMIT_SHARED_CACHE \
        -DSQLITE_THREADSAFE=0 \
        -DSQLITE_DEFAULT_MEMSTATUS=0 \
        -DSQLITE_DEFAULT_WAL_SYNCHRONOUS=1 \
        -DSQLITE_LIKE_DOESNT_MATCH_BLOBS \
        -DSQLITE_MAX_EXPR_DEPTH=0 \
        -DSQLITE_OMIT_PROGRESS_CALLBACK \
        -DSQLITE_OMIT_AUTOINIT \
        -DSQLITE_DEFAULT_CACHE_SIZE=-16000 \
        -DSQLITE_ENABLE_NORMALIZE \
        -DSQLITE_DQS=0 \
        -DSQLITE_DEFAULT_FOREIGN_KEYS=1 \
        -DSQLITE_DEFAULT_SYNCHRONOUS=1 \
        -DSQLITE_USE_URI=1 \
        -DSQLITE_CORE \
        -DSQLITE_WASM_MODE=1")

    # Emscripten link flags
    set(CMAKE_EXE_LINKER_FLAGS "${CMAKE_EXE_LINKER_FLAGS} \
        -sALLOW_MEMORY_GROWTH=1 \
        -sEXPORTED_RUNTIME_METHODS=FS,ccall,cwrap \
        -sERROR_ON_UNDEFINED_SYMBOLS=0")
else()
    message(FATAL_ERROR "This CMake configuration is designed for Emscripten only")
endif()

# SQLite source files (assuming downloaded)
set(SQLITE_DIR "${CMAKE_CURRENT_SOURCE_DIR}/sqlite")
set(SQLITE_SRC "${SQLITE_DIR}/sqlite3.c")

# Our VFS tracking sources
set(VFS_TRACKING_SRC
    "${CMAKE_CURRENT_SOURCE_DIR}/src/vfs_tracking.c"
)

# Include directories
include_directories(
    "${SQLITE_DIR}"
    "${CMAKE_CURRENT_SOURCE_DIR}/src"
)

# Create static library with SQLite + VFS tracking
add_library(e_sqlite3 STATIC
    ${SQLITE_SRC}
    ${VFS_TRACKING_SRC}
)

# Set output name
set_target_properties(e_sqlite3 PROPERTIES
    OUTPUT_NAME "e_sqlite3"
    ARCHIVE_OUTPUT_DIRECTORY "${CMAKE_CURRENT_SOURCE_DIR}/../wwwroot"
)

message(STATUS "SQLite library will be built to: ${CMAKE_CURRENT_SOURCE_DIR}/../wwwroot/libe_sqlite3.a")
