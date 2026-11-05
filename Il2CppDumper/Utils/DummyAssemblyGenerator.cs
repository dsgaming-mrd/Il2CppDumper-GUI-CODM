using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Il2CppDumper
{
    public class DummyAssemblyGenerator
    {
        public List<AssemblyDefinition> Assemblies = new();

        private readonly Il2CppExecutor executor;
        private readonly Metadata metadata;
        private readonly Il2Cpp il2Cpp;
        private readonly Dictionary<Il2CppTypeDefinition, TypeDefinition> typeDefinitionDic = new();
        private readonly Dictionary<Il2CppGenericParameter, GenericParameter> genericParameterDic = new();
        private readonly MethodDefinition attributeAttribute;
        private readonly TypeReference stringType;
        private readonly TypeSystem typeSystem;
        private readonly Dictionary<int, FieldDefinition> fieldDefinitionDic = new();
        private readonly Dictionary<int, PropertyDefinition> propertyDefinitionDic = new();
        private readonly Dictionary<int, MethodDefinition> methodDefinitionDic = new();

        public DummyAssemblyGenerator(Il2CppExecutor il2CppExecutor, bool addToken)
        {
            executor = il2CppExecutor;
            metadata = il2CppExecutor.metadata;
            il2Cpp = il2CppExecutor.il2Cpp;

            //Il2CppDummyDll
            var il2CppDummyDll = AssemblyDefinition.ReadAssembly(new MemoryStream(Resource1.Il2CppDummyDll));
            Assemblies.Add(il2CppDummyDll);
            var dummyMD = il2CppDummyDll.MainModule;
            var addressAttribute = dummyMD.Types.First(x => x.Name == "AddressAttribute").Methods[0];
            var fieldOffsetAttribute = dummyMD.Types.First(x => x.Name == "FieldOffsetAttribute").Methods[0];
            attributeAttribute = dummyMD.Types.First(x => x.Name == "AttributeAttribute").Methods[0];
            var metadataOffsetAttribute = dummyMD.Types.First(x => x.Name == "MetadataOffsetAttribute").Methods[0];
            var tokenAttribute = dummyMD.Types.First(x => x.Name == "TokenAttribute").Methods[0];
            stringType = dummyMD.TypeSystem.String;
            typeSystem = dummyMD.TypeSystem;

            var resolver = new MyAssemblyResolver();
            var moduleParameters = new ModuleParameters
            {
                Kind = ModuleKind.Dll,
                AssemblyResolver = resolver
            };
            resolver.Register(il2CppDummyDll);

            var parameterDefinitionDic = new Dictionary<int, ParameterDefinition>();
            var eventDefinitionDic = new Dictionary<int, EventDefinition>();

            Console.WriteLine("[DummyGen] Pass 1: Creating assemblies and types...");
            foreach (var imageDef in metadata.imageDefs)
            {
                var imageName = metadata.GetStringFromIndex(imageDef.nameIndex);
                var aname = metadata.assemblyDefs[imageDef.assemblyIndex].aname;
                var assemblyName = metadata.GetStringFromIndex(aname.nameIndex);
                Version vers;
                if (aname.build >= 0)
                {
                    vers = new Version(aname.major, aname.minor, aname.build, aname.revision);
                }
                else
                {
                    //__Generated
                    vers = new Version(3, 7, 1, 6);
                }
                var assemblyNameDef = new AssemblyNameDefinition(assemblyName, vers);
                var assemblyDefinition = AssemblyDefinition.CreateAssembly(assemblyNameDef, imageName, moduleParameters);
                resolver.Register(assemblyDefinition);
                Assemblies.Add(assemblyDefinition);
                var moduleDefinition = assemblyDefinition.MainModule;
                moduleDefinition.Types.Clear();//清除自动创建的<Module>类
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (var index = imageDef.typeStart; index < typeEnd; ++index)
                {
                    var typeDef = metadata.typeDefs[index];
                    var namespaceName = metadata.GetStringFromIndex(typeDef.namespaceIndex);
                    var typeName = metadata.GetStringFromIndex(typeDef.nameIndex);
                    var typeDefinition = new TypeDefinition(namespaceName, typeName, (TypeAttributes)typeDef.flags);
                    typeDefinitionDic.Add(typeDef, typeDefinition);
                    if (typeDef.declaringTypeIndex == -1)
                    {
                        moduleDefinition.Types.Add(typeDefinition);
                    }
                }
            }
            Console.WriteLine("[DummyGen] Pass 2: Nested types...");
            foreach (var imageDef in metadata.imageDefs)
            {
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (var index = imageDef.typeStart; index < typeEnd; ++index)
                {
                    var typeDef = metadata.typeDefs[index];
                    var typeDefinition = typeDefinitionDic[typeDef];

                    //nestedtype
                    for (int i = 0; i < typeDef.nested_type_count; i++)
                    {
                        int nPos = typeDef.nestedTypesStart + i;
                        if (nPos < 0 || nPos >= metadata.nestedTypeIndices.Length) continue;
                        var nestedIndex = metadata.nestedTypeIndices[nPos];
                        if (nestedIndex < 0 || nestedIndex >= metadata.typeDefs.Length) continue;
                        var nestedTypeDef = metadata.typeDefs[nestedIndex];
                        if (typeDefinitionDic.TryGetValue(nestedTypeDef, out var nestedTypeDefinition))
                            typeDefinition.NestedTypes.Add(nestedTypeDefinition);
                    }
                }
            }

            foreach (var imageDef in metadata.imageDefs)
            {
                var aname = metadata.assemblyDefs[imageDef.assemblyIndex].aname;
                var assemblyName = metadata.GetStringFromIndex(aname.nameIndex);
                var assembly = Assemblies.FirstOrDefault(a => a.Name.Name == assemblyName);
                if (assembly == null) continue;
                var moduleDefinition = assembly.MainModule;

                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (var index = imageDef.typeStart; index < typeEnd; ++index)
                {
                    if (index < 0 || index >= metadata.typeDefs.Length) continue;
                    var typeDef = metadata.typeDefs[index];
                    if (typeDefinitionDic.TryGetValue(typeDef, out var typeDefinition))
                    {
                        if (typeDefinition.Module == null && typeDefinition.DeclaringType == null)
                        {
                            moduleDefinition.Types.Add(typeDefinition);
                        }
                    }
                }
            }

            Console.WriteLine("[DummyGen] Pass 3: Generics, base type, interfaces...");
            foreach (var imageDef in metadata.imageDefs)
            {
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (var index = imageDef.typeStart; index < typeEnd; ++index)
                {
                    if (index < 0 || index >= metadata.typeDefs.Length) continue;
                    var typeDef = metadata.typeDefs[index];
                    if (!typeDefinitionDic.TryGetValue(typeDef, out var typeDefinition) || typeDefinition == null)
                        continue;

                    var mod = typeDefinition.Module ?? typeDefinition.DeclaringType?.Module;
                    if (mod == null)
                        continue;

                    if (addToken && tokenAttribute != null)
                    {
                        try
                        {
                            var customTokenAttribute = new CustomAttribute(mod.ImportReference(tokenAttribute));
                            typeDefinition.CustomAttributes.Add(customTokenAttribute);
                        }
                        catch { }
                    }

                    //genericParameter
                    if (typeDef.genericContainerIndex >= 0 && typeDef.genericContainerIndex < metadata.genericContainers.Length)
                    {
                        var genericContainer = metadata.genericContainers[typeDef.genericContainerIndex];
                        for (int i = 0; i < genericContainer.type_argc; i++)
                        {
                            var genericParameterIndex = genericContainer.genericParameterStart + i;
                            if (genericParameterIndex >= 0 && genericParameterIndex < metadata.genericParameters.Length)
                            {
                                var param = metadata.genericParameters[genericParameterIndex];
                                var genericParameter = CreateGenericParameter(param, typeDefinition);
                                if (genericParameter != null)
                                    typeDefinition.GenericParameters.Add(genericParameter);
                            }
                        }
                    }

                    //parent
                    if (typeDef.parentIndex >= 0 && typeDef.parentIndex < il2Cpp.types.Length)
                    {
                        var parentType = il2Cpp.types[typeDef.parentIndex];
                        var parentTypeRef = GetTypeReference(typeDefinition, parentType);
                        if (parentTypeRef != null)
                            typeDefinition.BaseType = parentTypeRef;
                    }

                    //interfaces
                    for (int i = 0; i < typeDef.interfaces_count; i++)
                    {
                        int ifPos = typeDef.interfacesStart + i;
                        if (ifPos >= 0 && ifPos < metadata.interfaceIndices.Length)
                        {
                            int ifTypeIdx = metadata.interfaceIndices[ifPos];
                            if (ifTypeIdx >= 0 && ifTypeIdx < il2Cpp.types.Length)
                            {
                                var interfaceType = il2Cpp.types[ifTypeIdx];
                                var interfaceTypeRef = GetTypeReference(typeDefinition, interfaceType);
                                if (interfaceTypeRef != null)
                                    typeDefinition.Interfaces.Add(new InterfaceImplementation(interfaceTypeRef));
                            }
                        }
                    }
                }
            }
            Console.WriteLine("[DummyGen] Pass 4: Fields, methods, properties, events...");
            foreach (var imageDef in metadata.imageDefs)
            {
                var imageName = metadata.GetStringFromIndex(imageDef.nameIndex);
                var typeEnd = imageDef.typeStart + imageDef.typeCount;
                for (int index = imageDef.typeStart; index < typeEnd; index++)
                {
                    var typeDef = metadata.typeDefs[index];
                    var typeDefinition = typeDefinitionDic[typeDef];

                    //field
                    var fieldEnd = Math.Min((int)metadata.fieldDefs.Length, typeDef.fieldStart + typeDef.field_count);
                    for (var i = typeDef.fieldStart; i < fieldEnd; ++i)
                    {
                        if (i < 0 || i >= metadata.fieldDefs.Length) continue;
                        var fieldDef = metadata.fieldDefs[i];
                        if (fieldDef.typeIndex < 0 || fieldDef.typeIndex >= il2Cpp.types.Length) continue;
                        var fieldType = il2Cpp.types[fieldDef.typeIndex];
                        var fieldName = metadata.GetStringFromIndex(fieldDef.nameIndex);
                        var fieldTypeRef = GetTypeReference(typeDefinition, fieldType);
                        var fieldDefinition = new FieldDefinition(fieldName, (FieldAttributes)fieldType.attrs, fieldTypeRef);
                        typeDefinition.Fields.Add(fieldDefinition);
                        fieldDefinitionDic.Add(i, fieldDefinition);

                        if (addToken)
                        {
                            var customTokenAttribute = new CustomAttribute(typeDefinition.Module.ImportReference(tokenAttribute));
                            customTokenAttribute.Fields.Add(new CustomAttributeNamedArgument("Token", new CustomAttributeArgument(stringType, $"0x")));
                            fieldDefinition.CustomAttributes.Add(customTokenAttribute);
                        }

                        //fieldDefault
                        if (metadata.GetFieldDefaultValueFromIndex(i, out var fieldDefault) && fieldDefault.dataIndex != -1)
                        {
                            if (executor.TryGetDefaultValue(fieldDefault.typeIndex, fieldDefault.dataIndex, out var value))
                            {
                                fieldDefinition.Constant = value;
                            }
                            else
                            {
                                var customAttribute = new CustomAttribute(typeDefinition.Module.ImportReference(metadataOffsetAttribute));
                                var offset = new CustomAttributeNamedArgument("Offset", new CustomAttributeArgument(stringType, $"0x{value:X}"));
                                customAttribute.Fields.Add(offset);
                                fieldDefinition.CustomAttributes.Add(customAttribute);
                            }
                        }
                        //fieldOffset
                        if (!fieldDefinition.IsLiteral)
                        {
                            var fieldOffset = il2Cpp.GetFieldOffsetFromIndex(index, i - typeDef.fieldStart, i, typeDefinition.IsValueType, fieldDefinition.IsStatic);
                            if (fieldOffset >= 0)
                            {
                                var customAttribute = new CustomAttribute(typeDefinition.Module.ImportReference(fieldOffsetAttribute));
                                var offset = new CustomAttributeNamedArgument("Offset", new CustomAttributeArgument(stringType, $"0x{fieldOffset:X}"));
                                customAttribute.Fields.Add(offset);
                                fieldDefinition.CustomAttributes.Add(customAttribute);
                            }
                        }
                    }
                    //method
                    var methodEnd = Math.Min((int)metadata.methodDefs.Length, typeDef.methodStart + typeDef.method_count);
                    for (var i = typeDef.methodStart; i < methodEnd; ++i)
                    {
                        if (i < 0 || i >= metadata.methodDefs.Length) continue;
                        var methodDef = metadata.methodDefs[i];
                        var methodName = metadata.GetStringFromIndex(methodDef.nameIndex);
                        var methodDefinition = new MethodDefinition(methodName, (MethodAttributes)methodDef.flags, typeDefinition.Module.ImportReference(typeSystem.Void))
                        {
                            ImplAttributes = (MethodImplAttributes)methodDef.iflags
                        };
                        typeDefinition.Methods.Add(methodDefinition);
                        //genericParameter
                        if (methodDef.genericContainerIndex >= 0 && methodDef.genericContainerIndex < metadata.genericContainers.Length)
                        {
                            var genericContainer = metadata.genericContainers[methodDef.genericContainerIndex];
                            for (int j = 0; j < genericContainer.type_argc; j++)
                            {
                                var genericParameterIndex = genericContainer.genericParameterStart + j;
                                if (genericParameterIndex >= 0 && genericParameterIndex < metadata.genericParameters.Length)
                                {
                                    var param = metadata.genericParameters[genericParameterIndex];
                                    var genericParameter = CreateGenericParameter(param, methodDefinition);
                                    methodDefinition.GenericParameters.Add(genericParameter);
                                }
                            }
                        }
                        var returnType = typeDefinition.Module.ImportReference(typeSystem.Void);
                        if (methodDef.returnType >= 0 && methodDef.returnType < il2Cpp.types.Length)
                        {
                            var methodReturnType = il2Cpp.types[methodDef.returnType];
                            returnType = GetTypeReferenceWithByRef(methodDefinition, methodReturnType);
                            methodDefinition.ReturnType = returnType;
                        }

                        if (addToken)
                        {
                            var customTokenAttribute = new CustomAttribute(typeDefinition.Module.ImportReference(tokenAttribute));
                            customTokenAttribute.Fields.Add(new CustomAttributeNamedArgument("Token", new CustomAttributeArgument(stringType, $"0x{methodDef.token:X}")));
                            methodDefinition.CustomAttributes.Add(customTokenAttribute);
                        }

                        if (methodDefinition.HasBody && typeDefinition.BaseType?.FullName != "System.MulticastDelegate")
                        {
                            var ilprocessor = methodDefinition.Body.GetILProcessor();
                            if (returnType.FullName == "System.Void")
                            {
                                ilprocessor.Append(ilprocessor.Create(OpCodes.Ret));
                            }
                            else if (returnType.IsValueType)
                            {
                                var variable = new VariableDefinition(returnType);
                                methodDefinition.Body.Variables.Add(variable);
                                ilprocessor.Append(ilprocessor.Create(OpCodes.Ldloca_S, variable));
                                ilprocessor.Append(ilprocessor.Create(OpCodes.Initobj, returnType));
                                ilprocessor.Append(ilprocessor.Create(OpCodes.Ldloc_0));
                                ilprocessor.Append(ilprocessor.Create(OpCodes.Ret));
                            }
                            else
                            {
                                ilprocessor.Append(ilprocessor.Create(OpCodes.Ldnull));
                                ilprocessor.Append(ilprocessor.Create(OpCodes.Ret));
                            }
                        }
                        methodDefinitionDic.Add(i, methodDefinition);
                        //method parameter
                        for (var j = 0; j < methodDef.parameterCount; ++j)
                        {
                            int pPos = methodDef.parameterStart + j;
                            if (pPos < 0 || pPos >= metadata.parameterDefs.Length) continue;
                            var parameterDef = metadata.parameterDefs[pPos];
                            var parameterName = metadata.GetStringFromIndex(parameterDef.nameIndex);
                            if (parameterDef.typeIndex < 0 || parameterDef.typeIndex >= il2Cpp.types.Length) continue;
                            var parameterType = il2Cpp.types[parameterDef.typeIndex];
                            var parameterTypeRef = GetTypeReferenceWithByRef(methodDefinition, parameterType);
                            var parameterDefinition = new ParameterDefinition(parameterName, (ParameterAttributes)parameterType.attrs, parameterTypeRef);
                            methodDefinition.Parameters.Add(parameterDefinition);
                            parameterDefinitionDic[methodDef.parameterStart + j] = parameterDefinition;
                            //ParameterDefault
                            if (metadata.GetParameterDefaultValueFromIndex(methodDef.parameterStart + j, out var parameterDefault) && parameterDefault.dataIndex != -1)
                            {
                                if (executor.TryGetDefaultValue(parameterDefault.typeIndex, parameterDefault.dataIndex, out var value))
                                {
                                    parameterDefinition.Constant = value;
                                }
                                else
                                {
                                    var customAttribute = new CustomAttribute(typeDefinition.Module.ImportReference(metadataOffsetAttribute));
                                    var offset = new CustomAttributeNamedArgument("Offset", new CustomAttributeArgument(stringType, $"0x{value:X}"));
                                    customAttribute.Fields.Add(offset);
                                    parameterDefinition.CustomAttributes.Add(customAttribute);
                                }
                            }
                        }
                        //methodAddress
                        if (!methodDefinition.IsAbstract)
                        {
                            var methodPointer = il2Cpp.GetMethodPointer(imageName, methodDef);
                            if (methodPointer > 0)
                            {
                                var customAttribute = new CustomAttribute(typeDefinition.Module.ImportReference(addressAttribute));
                                var fixedMethodPointer = il2Cpp.GetRVA(methodPointer);
                                var rva = new CustomAttributeNamedArgument("RVA", new CustomAttributeArgument(stringType, $"0x{fixedMethodPointer:X}"));
                                var offset = new CustomAttributeNamedArgument("Offset", new CustomAttributeArgument(stringType, $"0x{il2Cpp.MapVATR(methodPointer):X}"));
                                var va = new CustomAttributeNamedArgument("VA", new CustomAttributeArgument(stringType, $"0x{methodPointer:X}"));
                                customAttribute.Fields.Add(rva);
                                customAttribute.Fields.Add(offset);
                                customAttribute.Fields.Add(va);
                                if (methodDef.slot != ushort.MaxValue)
                                {
                                    var slot = new CustomAttributeNamedArgument("Slot", new CustomAttributeArgument(stringType, methodDef.slot.ToString()));
                                    customAttribute.Fields.Add(slot);
                                }
                                methodDefinition.CustomAttributes.Add(customAttribute);
                            }
                        }
                    }
                    //property
                    var propertyEnd = Math.Min((int)metadata.propertyDefs.Length, typeDef.propertyStart + typeDef.property_count);
                    for (var i = typeDef.propertyStart; i < propertyEnd; ++i)
                    {
                        if (i < 0 || i >= metadata.propertyDefs.Length) continue;
                        var propertyDef = metadata.propertyDefs[i];
                        var propertyName = metadata.GetStringFromIndex(propertyDef.nameIndex);
                        TypeReference propertyType = null;
                        MethodDefinition GetMethod = null;
                        MethodDefinition SetMethod = null;
                        if (propertyDef.get >= 0 && methodDefinitionDic.TryGetValue(typeDef.methodStart + propertyDef.get, out var gm))
                        {
                            GetMethod = gm;
                            propertyType = GetMethod.ReturnType;
                        }
                        if (propertyDef.set >= 0 && methodDefinitionDic.TryGetValue(typeDef.methodStart + propertyDef.set, out var sm))
                        {
                            SetMethod = sm;
                            if (SetMethod.Parameters.Count > 0)
                                propertyType ??= SetMethod.Parameters[0].ParameterType;
                        }
                        var propertyTypeRef = propertyType ?? typeDefinition.Module?.ImportReference(typeSystem.Object);
                        var propertyDefinition = new PropertyDefinition(propertyName, (PropertyAttributes)propertyDef.attrs, propertyTypeRef)
                        {
                            GetMethod = GetMethod,
                            SetMethod = SetMethod
                        };
                        typeDefinition.Properties.Add(propertyDefinition);
                        propertyDefinitionDic[i] = propertyDefinition;

                        if (addToken && tokenAttribute != null && typeDefinition.Module != null)
                        {
                            try
                            {
                                var customTokenAttribute = new CustomAttribute(typeDefinition.Module.ImportReference(tokenAttribute));
                                customTokenAttribute.Fields.Add(new CustomAttributeNamedArgument("Token", new CustomAttributeArgument(stringType, $"0x")));
                                propertyDefinition.CustomAttributes.Add(customTokenAttribute);
                            }
                            catch { }
                        }
                    }
                    //event
                    var eventEnd = Math.Min((int)metadata.eventDefs.Length, typeDef.eventStart + typeDef.event_count);
                    for (var i = typeDef.eventStart; i < eventEnd; ++i)
                    {
                        if (i < 0 || i >= metadata.eventDefs.Length) continue;
                        var eventDef = metadata.eventDefs[i];
                        var eventName = metadata.GetStringFromIndex(eventDef.nameIndex);
                        if (eventDef.typeIndex < 0 || eventDef.typeIndex >= il2Cpp.types.Length) continue;
                        var eventType = il2Cpp.types[eventDef.typeIndex];
                        var eventTypeRef = GetTypeReference(typeDefinition, eventType) ?? typeDefinition.Module?.ImportReference(typeSystem.Object);
                        var eventDefinition = new EventDefinition(eventName, (EventAttributes)eventType.attrs, eventTypeRef);
                        if (eventDef.add >= 0 && methodDefinitionDic.TryGetValue(typeDef.methodStart + eventDef.add, out var am))
                            eventDefinition.AddMethod = am;
                        if (eventDef.remove >= 0 && methodDefinitionDic.TryGetValue(typeDef.methodStart + eventDef.remove, out var rm))
                            eventDefinition.RemoveMethod = rm;
                        if (eventDef.raise >= 0 && methodDefinitionDic.TryGetValue(typeDef.methodStart + eventDef.raise, out var im))
                            eventDefinition.InvokeMethod = im;
                        typeDefinition.Events.Add(eventDefinition);
                        eventDefinitionDic[i] = eventDefinition;

                        if (addToken && tokenAttribute != null && typeDefinition.Module != null)
                        {
                            try
                            {
                                var customTokenAttribute = new CustomAttribute(typeDefinition.Module.ImportReference(tokenAttribute));
                                customTokenAttribute.Fields.Add(new CustomAttributeNamedArgument("Token", new CustomAttributeArgument(stringType, $"0x")));
                                eventDefinition.CustomAttributes.Add(customTokenAttribute);
                            }
                            catch { }
                        }
                    }
                }
            }
            Console.WriteLine("[DummyGen] Pass 5: Custom attributes...");
            if (il2Cpp.Version > 20)
            {
                foreach (var imageDef in metadata.imageDefs)
                {
                    var typeEnd = imageDef.typeStart + imageDef.typeCount;
                    for (int index = imageDef.typeStart; index < typeEnd; index++)
                    {
                        var typeDef = metadata.typeDefs[index];
                        var typeDefinition = typeDefinitionDic[typeDef];
                        //field
                        var fieldEnd = typeDef.fieldStart + typeDef.field_count;
                        for (var i = typeDef.fieldStart; i < fieldEnd; ++i)
                        {
                            if (fieldDefinitionDic.TryGetValue(i, out var fieldDefinition))
                            {
                                var fieldDef = metadata.fieldDefs[i];
                                CreateCustomAttribute(imageDef, fieldDef.customAttributeIndex, 0, typeDefinition.Module, fieldDefinition.CustomAttributes);
                            }
                        }

                        //method
                        var methodEnd = typeDef.methodStart + typeDef.method_count;
                        for (var i = typeDef.methodStart; i < methodEnd; ++i)
                        {
                            if (methodDefinitionDic.TryGetValue(i, out var methodDefinition))
                            {
                                var methodDef = metadata.methodDefs[i];
                                CreateCustomAttribute(imageDef, methodDef.customAttributeIndex, methodDef.token, typeDefinition.Module, methodDefinition.CustomAttributes);

                                for (var j = 0; j < methodDef.parameterCount; ++j)
                                {
                                    int pPos = methodDef.parameterStart + j;
                                    if (pPos >= 0 && pPos < metadata.parameterDefs.Length && parameterDefinitionDic.TryGetValue(pPos, out var parameterDefinition))
                                    {
                                        var parameterDef = metadata.parameterDefs[pPos];
                                        CreateCustomAttribute(imageDef, parameterDef.customAttributeIndex, 0, typeDefinition.Module, parameterDefinition.CustomAttributes);
                                    }
                                }
                            }
                        }

                        //property
                        var propertyEnd = typeDef.propertyStart + typeDef.property_count;
                        for (var i = typeDef.propertyStart; i < propertyEnd; ++i)
                        {
                            if (propertyDefinitionDic.TryGetValue(i, out var propertyDefinition))
                            {
                                var propertyDef = metadata.propertyDefs[i];
                            }
                        }

                        //event
                        var eventEnd = typeDef.eventStart + typeDef.event_count;
                        for (var i = typeDef.eventStart; i < eventEnd; ++i)
                        {
                            if (eventDefinitionDic.TryGetValue(i, out var eventDefinition))
                            {
                                var eventDef = metadata.eventDefs[i];
                                CreateCustomAttribute(imageDef, eventDef.customAttributeIndex, 0, typeDefinition.Module, eventDefinition.CustomAttributes);
                            }
                        }
                    }
                }
            }
        }

        private TypeReference GetTypeReferenceWithByRef(MemberReference memberReference, Il2CppType il2CppType)
        {
            var typeReference = GetTypeReference(memberReference, il2CppType);
            if (il2CppType.byref == 1)
            {
                return new ByReferenceType(typeReference);
            }
            else
            {
                return typeReference;
            }
        }

        private TypeReference GetTypeReference(MemberReference memberReference, Il2CppType il2CppType)
        {
            var moduleDefinition = memberReference.Module ?? (memberReference as TypeDefinition)?.DeclaringType?.Module;
            if (moduleDefinition == null)
                return null;
            if (il2CppType == null)
                return moduleDefinition.ImportReference(typeSystem.Object);
            if (il2CppType == null)
                return moduleDefinition.ImportReference(typeSystem.Object);
            switch (il2CppType.type)
            {
                case Il2CppTypeEnum.IL2CPP_TYPE_OBJECT:
                    return moduleDefinition.ImportReference(typeSystem.Object);
                case Il2CppTypeEnum.IL2CPP_TYPE_VOID:
                    return moduleDefinition.ImportReference(typeSystem.Void);
                case Il2CppTypeEnum.IL2CPP_TYPE_BOOLEAN:
                    return moduleDefinition.ImportReference(typeSystem.Boolean);
                case Il2CppTypeEnum.IL2CPP_TYPE_CHAR:
                    return moduleDefinition.ImportReference(typeSystem.Char);
                case Il2CppTypeEnum.IL2CPP_TYPE_I1:
                    return moduleDefinition.ImportReference(typeSystem.SByte);
                case Il2CppTypeEnum.IL2CPP_TYPE_U1:
                    return moduleDefinition.ImportReference(typeSystem.Byte);
                case Il2CppTypeEnum.IL2CPP_TYPE_I2:
                    return moduleDefinition.ImportReference(typeSystem.Int16);
                case Il2CppTypeEnum.IL2CPP_TYPE_U2:
                    return moduleDefinition.ImportReference(typeSystem.UInt16);
                case Il2CppTypeEnum.IL2CPP_TYPE_I4:
                    return moduleDefinition.ImportReference(typeSystem.Int32);
                case Il2CppTypeEnum.IL2CPP_TYPE_U4:
                    return moduleDefinition.ImportReference(typeSystem.UInt32);
                case Il2CppTypeEnum.IL2CPP_TYPE_I:
                    return moduleDefinition.ImportReference(typeSystem.IntPtr);
                case Il2CppTypeEnum.IL2CPP_TYPE_U:
                    return moduleDefinition.ImportReference(typeSystem.UIntPtr);
                case Il2CppTypeEnum.IL2CPP_TYPE_I8:
                    return moduleDefinition.ImportReference(typeSystem.Int64);
                case Il2CppTypeEnum.IL2CPP_TYPE_U8:
                    return moduleDefinition.ImportReference(typeSystem.UInt64);
                case Il2CppTypeEnum.IL2CPP_TYPE_R4:
                    return moduleDefinition.ImportReference(typeSystem.Single);
                case Il2CppTypeEnum.IL2CPP_TYPE_R8:
                    return moduleDefinition.ImportReference(typeSystem.Double);
                case Il2CppTypeEnum.IL2CPP_TYPE_STRING:
                    return moduleDefinition.ImportReference(typeSystem.String);
                case Il2CppTypeEnum.IL2CPP_TYPE_TYPEDBYREF:
                    return moduleDefinition.ImportReference(typeSystem.TypedReference);
                case Il2CppTypeEnum.IL2CPP_TYPE_CLASS:
                case Il2CppTypeEnum.IL2CPP_TYPE_VALUETYPE:
                    {
                        var typeDef = executor.GetTypeDefinitionFromIl2CppType(il2CppType);
                        if (typeDef == null || !typeDefinitionDic.TryGetValue(typeDef, out var typeDefinition) || typeDefinition == null)
                        {
                            return moduleDefinition.ImportReference(typeSystem.Object);
                        }
                        return moduleDefinition.ImportReference(typeDefinition);
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_ARRAY:
                    {
                        var arrayType = il2Cpp.MapVATR<Il2CppArrayType>(il2CppType.data.array);
                        if (arrayType == null)
                            return new ArrayType(moduleDefinition.ImportReference(typeSystem.Object));
                        var oriType = il2Cpp.GetIl2CppType(arrayType.etype);
                        return new ArrayType(GetTypeReference(memberReference, oriType), arrayType.rank);
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_GENERICINST:
                    {
                        var genericClass = il2Cpp.MapVATR<Il2CppGenericClass>(il2CppType.data.generic_class);
                        if (genericClass == null)
                            return moduleDefinition.ImportReference(typeSystem.Object);
                        var typeDef = executor.GetGenericClassTypeDefinition(genericClass);
                        if (typeDef == null || !typeDefinitionDic.TryGetValue(typeDef, out var typeDefinition) || typeDefinition == null)
                        {
                            return moduleDefinition.ImportReference(typeSystem.Object);
                        }
                        var genericInstanceType = new GenericInstanceType(moduleDefinition.ImportReference(typeDefinition));
                        var genericInst = il2Cpp.MapVATR<Il2CppGenericInst>(genericClass.context.class_inst);
                        if (genericInst != null && genericInst.type_argc > 0)
                        {
                            var pointers = il2Cpp.MapVATR<ulong>(genericInst.type_argv, genericInst.type_argc);
                            if (pointers != null)
                            {
                                foreach (var pointer in pointers)
                                {
                                    var oriType = il2Cpp.GetIl2CppType(pointer);
                                    if (oriType != null)
                                        genericInstanceType.GenericArguments.Add(GetTypeReference(memberReference, oriType));
                                }
                            }
                        }
                        return genericInstanceType;
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_SZARRAY:
                    {
                        var oriType = il2Cpp.GetIl2CppType(il2CppType.data.type);
                        if (oriType == null)
                            return new ArrayType(moduleDefinition.ImportReference(typeSystem.Object));
                        return new ArrayType(GetTypeReference(memberReference, oriType));
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_VAR:
                    {
                        var param = executor.GetGenericParameteFromIl2CppType(il2CppType);
                        if (param == null)
                            return moduleDefinition.ImportReference(typeSystem.Object);
                        if (memberReference is MethodDefinition methodDefinition)
                        {
                            var gp = CreateGenericParameter(param, methodDefinition.DeclaringType);
                            return gp ?? (TypeReference)moduleDefinition.ImportReference(typeSystem.Object);
                        }
                        var typeDefinition = (TypeDefinition)memberReference;
                        var gp2 = CreateGenericParameter(param, typeDefinition);
                        return gp2 ?? (TypeReference)moduleDefinition.ImportReference(typeSystem.Object);
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_MVAR:
                    {
                        var param = executor.GetGenericParameteFromIl2CppType(il2CppType);
                        if (param == null)
                            return moduleDefinition.ImportReference(typeSystem.Object);
                        var methodDefinition = (MethodDefinition)memberReference;
                        var gp = CreateGenericParameter(param, methodDefinition);
                        return gp ?? (TypeReference)moduleDefinition.ImportReference(typeSystem.Object);
                    }
                case Il2CppTypeEnum.IL2CPP_TYPE_PTR:
                    {
                        var oriType = il2Cpp.GetIl2CppType(il2CppType.data.type);
                        if (oriType == null)
                            return new PointerType(moduleDefinition.ImportReference(typeSystem.Void));
                        return new PointerType(GetTypeReference(memberReference, oriType));
                    }
                default:
                    throw new NotSupportedException();
            }
        }

        private void CreateCustomAttribute(Il2CppImageDefinition imageDef, int customAttributeIndex, uint token, ModuleDefinition moduleDefinition, Collection<CustomAttribute> customAttributes)
        {
            var attributeIndex = metadata.GetCustomAttributeIndex(imageDef, customAttributeIndex, token);
            if (attributeIndex >= 0)
            {
                try
                {
                    if (il2Cpp.Version < 29)
                    {
                        var attributeTypeRange = metadata.attributeTypeRanges[attributeIndex];
                        for (int i = 0; i < attributeTypeRange.count; i++)
                        {
                            int atPos = attributeTypeRange.start + i;
                            if (atPos < 0 || atPos >= metadata.attributeTypes.Length) continue;
                            var attributeTypeIndex = metadata.attributeTypes[atPos];
                            if (attributeTypeIndex < 0 || attributeTypeIndex >= il2Cpp.types.Length) continue;
                            var attributeType = il2Cpp.types[attributeTypeIndex];
                            var typeDef = executor.GetTypeDefinitionFromIl2CppType(attributeType);
                            if (typeDef == null || !typeDefinitionDic.TryGetValue(typeDef, out var typeDefinition) || typeDefinition == null) continue;
                            if (!TryRestoreCustomAttribute(typeDefinition, moduleDefinition, customAttributes))
                            {
                                var methodPointer = executor.customAttributeGenerators[attributeIndex];
                                var fixedMethodPointer = il2Cpp.GetRVA(methodPointer);
                                var customAttribute = new CustomAttribute(moduleDefinition.ImportReference(attributeAttribute));
                                var name = new CustomAttributeNamedArgument("Name", new CustomAttributeArgument(stringType, typeDefinition.Name));
                                var rva = new CustomAttributeNamedArgument("RVA", new CustomAttributeArgument(stringType, $"0x{fixedMethodPointer:X}"));
                                var offset = new CustomAttributeNamedArgument("Offset", new CustomAttributeArgument(stringType, $"0x{il2Cpp.MapVATR(methodPointer):X}"));
                                customAttribute.Fields.Add(name);
                                customAttribute.Fields.Add(rva);
                                customAttribute.Fields.Add(offset);
                                customAttributes.Add(customAttribute);
                            }
                        }
                    }
                    else
                    {
                        var startRange = metadata.attributeDataRanges[attributeIndex];
                        var endRange = metadata.attributeDataRanges[attributeIndex + 1];
                        metadata.Position = metadata.header.attributeDataOffset + startRange.startOffset;
                        var buff = metadata.ReadBytes((int)(endRange.startOffset - startRange.startOffset));
                        var reader = new CustomAttributeDataReader(executor, buff);
                        if (reader.Count != 0)
                        {
                            for (var i = 0; i < reader.Count; i++)
                            {
                                var visitor = reader.VisitCustomAttributeData();
                                var methodDefinition = methodDefinitionDic[visitor.CtorIndex];
                                var customAttribute = new CustomAttribute(moduleDefinition.ImportReference(methodDefinition));
                                foreach (var argument in visitor.Arguments)
                                {
                                    var parameterDefinition = methodDefinition.Parameters[argument.Index];
                                    var customAttributeArgument = CreateCustomAttributeArgument(parameterDefinition.ParameterType, argument.Value, methodDefinition);
                                    customAttribute.ConstructorArguments.Add(customAttributeArgument);
                                }
                                foreach (var field in visitor.Fields)
                                {
                                    var fieldDefinition = fieldDefinitionDic[field.Index];
                                    var customAttributeArgument = CreateCustomAttributeArgument(fieldDefinition.FieldType, field.Value, fieldDefinition);
                                    var customAttributeNamedArgument = new CustomAttributeNamedArgument(fieldDefinition.Name, customAttributeArgument);
                                    customAttribute.Fields.Add(customAttributeNamedArgument);
                                }
                                foreach (var property in visitor.Properties)
                                {
                                    var propertyDefinition = propertyDefinitionDic[property.Index];
                                    var customAttributeArgument = CreateCustomAttributeArgument(propertyDefinition.PropertyType, property.Value, propertyDefinition);
                                    var customAttributeNamedArgument = new CustomAttributeNamedArgument(propertyDefinition.Name, customAttributeArgument);
                                    customAttribute.Properties.Add(customAttributeNamedArgument);
                                }
                                customAttributes.Add(customAttribute);
                            }
                        }
                    }
                }
                catch
                {
                    MainForm.Log($"ERROR: Error while restoring attributeIndex {attributeIndex}");
                }
            }
        }

        private static bool TryRestoreCustomAttribute(TypeDefinition attributeType, ModuleDefinition moduleDefinition, Collection<CustomAttribute> customAttributes)
        {
            if (attributeType.Methods.Count == 1 && attributeType.Name != "CompilerGeneratedAttribute")
            {
                var methodDefinition = attributeType.Methods[0];
                if (methodDefinition.Name == ".ctor" && methodDefinition.Parameters.Count == 0)
                {
                    var customAttribute = new CustomAttribute(moduleDefinition.ImportReference(methodDefinition));
                    customAttributes.Add(customAttribute);
                    return true;
                }
            }
            return false;
        }

        private GenericParameter CreateGenericParameter(Il2CppGenericParameter param, IGenericParameterProvider iGenericParameterProvider)
        {
            if (param == null || iGenericParameterProvider == null)
                return null;
            if (!genericParameterDic.TryGetValue(param, out var genericParameter))
            {
                var genericName = metadata.GetStringFromIndex(param.nameIndex);
                if (string.IsNullOrEmpty(genericName))
                    genericName = $"T{param.num}";
                genericParameter = new GenericParameter(genericName, iGenericParameterProvider)
                {
                    Attributes = (GenericParameterAttributes)param.flags
                };
                genericParameterDic.Add(param, genericParameter);
                for (int i = 0; i < param.constraintsCount; ++i)
                {
                    int cPos = param.constraintsStart + i;
                    if (cPos >= 0 && cPos < metadata.constraintIndices.Length)
                    {
                        int tIdx = metadata.constraintIndices[cPos];
                        if (tIdx >= 0 && tIdx < il2Cpp.types.Length)
                        {
                            var il2CppType = il2Cpp.types[tIdx];
                            var typeRef = GetTypeReference((MemberReference)iGenericParameterProvider, il2CppType);
                            if (typeRef != null)
                                genericParameter.Constraints.Add(new GenericParameterConstraint(typeRef));
                        }
                    }
                }
            }
            return genericParameter;
        }

        private CustomAttributeArgument CreateCustomAttributeArgument(TypeReference typeReference, BlobValue blobValue, MemberReference memberReference)
        {
            var val = blobValue.Value;
            if (typeReference.FullName == "System.Object")
            {
                if (blobValue.il2CppTypeEnum == Il2CppTypeEnum.IL2CPP_TYPE_IL2CPP_TYPE_INDEX)
                {
                    val = new CustomAttributeArgument(memberReference.Module.ImportReference(typeof(Type)), GetTypeReference(memberReference, (Il2CppType)val));
                }
                else
                {
                    val = new CustomAttributeArgument(GetBlobValueTypeReference(blobValue, memberReference), val);
                }
            }
            else if (val == null)
            {
                return new CustomAttributeArgument(typeReference, val);
            }
            else if (typeReference is ArrayType arrayType)
            {
                var arrayVal = (BlobValue[])val;
                var array = new CustomAttributeArgument[arrayVal.Length];
                var elementType = arrayType.ElementType;
                for (int i = 0; i < arrayVal.Length; i++)
                {
                    array[i] = CreateCustomAttributeArgument(elementType, arrayVal[i], memberReference);
                }
                val = array;
            }
            else if (typeReference.FullName == "System.Type")
            {
                val = GetTypeReference(memberReference, (Il2CppType)val);
            }
            return new CustomAttributeArgument(typeReference, val);
        }

        private TypeReference GetBlobValueTypeReference(BlobValue blobValue, MemberReference memberReference)
        {
            if (blobValue.EnumType != null)
            {
                return GetTypeReference(memberReference, blobValue.EnumType);
            }
            var il2CppType = new Il2CppType
            {
                type = blobValue.il2CppTypeEnum
            };
            return GetTypeReference(memberReference, il2CppType);
        }
    }
}
