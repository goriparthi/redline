# CSQLite

The SQLite amalgamation, vendored for Linux and Windows. macOS never compiles this target:
`import SQLite3` there resolves against the SDK's own module map and links the system copy.

Vendored because the alternatives are worse. A system library target needs `libsqlite3-dev`
on every Linux builder, and Windows ships no SQLite that SwiftPM can link at all, so a
downloaded binary would simply fail to start. This keeps the package dependency-free, which
it has always been.

- Version: 3.45.1 (amalgamation, 2024-01-30)
- Source: https://www.sqlite.org/2024/sqlite-amalgamation-3450100.zip
- Licence: public domain

To update, download a newer amalgamation and replace `sqlite3.c`, `include/sqlite3.h` and
`include/sqlite3ext.h`. Nothing here is modified from upstream.
