# OracleApmPatcher
This tool uses Mono Cecil to instrument `Oracle.ManagedDataAccess.dll` with activity events similar to `ActvitySource`.

> [!NOTE]
> Earlier code that created a real `ActivitySource` is available on branch [activity-source](https://github.com/jods4/OracleApmPatcher/tree/activity-source).

## Usage 
Build `Src` then run `OraclePatcher Oracle.ManagedDataAccess.dll` on the command line.

The only argument is the path to `Oracle.ManagedDataAccess.dll` that must be patched.
This tool writes a patched `Oracle.ManagedDataAccess.APM.dll` file in the same folder than the original.

Alternatively, if you have dotnet CLI and don't want to compile this project, you can `dotnet run -- Oracle.ManagedDataAccess.dll` inside `Src` folder.

## Instrumentation
After patching, two new static events `ActivityStart` and `ActivityEnd` are added to `OracleConfiguration`. Both events have type `EventHandler<OracleActivityEventArgs>`.

### OracleActivityEventArgs
```csharp
public class OracleActivityEventArgs : EventArgs
{
    // Indicates which activity starts/end:
    //   "ExecuteNonQuery" (sender is OracleCommand)
    //   "ExecuteReader" (sender is OracleCommand)
    //   "Read" (sender is OracleDataReader)
    public string Activity;
    // When "ExecuteNonQuery" ends successfully: number of rows affected
    // When "Read" ends: number of rows fetched
    public int Rows;
    // When "ExecuteNonQuery" and "ExecuteReader" end: indicates if the command failed
    public bool Failed;
    // The same OracleActivityEventArgs instance is passed to matching ActivityStart and ActivityEnd events
    // This field can be used to share an object between the two handlers
    public object State;
}
```

Note that `"Read"` activity starts when `OracleDataReader` fetches the first row (i.e. first call to `.Read()`) and ends when the reader is closed (call to `.Close()`, also internally called by `Dispose()`). **Be sure to dispose your readers** otherwise activities will never end!

## Sample integration with Elastic APM
Inside `Sample` there is a class `OracleDiagnosticSubscriber.cs` based on those two events that can be used to automatically add Oracle to your Elastic APM, with very similar results as `Elastic.APm.SqlClient`.

Copy this sample file in your project and add the subscriber to your ElasticApm config:
```csharp
builder.UseElasticApm(
    new OracleDiagnosticSubscriber()
);
```