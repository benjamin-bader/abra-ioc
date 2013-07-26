﻿/*
 * Copyright © 2013 Ben Bader
 * 
 * Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 *
 * http://www.apache.org/licenses/LICENSE-2.0
 *
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Mono.Cecil.Rocks;
using Stiletto.Internal.Loaders.Codegen;

namespace Stiletto.Fody.Generators
{
    public class ModuleGenerator : Generator
    {
        private readonly TypeDefinition moduleType;
        private readonly TypeReference importedModuleType;

        private IList<MethodDefinition> baseProvidesMethods;
        private MethodReference moduleCtor;

        public TypeReference ModuleType { get { return moduleType; } }
        public bool IsComplete { get; private set; }
        public bool IsOverride { get; private set; }
        public bool IsLibrary { get; private set; }
        public ISet<string> ProvidedKeys { get; private set; }
        public IList<TypeReference> IncludedModules { get; private set; }
        public IList<TypeReference> Injects { get; private set; }
        public IList<MethodDefinition> BaseProvidesMethods { get { return baseProvidesMethods; } }
        public IList<ProviderMethodBindingGenerator> ProviderGenerators { get; private set; }
        public bool IsVisibleToLoader { get; private set; }

        private MethodReference generatedCtor;

        public ModuleGenerator(ModuleDefinition moduleDefinition, References references, TypeDefinition moduleType)
            : base(moduleDefinition, references)
        {
            this.moduleType = Conditions.CheckNotNull(moduleType, "moduleType");

            var attr = moduleType.CustomAttributes.SingleOrDefault(Attributes.IsModuleAttribute);

            if (attr == null)
            {
                throw new ArgumentException(moduleType.FullName + " is not marked as a [Module].", "moduleType");
            }

            CustomAttributeNamedArgument? argComplete = null,
                                          argInjects = null,
                                          argIncludes = null,
                                          argOverrides = null,
                                          argIsLibrary = null;

            foreach (var arg in attr.Properties)
            {
                switch (arg.Name)
                {
                    case "IsComplete":
                        argComplete = arg;
                        break;
                    case "Injects":
                        argInjects = arg;
                        break;
                    case "IncludedModules":
                        argIncludes = arg;
                        break;
                    case "IsOverride":
                        argOverrides = arg;
                        break;
                    case "IsLibrary":
                        argIsLibrary = arg;
                        break;
                    default:
                        throw new Exception("WTF, unexpected ModuleAttribute property: " + arg.Name);
                }
            }

            IsComplete = GetArgumentValue(argComplete, true);
            IsOverride = GetArgumentValue(argOverrides, false);
            IsLibrary = GetArgumentValue(argIsLibrary, false);

            Injects = new List<TypeReference>();
            if (argInjects != null)
            {
                foreach (var val in (CustomAttributeArgument[])argInjects.Value.Argument.Value)
                {
                    var injectType = (TypeReference)val.Value;
                    Injects.Add(injectType);
                }
            }

            IncludedModules = new List<TypeReference>();
            if (argIncludes != null)
            {
                foreach (var val in (CustomAttributeArgument[])argIncludes.Value.Argument.Value)
                {
                    var includeType = (TypeReference)val.Value;
                    IncludedModules.Add(includeType);
                }
            }

            importedModuleType = Import(moduleType);

            baseProvidesMethods = moduleType
                .Methods
                .Where(m => m.CustomAttributes.Any(Attributes.IsProvidesAttribute))
                .ToList();

            ProviderGenerators = baseProvidesMethods
                .Select(m => new ProviderMethodBindingGenerator(ModuleDefinition, References, importedModuleType, m, IsLibrary))
                .ToList();

            IsVisibleToLoader = true;
        }

        private static T GetArgumentValue<T>(CustomAttributeNamedArgument? arg, T defaultValue = default(T))
        {
            return arg == null
                       ? defaultValue
                       : (T)arg.Value.Argument.Value;
        }

        public override void Validate(IErrorReporter errorReporter)
        {
            if (moduleType.BaseType != null && moduleType.BaseType.FullName != References.Object.FullName)
            {
                errorReporter.LogError(moduleType.FullName + ": Modules must inherit from System.Object only.");
            }

            if (moduleType.IsAbstract)
            {
                errorReporter.LogError(moduleType.FullName + ": Modules cannot be abstract.");
            }

            moduleCtor = moduleType.GetConstructors().FirstOrDefault(m => m.Parameters.Count == 0);
            if (moduleCtor == null)
            {
                errorReporter.LogError(moduleType.FullName + " is marked as a [Module], but no default constructor is visible.");
            }
            else
            {
                moduleCtor = Import(moduleCtor);
            }

            ProvidedKeys = new HashSet<string>(StringComparer.Ordinal);
            foreach (var method in baseProvidesMethods)
            {
                var name = method.GetNamedAttributeName();
                var key = CompilerKeys.ForType(method.ReturnType, name);

                if (!ProvidedKeys.Add(key))
                {
                    errorReporter.LogError(moduleType.FullName + ": Duplicate provider key for method " + moduleType.FullName + "." + method.Name);
                }
            }

            switch (moduleType.Attributes & TypeAttributes.VisibilityMask)
            {
                case TypeAttributes.NestedFamily:
                case TypeAttributes.NestedFamANDAssem:
                case TypeAttributes.NestedPrivate:
                case TypeAttributes.NotPublic:
                    // This type is not externally visible and can't be included in a compiled loader.
                    // It can still be loaded reflectively.
                    IsVisibleToLoader = false;
                    errorReporter.LogWarning(moduleType.FullName + ": This type is private, and will be loaded reflectively.  Consider making it internal or public.");
                    break;
            }

            foreach (var gen in ProviderGenerators)
            {
                gen.Validate(errorReporter);
            }
        }

        public void ValidateCompleteness(ISet<string> injectableKeys, IErrorReporter errorReporter)
        {
            if (!IsComplete)
            {
                return;
            }

            foreach (var method in baseProvidesMethods)
            {
                foreach (var param in method.Parameters)
                {
                    var name = param.Name;
                    var key = CompilerKeys.ForParam(param);

                    if (!injectableKeys.Contains(key))
                    {
                        const string msg = "{0}: Module is a complete module but has an unsatisfied dependency on {1}{2}";
                        var nameDescr = name == null ? string.Empty : "[Named(\"" + name + "\")] ";
                        errorReporter.LogError(string.Format(msg, moduleType.FullName, nameDescr, param.ParameterType.FullName));
                    }
                }
            }
        }

        public override TypeDefinition Generate(IErrorReporter errorReporter)
        {
            var name = moduleType.Name + CodegenLoader.ModuleSuffix;
            var t = new TypeDefinition(moduleType.Namespace, name, moduleType.Attributes, References.RuntimeModule);

            t.CustomAttributes.Add(new CustomAttribute(References.CompilerGeneratedAttribute));

            foreach (var gen in ProviderGenerators)
            {
                gen.RuntimeModuleType = t;
                gen.Generate(errorReporter);
            }

            EmitCtor(t);
            EmitCreateModule(t);
            EmitGetBindings(t);

            if (moduleType.DeclaringType != null)
            {
                t.DeclaringType = moduleType.DeclaringType;
            }

            return t;
        }

        public override KeyedCtor GetKeyedCtor()
        {
            // We don't care about keys for modules, we can dispatch on moduleType.
            return null;
        }

        public Tuple<TypeReference, MethodReference> GetModuleTypeAndGeneratedCtor()
        {
            Conditions.CheckNotNull(generatedCtor);
            return Tuple.Create((TypeReference)moduleType, generatedCtor);
        }

        private void EmitCreateModule(TypeDefinition runtimeModule)
        {
            /**
             * public override object CreateModule()
             * {
             *     return new CompiledRuntimeModule();
             * }
             */

            var createModule = new MethodDefinition(
                "CreateModule",
                MethodAttributes.Public | MethodAttributes.Virtual,
                References.Object);

            var il = createModule.Body.GetILProcessor();
            il.Emit(OpCodes.Newobj, moduleCtor);
            il.Emit(OpCodes.Ret);

            runtimeModule.Methods.Add(createModule);
        }

        private void EmitGetBindings(TypeDefinition runtimeModule)
        {
            /**
             * public override void GetBindings(Dictionary<string, Binding> bindings)
             * {
             *     var module = this.Module;
             *     bindings.Add("keyof binding0", new ProviderBinding0(module));
             *     ...
             *     bindings.Add("keyof bindingN", new ProviderBindingN(module));
             * }
             */
            var getBindings = new MethodDefinition(
                "GetBindings",
                MethodAttributes.Public | MethodAttributes.Virtual,
                References.Void);

            var vModule = new VariableDefinition("module", importedModuleType);
            getBindings.Body.Variables.Add(vModule);
            getBindings.Body.InitLocals = true;

            getBindings.Parameters.Add(new ParameterDefinition(References.DictionaryOfStringToBinding));

            var il = getBindings.Body.GetILProcessor();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Callvirt, References.RuntimeModule_ModuleGetter);
            il.Emit(OpCodes.Castclass, importedModuleType);
            il.Emit(OpCodes.Stloc, vModule);

            foreach (var binding in ProviderGenerators)
            {
                il.Emit(OpCodes.Ldarg_1);
                il.Emit(OpCodes.Ldstr, binding.Key);
                il.Emit(OpCodes.Ldloc, vModule);
                il.Emit(OpCodes.Newobj, binding.GeneratedCtor);
                il.Emit(OpCodes.Callvirt, References.DictionaryOfStringToBinding_Add);
            }

            il.Emit(OpCodes.Ret);

            runtimeModule.Methods.Add(getBindings);
        }

        private void EmitCtor(TypeDefinition runtimeModule)
        {
            /**
             * public CompiledRuntimeModule()
             *     : base(typeof(OriginalModule),
             *            new[] { "key0", ..., "keyN" },
             *            new[] { typeof(IncludedModule0), ..., typeof(IncludedModuleN) },
             *            isComplete,
             *            isLibrary)
             * {
             * }
             */

            var ctor = new MethodDefinition(".ctor",
                MethodAttributes.Public | MethodAttributes.HideBySig | MethodAttributes.SpecialName | MethodAttributes.RTSpecialName,
                References.Void);

            var il = ctor.Body.GetILProcessor();

            // Push args (this, moduleType, entryPoints, includes, complete, library) and call base ctor
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldtoken, importedModuleType);
            il.Emit(OpCodes.Call, References.Type_GetTypeFromHandle);

            // make array of entry point keys
            il.Emit(OpCodes.Ldc_I4, Injects.Count);
            il.Emit(OpCodes.Newarr, References.String);
            for (var i = 0; i < Injects.Count; ++i)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldstr, CompilerKeys.GetMemberKey(Injects[i]));
                il.Emit(OpCodes.Stelem_Ref);
            }

            // make array of included module types
            il.Emit(OpCodes.Ldc_I4, IncludedModules.Count);
            il.Emit(OpCodes.Newarr, References.Type);
            for (var i = 0; i < IncludedModules.Count; ++i)
            {
                il.Emit(OpCodes.Dup);
                il.Emit(OpCodes.Ldc_I4, i);
                il.Emit(OpCodes.Ldtoken, Import(IncludedModules[i]));
                il.Emit(OpCodes.Call, References.Type_GetTypeFromHandle);
                il.Emit(OpCodes.Stelem_Ref);
            }

            il.EmitBoolean(IsComplete);
            il.EmitBoolean(IsLibrary);
            il.EmitBoolean(IsOverride);

            il.Emit(OpCodes.Call, References.RuntimeModule_Ctor);
            il.Emit(OpCodes.Ret);

            runtimeModule.Methods.Add(ctor);
            generatedCtor = ctor;
        }
    }
}
