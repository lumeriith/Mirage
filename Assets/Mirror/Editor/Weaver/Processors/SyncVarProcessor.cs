using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Mirror.Weaver
{
    /// <summary>
    /// Processes [SyncVar] in NetworkBehaviour
    /// </summary>
    public static class SyncVarProcessor
    {
        // ulong = 64 bytes
        const int SyncVarLimit = 64;


        static string HookParameterMessage(string hookName, TypeReference ValueType)
            => string.Format("void {0}({1} oldValue, {1} newValue)", hookName, ValueType);

        // Get hook method if any
        public static MethodDefinition GetHookMethod(FieldDefinition syncVar)
        {
            CustomAttribute syncVarAttr = syncVar.GetCustomAttribute<SyncVarAttribute>();

            if (syncVarAttr == null)
                return null;

            string hookFunctionName = syncVarAttr.GetField<string>("hook", null);

            if (hookFunctionName == null)
                return null;

            return FindHookMethod(syncVar, hookFunctionName);
        }

        static MethodDefinition FindHookMethod(FieldDefinition syncVar, string hookFunctionName)
        {
            List<MethodDefinition> methods = syncVar.DeclaringType.GetMethods(hookFunctionName);

            var methodsWith2Param = new List<MethodDefinition>(methods.Where(m => m.Parameters.Count == 2));

            if (methodsWith2Param.Count == 0)
            {
                Weaver.Error($"Could not find hook for '{syncVar.Name}', hook name '{hookFunctionName}'. " +
                    $"Method signature should be {HookParameterMessage(hookFunctionName, syncVar.FieldType)}",
                    syncVar);

                return null;
            }

            foreach (MethodDefinition method in methodsWith2Param)
            {
                if (MatchesParameters(syncVar, method))
                {
                    return method;
                }
            }

            Weaver.Error($"Wrong type for Parameter in hook for '{syncVar.Name}', hook name '{hookFunctionName}'. " +
                     $"Method signature should be {HookParameterMessage(hookFunctionName, syncVar.FieldType)}",
                   syncVar);

            return null;
        }

        static bool MatchesParameters(FieldDefinition syncVar, MethodDefinition method)
        {
            // matches void onValueChange(T oldValue, T newValue)
            return method.Parameters[0].ParameterType.FullName == syncVar.FieldType.FullName &&
                   method.Parameters[1].ParameterType.FullName == syncVar.FieldType.FullName;
        }

        public static MethodDefinition GenerateSyncVarGetter(FieldDefinition fd, string originalName, FieldDefinition netFieldId)
        {
            //Create the get method
            var get = new MethodDefinition(
                    "get_Network" + originalName, MethodAttributes.Public |
                    MethodAttributes.SpecialName |
                    MethodAttributes.HideBySig,
                    fd.FieldType);

            ILProcessor worker = get.Body.GetILProcessor();

            // [SyncVar] NetworkIdentity?
            if (fd.FieldType.Is<NetworkIdentity>())
            {
                // return this.GetSyncVarNetworkIdentity(ref field, uint netId);
                // this.
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldfld, netFieldId));
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldflda, fd));
                worker.Append(worker.Create(OpCodes.Call, WeaverTypes.getSyncVarNetworkIdentityReference));
                worker.Append(worker.Create(OpCodes.Ret));
            }
            // [SyncVar] int, string, etc.
            else
            {
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldfld, fd));
                worker.Append(worker.Create(OpCodes.Ret));
            }

            get.Body.Variables.Add(new VariableDefinition(fd.FieldType));
            get.Body.InitLocals = true;
            get.SemanticsAttributes = MethodSemanticsAttributes.Getter;

            get.DeclaringType = fd.DeclaringType;
            return get;
        }

        public static MethodDefinition GenerateSyncVarSetter(FieldDefinition fd, string originalName, long dirtyBit, FieldDefinition netFieldId)
        {
            //Create the set method
            var set = new MethodDefinition("set_Network" + originalName, MethodAttributes.Public |
                    MethodAttributes.SpecialName |
                    MethodAttributes.HideBySig,
                    WeaverTypes.Import(typeof(void)));
            var valueParam = new ParameterDefinition("value", ParameterAttributes.In, fd.FieldType);
            set.Parameters.Add(valueParam);
            set.SemanticsAttributes = MethodSemanticsAttributes.Setter;
            set.DeclaringType = fd.DeclaringType;

            ILProcessor worker = set.Body.GetILProcessor();

            // if (!SyncVarEqual(value, ref playerData))
            Instruction endOfMethod = worker.Create(OpCodes.Nop);

            // this
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            // new value to set
            worker.Append(worker.Create(OpCodes.Ldarg, valueParam));
            // reference to field to set
            // make generic version of SetSyncVar with field type
            if (fd.FieldType.Is<NetworkIdentity>())
            {
                // reference to netId Field to set
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldfld, netFieldId));

                worker.Append(worker.Create(OpCodes.Call, WeaverTypes.syncVarNetworkIdentityEqualReference));
            }
            else
            {
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldflda, fd));

                var syncVarEqualGm = new GenericInstanceMethod(WeaverTypes.syncVarEqualReference);
                syncVarEqualGm.GenericArguments.Add(fd.FieldType);
                worker.Append(worker.Create(OpCodes.Call, syncVarEqualGm));
            }

            worker.Append(worker.Create(OpCodes.Brtrue, endOfMethod));

            // T oldValue = value;
            var oldValue = new VariableDefinition(fd.FieldType);
            set.Body.Variables.Add(oldValue);
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            worker.Append(worker.Create(OpCodes.Ldfld, fd));
            worker.Append(worker.Create(OpCodes.Stloc, oldValue));

            // this
            worker.Append(worker.Create(OpCodes.Ldarg_0));

            // new value to set
            worker.Append(worker.Create(OpCodes.Ldarg, valueParam));

            // reference to field to set
            worker.Append(worker.Create(OpCodes.Ldarg_0));
            worker.Append(worker.Create(OpCodes.Ldflda, fd));

            // dirty bit
            // 8 byte integer aka long
            worker.Append(worker.Create(OpCodes.Ldc_I8, dirtyBit));

            if (fd.FieldType.Is<NetworkIdentity>())
            {
                // reference to netId Field to set
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldflda, netFieldId));

                worker.Append(worker.Create(OpCodes.Call, WeaverTypes.setSyncVarNetworkIdentityReference));
            }
            else
            {
                // make generic version of SetSyncVar with field type
                var gm = new GenericInstanceMethod(WeaverTypes.setSyncVarReference);
                gm.GenericArguments.Add(fd.FieldType);

                // invoke SetSyncVar
                worker.Append(worker.Create(OpCodes.Call, gm));
            }

            MethodDefinition hookMethod = GetHookMethod(fd);

            if (hookMethod != null)
            {
                //if (base.isLocalClient && !getSyncVarHookGuard(dirtyBit))
                Instruction label = worker.Create(OpCodes.Nop);
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Call, WeaverTypes.NetworkBehaviourIsLocalClient));
                worker.Append(worker.Create(OpCodes.Brfalse, label));
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldc_I8, dirtyBit));
                worker.Append(worker.Create(OpCodes.Call, WeaverTypes.getSyncVarHookGuard));
                worker.Append(worker.Create(OpCodes.Brtrue, label));

                // setSyncVarHookGuard(dirtyBit, true);
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldc_I8, dirtyBit));
                worker.Append(worker.Create(OpCodes.Ldc_I4_1));
                worker.Append(worker.Create(OpCodes.Call, WeaverTypes.setSyncVarHookGuard));

                // call hook (oldValue, newValue)
                // Generates: OnValueChanged(oldValue, value);
                WriteCallHookMethodUsingArgument(worker, hookMethod, oldValue);

                // setSyncVarHookGuard(dirtyBit, false);
                worker.Append(worker.Create(OpCodes.Ldarg_0));
                worker.Append(worker.Create(OpCodes.Ldc_I8, dirtyBit));
                worker.Append(worker.Create(OpCodes.Ldc_I4_0));
                worker.Append(worker.Create(OpCodes.Call, WeaverTypes.setSyncVarHookGuard));

                worker.Append(label);
            }

            worker.Append(endOfMethod);

            worker.Append(worker.Create(OpCodes.Ret));


            return set;
        }

        public static void ProcessSyncVar(FieldDefinition fd, Dictionary<FieldDefinition, FieldDefinition> syncVarNetIds, long dirtyBit)
        {
            string originalName = fd.Name;
            Weaver.DLog(fd.DeclaringType, "Sync Var " + fd.Name + " " + fd.FieldType);

            // NetworkIdentity SyncVars have a new field for netId
            FieldDefinition netIdField = null;
            if (fd.FieldType.Is<NetworkIdentity>())
            {
                netIdField = new FieldDefinition("___" + fd.Name + "NetId",
                    FieldAttributes.Private,
                    WeaverTypes.Import<uint>());

                syncVarNetIds[fd] = netIdField;
            }

            MethodDefinition get = GenerateSyncVarGetter(fd, originalName, netIdField);
            MethodDefinition set = GenerateSyncVarSetter(fd, originalName, dirtyBit, netIdField);

            //NOTE: is property even needed? Could just use a setter function?
            //create the property
            var propertyDefinition = new PropertyDefinition("Network" + originalName, PropertyAttributes.None, fd.FieldType)
            {
                GetMethod = get,
                SetMethod = set
            };

            propertyDefinition.DeclaringType = fd.DeclaringType;
            //add the methods and property to the type.
            fd.DeclaringType.Methods.Add(get);
            fd.DeclaringType.Methods.Add(set);
            fd.DeclaringType.Properties.Add(propertyDefinition);
            Weaver.WeaveLists.replacementSetterProperties[fd] = set;

            // replace getter field if NetworkIdentity so it uses
            // netId instead
            // -> only for GameObjects, otherwise an int syncvar's getter would
            //    end up in recursion.
            if (fd.FieldType.Is<NetworkIdentity>())
            {
                Weaver.WeaveLists.replacementGetterProperties[fd] = get;
            }
        }

        public static (List<FieldDefinition> syncVars, Dictionary<FieldDefinition, FieldDefinition> syncVarNetIds) ProcessSyncVars(TypeDefinition td)
        {
            var syncVars = new List<FieldDefinition>();
            var syncVarNetIds = new Dictionary<FieldDefinition, FieldDefinition>();

            // the mapping of dirtybits to sync-vars is implicit in the order of the fields here. this order is recorded in m_replacementProperties.
            // start assigning syncvars at the place the base class stopped, if any
            int dirtyBitCounter = Weaver.WeaveLists.GetSyncVarStart(td.BaseType.FullName);

            // find syncvars
            foreach (FieldDefinition fd in td.Fields)
            {
                if (!fd.HasCustomAttribute<SyncVarAttribute>())
                {
                    continue;
                }

                if ((fd.Attributes & FieldAttributes.Static) != 0)
                {
                    Weaver.Error($"{fd.Name} cannot be static", fd);
                    continue;
                }

                if (fd.FieldType.IsArray)
                {
                    Weaver.Error($"{fd.Name} has invalid type. Use SyncLists instead of arrays", fd);
                    continue;
                }

                if (SyncObjectInitializer.ImplementsSyncObject(fd.FieldType))
                {
                    Weaver.Warning($"{fd.Name} has [SyncVar] attribute. SyncLists should not be marked with SyncVar", fd);
                    continue;
                }
                syncVars.Add(fd);

                ProcessSyncVar(fd, syncVarNetIds, 1L << dirtyBitCounter);
                dirtyBitCounter += 1;
            }

            if (dirtyBitCounter >= SyncVarLimit)
            {
                Weaver.Error($"{td.Name} has too many SyncVars. Consider refactoring your class into multiple components", td);
            }

            // add all the new SyncVar __netId fields
            foreach (FieldDefinition fd in syncVarNetIds.Values)
            {
                td.Fields.Add(fd);
            }
            Weaver.WeaveLists.SetNumSyncVars(td.FullName, syncVars.Count);

            return (syncVars, syncVarNetIds);
        }

        public static void WriteCallHookMethodUsingArgument(ILProcessor worker, MethodDefinition hookMethod, VariableDefinition oldValue)
        {
            WriteCallHookMethod(worker, hookMethod, oldValue, null);
        }

        public static void WriteCallHookMethodUsingField(ILProcessor worker, MethodDefinition hookMethod, VariableDefinition oldValue, FieldDefinition newValue)
        {
            if (newValue == null)
            {
                Weaver.Error("NewValue field was null when writing SyncVar hook");
            }

            WriteCallHookMethod(worker, hookMethod, oldValue, newValue);
        }

        static void WriteCallHookMethod(ILProcessor worker, MethodDefinition hookMethod, VariableDefinition oldValue, FieldDefinition newValue)
        {
            WriteStartFunctionCall();

            // write args
            WriteOldValue();
            WriteNewValue();

            WriteEndFunctionCall();


            // *** Local functions used to write OpCodes ***
            // Local functions have access to function variables, no need to pass in args

            void WriteOldValue()
            {
                worker.Append(worker.Create(OpCodes.Ldloc, oldValue));
            }

            void WriteNewValue()
            {
                // write arg1 or this.field
                if (newValue == null)
                {
                    worker.Append(worker.Create(OpCodes.Ldarg_1));
                }
                else
                {
                    // this.
                    worker.Append(worker.Create(OpCodes.Ldarg_0));
                    // syncvar.get
                    worker.Append(worker.Create(OpCodes.Ldfld, newValue));
                }
            }

            // Writes this before method if it is not static
            void WriteStartFunctionCall()
            {
                // dont add this (Ldarg_0) if method is static
                if (!hookMethod.IsStatic)
                {
                    // this before method call
                    // eg this.onValueChanged
                    worker.Append(worker.Create(OpCodes.Ldarg_0));
                }
            }

            // Calls method
            void WriteEndFunctionCall()
            {
                // only use Callvirt when not static
                OpCode opcode = hookMethod.IsStatic ? OpCodes.Call : OpCodes.Callvirt;
                worker.Append(worker.Create(opcode, hookMethod));
            }
        }
    }
}
