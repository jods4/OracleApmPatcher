using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using System.Diagnostics;

if (args.Length != 1)
{
  Console.WriteLine("Usage: OraclePatcher.exe Oracle.ManagedDataAccess.dll");
  return;
}

var filename = args[0];

Console.WriteLine("Patching " + filename);
var module = ModuleDefinition.ReadModule(filename);

var command = module.Types.Single(t => t.Name == "OracleCommand");
var activitySource = AddActivitySource("Oracle.ManagedDataAccess.Client.OracleCommand", command, module);
var execReader = command.Methods.Single(t => t.Name == "ExecuteReader" && t.IsAssembly);
WrapWithActivity("ExecuteReader", execReader, activitySource, module);
var execNonQuery = command.Methods.Single(t => t.Name == "ExecuteNonQuery");
WrapWithActivity("ExecuteNonQuery", execNonQuery, activitySource, module, "affected");

var reader = module.Types.Single(t => t.Name == "OracleDataReader");
activitySource = AddActivitySource("Oracle.ManagedDataAccess.Client.OracleDataReader", reader, module);
InstrumentReader(reader, activitySource, module);

filename = filename.Replace(".dll", ".APM.dll");
module.Write(filename);
Console.WriteLine("Assembly patched: " + filename);

static FieldDefinition AddActivitySource(string sourceName, TypeDefinition t, ModuleDefinition mod)
{
  // Declare field
  // private static ActivitySource activitySource;
  var activitySource = new FieldDefinition(
    "activitySource",
    FieldAttributes.Static | FieldAttributes.Private,
    mod.ImportReference(typeof(ActivitySource)));
  t.Fields.Add(activitySource);

  // Find static ctor for initialization
  var cctor = t.GetStaticConstructor();
  if (cctor == null)
  {
    cctor = new MethodDefinition(
      ".cctor",
      MethodAttributes.Private | MethodAttributes.Static | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
      mod.TypeSystem.Void);
    t.Methods.Add(cctor);
    cctor.Body.Instructions.Add(Instruction.Create(OpCodes.Ret));
  }

  // Init field
  // activitySource = new ActivitySource("Oracle.ManagedDataAccess.Client.OracleCommand", version: "");
  var activitySourceCtor = mod.ImportReference(typeof(ActivitySource).GetConstructor(new[] { typeof(string), typeof(string) }));
  var il = cctor.Body.GetILProcessor();
  il.Body.Instructions.Insert(0, il.Create(OpCodes.Ldstr, sourceName));
  il.Body.Instructions.Insert(1, il.Create(OpCodes.Ldstr, ""));
  il.Body.Instructions.Insert(2, il.Create(OpCodes.Newobj, activitySourceCtor));
  il.Body.Instructions.Insert(3, il.Create(OpCodes.Stsfld, activitySource));

  return activitySource;
}

static void WrapWithActivity(string activityName, MethodDefinition method, FieldDefinition activitySource, ModuleDefinition mod, string? resultTag = null)
{
  var il = method.Body.GetILProcessor();

  // Define variables:
  //   Activity? activity;      // Activity that wraps this method
  //   ReturnType result;       // Temp storage for method result when leaving .try block
  var activity = new VariableDefinition(mod.ImportReference(typeof(Activity)));
  il.Body.Variables.Add(activity);

  var result = new VariableDefinition(method.ReturnType);
  il.Body.Variables.Add(result);

  // Create final instruction that can be jumped to by `leave` instructions
  var endLabel = il.Create(OpCodes.Nop);

  // Replace all `ret` instructions because they are forbidden inside a `try` block.
  //   stloc result   // Put the return value aside (stack is emptied by leave)
  //   leave endLabel // Leave the try block and jump to final instruction.
  for (int i = 0; i < il.Body.Instructions.Count; i++)
  {
    if (il.Body.Instructions[i].OpCode == OpCodes.Ret)
    {
      // Don't replace the `ret` instruction with a new one, mutate instead.
      // This is important because `ret` might be the target of some jumps in original code.
      il.Body.Instructions[i].OpCode = OpCodes.Stloc;
      il.Body.Instructions[i].Operand = result;
      il.Body.Instructions.Insert(++i, il.Create(OpCodes.Leave, endLabel));
    }
  }

  // Prepend activity initialisation
  //   activity = activitySource
  //      .CreateActivity(activityName, ActivityKind.Internal)
  //      ?.SetTag("cmd", this)
  //      .Start();
  var store = il.Create(OpCodes.Stloc, activity);
  il.Body.Instructions.Insert(0, il.Create(OpCodes.Ldsfld, activitySource));
  il.Body.Instructions.Insert(1, il.Create(OpCodes.Ldstr, activityName));
  il.Body.Instructions.Insert(2, il.Create(OpCodes.Ldc_I4_0)); // ActivityKind.Internal
  il.Body.Instructions.Insert(3, il.Create(OpCodes.Call,
    mod.ImportReference(typeof(ActivitySource).GetMethod("CreateActivity", new[] { typeof(string), typeof(ActivityKind) }))));
  il.Body.Instructions.Insert(4, il.Create(OpCodes.Dup));
  il.Body.Instructions.Insert(5, il.Create(OpCodes.Brfalse_S, store));
  il.Body.Instructions.Insert(6, il.Create(OpCodes.Ldstr, "cmd"));
  il.Body.Instructions.Insert(7, il.Create(OpCodes.Ldarg_0));
  il.Body.Instructions.Insert(8, il.Create(OpCodes.Call,
    mod.ImportReference(typeof(Activity).GetMethod("SetTag", new[] { typeof(string), typeof(object) }))));
  il.Body.Instructions.Insert(9, il.Create(OpCodes.Call,
    mod.ImportReference(typeof(Activity).GetMethod("Start"))));
  il.Body.Instructions.Insert(10, store);

  // Append fault block to handle errors
  // .fault
  // {
  //     activity
  //       ?.SetTag("otel.status_code", "ERROR")
  //        .Dispose();
  // }
  var endFault = il.Create(OpCodes.Endfinally); // Endfault and Endfinally are the same opcode
  var faultBlock = il.Create(OpCodes.Ldloc, activity);
  il.Append(faultBlock);
  il.Emit(OpCodes.Brfalse_S, endFault);
  il.Emit(OpCodes.Ldloc, activity);
  il.Emit(OpCodes.Ldstr, "otel.status_code");
  il.Emit(OpCodes.Ldstr, "ERROR");
  il.Emit(OpCodes.Call,
    mod.ImportReference(typeof(Activity).GetMethod("SetTag", new[] { typeof(string), typeof(object) })));
  il.Emit(OpCodes.Callvirt,
    mod.ImportReference(typeof(Activity).GetMethod("Dispose")));
  il.Append(endFault);

  // Complete activity and return set aside result from wrapped code
  il.Append(endLabel);
  var retLabel = il.Create(OpCodes.Nop);
  //   activity?.SetTag(resultTag, result)
  il.Emit(OpCodes.Ldloc, activity);
  il.Emit(OpCodes.Brfalse_S, retLabel);
  il.Emit(OpCodes.Ldloc, activity);
  if (resultTag != null)
  {
    il.Emit(OpCodes.Ldstr, resultTag);
    il.Emit(OpCodes.Ldloc, result);
    if (result.VariableType.IsValueType)
      il.Emit(OpCodes.Box, result.VariableType);
    il.Emit(OpCodes.Call,
      mod.ImportReference(typeof(Activity).GetMethod("SetTag", new[] { typeof(string), typeof(object) })));
  }
  //   activity?.Dispose();
  il.Emit(OpCodes.Callvirt,
    mod.ImportReference(typeof(Activity).GetMethod("Dispose")));
  //   return result;
  il.Append(retLabel);
  il.Emit(OpCodes.Ldloc, result);
  il.Emit(OpCodes.Ret);

  var handler = new ExceptionHandler(ExceptionHandlerType.Fault)
  {
    TryStart = il.Body.Instructions[0],
    TryEnd = faultBlock,
    HandlerStart = faultBlock,
    HandlerEnd = endLabel,
  };
  il.Body.ExceptionHandlers.Add(handler);
}

static void InstrumentReader(TypeDefinition t, FieldReference activitySource, ModuleDefinition mod)
{
  // Create fields for activity and count
  var activity = new FieldDefinition("activity", FieldAttributes.Private, mod.ImportReference(typeof(Activity)));
  t.Fields.Add(activity);

  var rowCount = new FieldDefinition("rowCount", FieldAttributes.Private, mod.TypeSystem.Int32);
  t.Fields.Add(rowCount);

  // ----------------------------------------
  // Instantiate activity in ctor
  // ----------------------------------------
  var ctor = t.GetConstructors().Single(c => !c.IsStatic);
  // Find the single `ret` instruction (if there's only one, it has to be the last instruction) and insert
  //   activity = activitySource.StartActivity("FetchData", ActivityKind.Internal);
  var il = ctor.Body.GetILProcessor();
  var ret = ctor.Body.Instructions.Single(i => i.OpCode == OpCodes.Ret);
  ret.OpCode = OpCodes.Ldarg_0;
  il.Emit(OpCodes.Ldsfld, activitySource);
  il.Emit(OpCodes.Ldstr, "FetchData");
  il.Emit(OpCodes.Ldc_I4_0);  // ActivityKind.Internal
  il.Emit(OpCodes.Call,
    mod.ImportReference(typeof(ActivitySource).GetMethod("StartActivity", new[] { typeof(string), typeof(ActivityKind) })));
  il.Emit(OpCodes.Stfld, activity);
  il.Emit(OpCodes.Ret);

  // ----------------------------------------
  // Complete activity in Close()
  // ----------------------------------------
  var dispose = t.GetMethods().Single(m => m.Name == "Close");
  il = dispose.Body.GetILProcessor();
  int i = 0;
  //   activity?.SetTag("rows", rowCount).Dispose()
  il.Body.Instructions.Insert(i++, il.Create(OpCodes.Ldarg_0));
  il.Body.Instructions.Insert(i++, il.Create(OpCodes.Ldfld, activity));
  il.Body.Instructions.Insert(i, il.Create(OpCodes.Brfalse_S, il.Body.Instructions[i++]));
  il.Body.Instructions.Insert(i++, il.Create(OpCodes.Ldarg_0));
  il.Body.Instructions.Insert(i++, il.Create(OpCodes.Ldfld, activity));
  il.Body.Instructions.Insert(i++, il.Create(OpCodes.Ldstr, "rows"));
  il.Body.Instructions.Insert(i++, il.Create(OpCodes.Ldarg_0));
  il.Body.Instructions.Insert(i++, il.Create(OpCodes.Ldfld, rowCount));
  il.Body.Instructions.Insert(i++, il.Create(OpCodes.Box, rowCount.FieldType));
  il.Body.Instructions.Insert(i++, il.Create(OpCodes.Call,
    mod.ImportReference(typeof(Activity).GetMethod("SetTag", new[] { typeof(string), typeof(object) }))));
  il.Body.Instructions.Insert(i++, il.Create(OpCodes.Callvirt,
    mod.ImportReference(typeof(Activity).GetMethod("Dispose"))));
  il.Body.Instructions.Insert(i++, il.Create(OpCodes.Ldarg_0));
  il.Body.Instructions.Insert(i++, il.Create(OpCodes.Ldnull));
  il.Body.Instructions.Insert(i++, il.Create(OpCodes.Stfld, activity));

  // ----------------------------------------
  // Increment rowCount in .Read() calls
  // ----------------------------------------
  var read = t.GetMethods().Single(m => m.Name == "Read");
  il = read.Body.GetILProcessor();
  var instr = read.Body.Instructions;
  for (i = 0; i < instr.Count; i++)
  {
    if (instr[i].OpCode == OpCodes.Ret)
    {
      // return false
      if (instr[i - 1].OpCode == OpCodes.Ldc_I4_0) continue;
      if (instr[i - 1].OpCode != OpCodes.Ldc_I4_1)
      {
        // Dynamic case, not `return true`. Let's make a quick check.
        instr.Insert(i++, il.Create(OpCodes.Dup));
        instr.Insert(i, il.Create(OpCodes.Brfalse_S, instr[i++]));
      }
      // rowCount++;
      instr.Insert(i++, il.Create(OpCodes.Ldarg_0));
      instr.Insert(i++, il.Create(OpCodes.Ldarg_0));
      instr.Insert(i++, il.Create(OpCodes.Ldfld, rowCount));
      instr.Insert(i++, il.Create(OpCodes.Ldc_I4_1));
      instr.Insert(i++, il.Create(OpCodes.Add));
      instr.Insert(i++, il.Create(OpCodes.Stfld, rowCount));
    }
  }
}