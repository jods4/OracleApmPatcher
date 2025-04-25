using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;

if (args.Length != 1)
{
    Console.WriteLine("Usage: OraclePatcher.exe Oracle.ManagedDataAccess.dll");
    return;
}

var filename = args[0];

Console.WriteLine("Patching " + filename);
var module = ModuleDefinition.ReadModule(filename, new()
{
    ReflectionImporterProvider = new CoreLibReflectionImporterProvider("netstandard, Version=2.1.0.0"),
});

var oracleConfig = module.GetType("Oracle.ManagedDataAccess.Client.OracleConfiguration");
var eventArgs = DeclareEventArgs();
var onStart = DeclareEvent("ActivityStart");
var onEnd = DeclareEvent("ActivityEnd");

// Note that recente ODP.NET releases have true async support, which isn't instrumented here.

var command = module.GetType("Oracle.ManagedDataAccess.Client.OracleCommand");
var execReader = command.Methods.Single(t => t.Name == "ExecuteReaderInternal");
WrapWithActivity("ExecuteReader", execReader, false);
var execNonQuery = command.Methods.Single(t => t.Name == "ExecuteNonQuery");
WrapWithActivity("ExecuteNonQuery", execNonQuery, true);

var reader = module.GetType("Oracle.ManagedDataAccess.Client.OracleDataReader");
InstrumentReader(reader);

filename = filename.Replace(".dll", ".APM.dll");
module.Write(filename);
Console.WriteLine("Assembly patched: " + filename);

TypeDefinition DeclareEventArgs()
{
    var eventArgs = module.ImportReference(typeof(EventArgs));
    var eventClass = new TypeDefinition(
        "Oracle.ManagedDataAccess.Client",
        "OracleActivityEventArgs",
        TypeAttributes.Public | TypeAttributes.Class | TypeAttributes.BeforeFieldInit,
        eventArgs);
    module.Types.Add(eventClass);

    eventClass.Fields.Add(new("Activity", FieldAttributes.Public, module.TypeSystem.String));  // ExecuteReader | ExecuteNonQuery | Read
    eventClass.Fields.Add(new("Rows", FieldAttributes.Public, module.TypeSystem.Int32));       // # affected rows on ExecuteNonQuery end; # read rows on Read End
    eventClass.Fields.Add(new("Failed", FieldAttributes.Public, module.TypeSystem.Boolean));   // true if ExecuteReader or ExecuteNonQuery threw an exception
    eventClass.Fields.Add(new("State", FieldAttributes.Public, module.TypeSystem.Object));     // Available to store state between Start and End events

    var ctor = new MethodDefinition(
        ".ctor",
        MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.RTSpecialName | MethodAttributes.SpecialName,
        module.TypeSystem.Void);
    eventClass.Methods.Add(ctor);
    var il = ctor.Body.GetILProcessor();
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Call, module.ImportReference(typeof(EventArgs).GetConstructor(Array.Empty<Type>())));
    il.Emit(OpCodes.Ret);

    return eventClass;
}

MethodDefinition DeclareEvent(string name)
{
    var delegateType = module.ImportReference(typeof(EventHandler<>)).MakeGenericInstanceType(eventArgs);
    var compareExchange = new GenericInstanceMethod(
        module.ImportReference(typeof(Interlocked).GetMethods().Single(m => m.Name == "CompareExchange" && m.IsGenericMethod)))
    {
        GenericArguments = { delegateType }
    };
    var combine = module.ImportReference(typeof(Delegate).GetMethods().Single(m => m.Name == "Combine" && m.GetParameters().Length == 2));

    var field = new FieldDefinition("m_" + name, FieldAttributes.Private | FieldAttributes.Static, module.ImportReference(delegateType));
    oracleConfig.Fields.Add(field);

    var add = new MethodDefinition(
        "add_" + name,
        MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
        module.TypeSystem.Void)
    {
        Parameters = { new("value", ParameterAttributes.None, module.ImportReference(delegateType)) },
        Body = { Variables = { /* handler */ new(delegateType), /* beforeXchg */ new(delegateType), /* combined */ new(delegateType) } },
    };
    oracleConfig.Methods.Add(add);
    var il = add.Body.GetILProcessor();
    // var handler = OracleConfiguration.m_name
    il.Emit(OpCodes.Ldsfld, field);
    il.Emit(OpCodes.Stloc_0);
    // do {
    Instruction loop;
    {
        // beforeXchg = handler
        il.Append(loop = il.Create(OpCodes.Ldloc_0));
        il.Emit(OpCodes.Stloc_1);
        // combined = (EventHandler<OracleActivityEventArgs>) Delegate.Combine(beforeXchg, value)
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, combine);
        il.Emit(OpCodes.Castclass, delegateType);
        il.Emit(OpCodes.Stloc_2);
        // handler = Interlocked.CompareExchange(ref OracleConfiguration.m_name, combined, beforeXchg)
        il.Emit(OpCodes.Ldsflda, field);
        il.Emit(OpCodes.Ldloc_2);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Call, compareExchange);
        il.Emit(OpCodes.Stloc_0);
        // } while (handler != beforeXchg)
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Bne_Un_S, loop);
    }
    il.Emit(OpCodes.Ret);

    var remove = new MethodDefinition(
        "remove_" + name,
        MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.SpecialName | MethodAttributes.HideBySig,
        module.TypeSystem.Void)
    {
        Parameters = { new("value", ParameterAttributes.None, delegateType) },
        Body = { Variables = { /* handler */ new(delegateType), /* beforeXchg */ new(delegateType), /* removed */ new(delegateType) } },
    };
    oracleConfig.Methods.Add(remove);
    il = remove.Body.GetILProcessor();
    // handler = OracleConfiguration..m_name
    il.Emit(OpCodes.Ldsfld, field);
    il.Emit(OpCodes.Stloc_0);
    // do {
    {
        // beforeXchg = handler
        il.Append(loop = il.Create(OpCodes.Ldloc_0));
        il.Emit(OpCodes.Stloc_1);
        // removed = (EventHandler<OracleActivityEventArgs>) Delegate.Remove(beforeXchg, value)
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Call, module.ImportReference(typeof(Delegate).GetMethod("Remove")));
        il.Emit(OpCodes.Castclass, delegateType);
        il.Emit(OpCodes.Stloc_2);
        // handler = Interlocked.CompareExchange(ref OracleConfiguration.m_name, removed, beforeXchg)
        il.Emit(OpCodes.Ldsflda, field);
        il.Emit(OpCodes.Ldloc_2);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Call, compareExchange);
        il.Emit(OpCodes.Stloc_0);
        // } while (handler != beforeXchg)
        il.Emit(OpCodes.Ldloc_0);
        il.Emit(OpCodes.Ldloc_1);
        il.Emit(OpCodes.Bne_Un_S, loop);
    }
    il.Emit(OpCodes.Ret);

    var eventDef = new EventDefinition(name, EventAttributes.None, delegateType)
    {
        AddMethod = add,
        RemoveMethod = remove,
    };
    oracleConfig.Events.Add(eventDef);

    var raise = new MethodDefinition(
        "On" + name,
        MethodAttributes.Assembly | MethodAttributes.Static,
        module.TypeSystem.Void)
    {
        Parameters =
        {
            new("sender", ParameterAttributes.None, module.TypeSystem.Object),
            new("args", ParameterAttributes.None, eventArgs),
        },
    };
    oracleConfig.Methods.Add(raise);
    il = raise.Body.GetILProcessor();
    // if m_name != null
    il.Emit(OpCodes.Ldsfld, field);
    il.Emit(OpCodes.Dup);
    var jump = il.Create(OpCodes.Nop);
    il.Emit(OpCodes.Brtrue_S, jump);
    il.Emit(OpCodes.Pop);
    il.Emit(OpCodes.Ret);
    // m_name.Invoke(sender, args)
    il.Append(jump);
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldarg_1);
    il.Emit(OpCodes.Call, new MethodReference("Invoke", module.TypeSystem.Void, delegateType)
    {
        HasThis = true,
        Parameters =
        {
            new("sender", ParameterAttributes.None, module.TypeSystem.Object),
            new("args", ParameterAttributes.None, delegateType.ElementType.GenericParameters[0]),
        },
    });
    il.Emit(OpCodes.Ret);

    return raise;
}

void WrapWithActivity(string activityName, MethodDefinition method, bool setRows)
{
    var il = method.Body.GetILProcessor();

    // Define variables:
    //   OracleActivityEventArgs args;    // Event args for this call (only one -> shared State between start and end)
    //   ReturnType result;               // Temp storage for method result when leaving .try block
    var args = new VariableDefinition(eventArgs);
    il.Body.Variables.Add(args);

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

    // Prepend:
    int j = 0;
    var instr = il.Body.Instructions;
    // args = new OracleActivityEventArgs { Activity = activityName };
    instr.Insert(j++, il.Create(OpCodes.Newobj, eventArgs.GetConstructors().Single()));
    instr.Insert(j++, il.Create(OpCodes.Dup));
    instr.Insert(j++, il.Create(OpCodes.Ldstr, activityName));
    instr.Insert(j++, il.Create(OpCodes.Stfld, eventArgs.Fields.Single(x => x.Name == "Activity")));
    instr.Insert(j++, il.Create(OpCodes.Stloc, args));
    // OracleConfiguration.OnActivityStart(this, args)
    Instruction tryStart;
    instr.Insert(j++, tryStart = il.Create(OpCodes.Ldarg_0));
    instr.Insert(j++, il.Create(OpCodes.Ldloc, args));
    instr.Insert(j++, il.Create(OpCodes.Call, onStart));

    // Append fault block to handle errors
    // .fault
    // {
    //     args.Failed = true;
    //     OracleConfiguration.OnActivityEnd(this, args);
    // }
    var faultBlock = il.Create(OpCodes.Ldloc, args);
    il.Append(faultBlock);
    il.Emit(OpCodes.Ldc_I4_1);
    il.Emit(OpCodes.Stfld, eventArgs.Fields.Single(x => x.Name == "Failed"));
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldloc, args);
    il.Emit(OpCodes.Call, onEnd);
    il.Emit(OpCodes.Endfinally); // Endfault and Endfinally are the same opcode

    // Complete activity and return set aside result from wrapped code
    il.Append(endLabel);
    var retLabel = il.Create(OpCodes.Nop);
    //  args.Rows = result;
    if (setRows)
    {
        il.Emit(OpCodes.Ldloc, args);
        il.Emit(OpCodes.Ldloc, result);
        il.Emit(OpCodes.Stfld, eventArgs.Fields.Single(x => x.Name == "Rows"));
    }
    //   OracleConfiguration.OnActivityEnd(this, args);
    il.Emit(OpCodes.Ldarg_0);
    il.Emit(OpCodes.Ldloc, args);
    il.Emit(OpCodes.Call, onEnd);
    //   return result;
    il.Append(retLabel);
    il.Emit(OpCodes.Ldloc, result);
    il.Emit(OpCodes.Ret);

    var handler = new ExceptionHandler(ExceptionHandlerType.Fault)
    {
        TryStart = tryStart,
        TryEnd = faultBlock,
        HandlerStart = faultBlock,
        HandlerEnd = endLabel,
    };
    il.Body.ExceptionHandlers.Add(handler);
}

void InstrumentReader(TypeDefinition t)
{
    // Create fields for event activityArgs and count
    var args = new FieldDefinition("activityArgs", FieldAttributes.Private, eventArgs);
    t.Fields.Add(args);

    var rowCount = new FieldDefinition("rowCount", FieldAttributes.Private, module.TypeSystem.Int32);
    t.Fields.Add(rowCount);

    // ----------------------------------------
    // Start activity on 1st Read() call
    // ----------------------------------------
    var read = t.GetMethods().Single(m => m.Name == "Read");
    var il = read.Body.GetILProcessor();
    var instr = il.Body.Instructions;
    int i = 0;
    // if activityArgs == null
    instr.Insert(i++, il.Create(OpCodes.Ldarg_0));
    instr.Insert(i++, il.Create(OpCodes.Ldfld, args));
    instr.Insert(i, il.Create(OpCodes.Brtrue_S, instr[i++]));
    {
        // activityArgs = new OracleActivityEventArgs { Activity = "Read" };
        instr.Insert(i++, il.Create(OpCodes.Ldarg_0));
        instr.Insert(i++, il.Create(OpCodes.Newobj, eventArgs.GetConstructors().Single()));
        instr.Insert(i++, il.Create(OpCodes.Dup));
        instr.Insert(i++, il.Create(OpCodes.Ldstr, "Read"));
        instr.Insert(i++, il.Create(OpCodes.Stfld, eventArgs.Fields.Single(x => x.Name == "Activity")));
        instr.Insert(i++, il.Create(OpCodes.Stfld, args));
        // OracleConfiguration.OnActivityStart(this, activityArgs);
        instr.Insert(i++, il.Create(OpCodes.Ldarg_0));
        instr.Insert(i++, il.Create(OpCodes.Dup));
        instr.Insert(i++, il.Create(OpCodes.Ldfld, args));
        instr.Insert(i++, il.Create(OpCodes.Call, onStart));
    }

    // ----------------------------------------
    // Increment rowCount in .Read() calls
    // ----------------------------------------
    for (; i < instr.Count; ++i)
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

    // ----------------------------------------
    // Complete activity in Close()
    // ----------------------------------------
    var close = t.GetMethods().Single(m => m.Name == "Close");
    il = close.Body.GetILProcessor();
    instr = il.Body.Instructions;
    i = 0;
    // if activityArgs != null
    instr.Insert(i++, il.Create(OpCodes.Ldarg_0));
    instr.Insert(i++, il.Create(OpCodes.Ldfld, args));
    instr.Insert(i, il.Create(OpCodes.Brfalse_S, instr[i++]));
    {
        //   activityArgs.Rows = rowCount;
        instr.Insert(i++, il.Create(OpCodes.Ldarg_0));
        instr.Insert(i++, il.Create(OpCodes.Ldfld, args));
        instr.Insert(i++, il.Create(OpCodes.Ldarg_0));
        instr.Insert(i++, il.Create(OpCodes.Ldfld, rowCount));
        instr.Insert(i++, il.Create(OpCodes.Stfld, eventArgs.Fields.Single(x => x.Name == "Rows")));
        //   OracleConfiguration.OnActivityEnd(this, activityArgs);
        instr.Insert(i++, il.Create(OpCodes.Ldarg_0));
        instr.Insert(i++, il.Create(OpCodes.Dup));
        instr.Insert(i++, il.Create(OpCodes.Ldfld, args));
        instr.Insert(i++, il.Create(OpCodes.Call, onEnd));
        // activityArgs = null;
        instr.Insert(i++, il.Create(OpCodes.Ldarg_0));
        instr.Insert(i++, il.Create(OpCodes.Ldnull));
        instr.Insert(i++, il.Create(OpCodes.Stfld, args));
    }
}