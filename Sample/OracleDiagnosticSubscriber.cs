using Elastic.Apm;
using Elastic.Apm.Api;
using Elastic.Apm.DiagnosticSource;
using Oracle.ManagedDataAccess.Client;
using System;
using System.Data;
using System.Reflection;

namespace Elastic.Apm.Oracle;

public class OracleDiagnosticSubscriber : IDiagnosticsSubscriber
{
  public IDisposable Subscribe(IApmAgent components)
  {
    // DbSpanCommon is really useful but internal.
    // We use private reflection to grab it.
    var dbSpanCommon = components.Tracer
      .GetType().GetProperty("DbSpanCommon")!
      .GetValue(components.Tracer)!;

    var startSpan = dbSpanCommon
      .GetType().GetMethod("StartSpan", BindingFlags.NonPublic | BindingFlags.Instance)!
      .CreateDelegate<Func<IApmAgent, IDbCommand, short, string?, bool, ISpan?>>(dbSpanCommon);

    var endSpan = dbSpanCommon
      .GetType().GetMethod("EndSpan", BindingFlags.NonPublic | BindingFlags.Instance)!
      .CreateDelegate<Action<ISpan, IDbCommand, Outcome, TimeSpan?>>(dbSpanCommon);

    OracleConfiguration.ActivityStart += (sender, activity) =>
    {
      ISpan? span = null;

      switch (activity.Activity)
      {
        case "ExecuteReader":
        case "ExecuteNonQuery":
          var cmd = (OracleCommand)sender!;
          span = startSpan(
            components,
            cmd,
            /*instrumentationFlag:*/ 1 << 9 /* Oracle */,
            /*subtype:*/ null,
            /*captureStack:*/ false);
          break;

        case "Read":
          var executionSegment = (IExecutionSegment?)components.Tracer.CurrentSpan ?? components.Tracer.CurrentTransaction;
          span = executionSegment?.StartSpan("DataReader fetch", ApiConstants.TypeDb, ApiConstants.SubtypeOracle, "fetch");
          break;
      }

      activity.State = span;
    };

    OracleConfiguration.ActivityEnd += (sender, activity) =>
    {
      if (activity.State is not ISpan span)
        return;

      switch (activity.Activity)
      {
        case "ExecuteNonQuery":
          span.SetLabel("affected", activity.Rows);
          goto case "ExecuteReader";

        case "ExecuteReader":
          endSpan(
            span,
            (OracleCommand)sender!,
            activity.Failed ? Outcome.Failure : Outcome.Success,
            null);
          break;

        case "Read":
          span.SetLabel("affected", activity.Rows);
          span.End();
          break;
      }
    };

    return new Unsubscriber();
  }

  class Unsubscriber : IDisposable
  {
    public void Dispose()
    {
      //We stay subscribed forever
    }
  }
}