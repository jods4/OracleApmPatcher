# OracleApmPatcher
This tool uses Mono Cecil to instrument `Oracle.ManagedDataAccess.dll` with `ActivitySource`.

## Usage 
Run `OraclePatcher Oracle.ManagedDataAccess.dll` on the command line.

The only argument is the path to `Oracle.ManagedDataAccess.dll` that must be patched.
This tool writes a patched `Oracle.ManagedDataAccess.APM.dll` file in the same folder than the original.

Alternatively, if you have dotnet CLI and don't want to compile this project, you can `dotnet run -- Oracle.ManagedDataAccess.dll`.

## Target runtime
This script will reference `Activity` and `ActivitySource` from the same SDK as you're running/compiling with.
So if you want to use the patched library on .net 5, be sure to run it with the .net 5 SDK.

## Instrumentation
The following `ActivitySource` are added into ODP.NET:

### ActivitySource `Oracle.ManagedDataAccess.Client.OracleCommand`
#### Activity `ExecuteReader`
Wraps `ExecuteReader` calls.

Has a tag `cmd` with this `OracleCommand` instance, so you have access to `CommandText`, `CommandType`, `Parameters`, etc.

If the call throws an exception, adds a tag `otel.status_code` with value `ERROR`.

#### Activity `ExecuteNonQuery`
Wraps `ExecuteNonQuery` calls.

Has a tag `cmd` with this `OracleCommand` instance, so you have access to `CommandText`, `CommandType`, `Parameters`, etc.

On successful completion, adds a tag `affected` with the number of affected rows (aka the return value).

If the call throws an exception, adds a tag `otel.status_code` with value `ERROR`.

### ActivitySource `Oracle.ManagedDataAccess.Client.OracleDataReader`
#### Activity `FetchData`
Starts when reader is created, stops when `Close` is called: be sure to `Dispose()` your readers -- or wrap them in `using` calls!

One completion adds a tag `rows` with the number of rows that were fetched from DB (calls to `Read()` that returned `true`).
