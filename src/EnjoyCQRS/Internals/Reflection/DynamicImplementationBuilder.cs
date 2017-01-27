﻿// Copyright 2012-2016 Chris Patterson
//  
// Licensed under the Apache License, Version 2.0 (the "License"); you may not use
// this file except in compliance with the License. You may obtain a copy of the 
// License at 
// 
//     http://www.apache.org/licenses/LICENSE-2.0 
// 
// Unless required by applicable law or agreed to in writing, software distributed
// under the License is distributed on an "AS IS" BASIS, WITHOUT WARRANTIES OR 
// CONDITIONS OF ANY KIND, either express or implied. See the License for the 
// specific language governing permissions and limitations under the License.
namespace GreenPipes.Internals.Reflection
{
    using System;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Reflection;
    using System.Reflection.Emit;
    using Extensions;
#if NETSTANDARD1_6
    using System.IO;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis;
    using System.Runtime.Loader;
#endif

    public class DynamicImplementationBuilder :
        IImplementationBuilder
    {
        const MethodAttributes PropertyAccessMethodAttributes = MethodAttributes.Public
            | MethodAttributes.SpecialName
            | MethodAttributes.HideBySig
            | MethodAttributes.Final
            | MethodAttributes.Virtual
            | MethodAttributes.VtableLayoutMask;

        readonly ConcurrentDictionary<string, ModuleBuilder> _moduleBuilders;
        readonly string _proxyNamespaceSuffix = "GreenPipes.DynamicInternal" + Guid.NewGuid().ToString("N");
        readonly ConcurrentDictionary<Type, Lazy<Type>> _proxyTypes;

        public DynamicImplementationBuilder()
        {
            _moduleBuilders = new ConcurrentDictionary<string, ModuleBuilder>();

            _proxyTypes = new ConcurrentDictionary<Type, Lazy<Type>>();
        }

        public Type GetImplementationType(Type interfaceType)
        {
            return _proxyTypes.GetOrAdd(interfaceType, x => new Lazy<Type>(() => CreateImplementation(x))).Value;
        }

        Type CreateImplementation(Type interfaceType)
        {
            if (!interfaceType.GetTypeInfo().IsInterface)
                throw new ArgumentException("Proxies can only be created for interfaces: " + interfaceType.Name, nameof(interfaceType));

            return GetModuleBuilderForType(interfaceType, moduleBuilder => CreateTypeFromInterface(moduleBuilder, interfaceType));
        }

        Type CreateTypeFromInterface(ModuleBuilder builder, Type interfaceType)
        {
            var typeName = "GreenPipes.DynamicInternal." +
                (interfaceType.IsNested && (interfaceType.DeclaringType != null)
                    ? $"{interfaceType.DeclaringType.Name}+{TypeCache.GetShortName(interfaceType)}"
                    : TypeCache.GetShortName(interfaceType));
            try
            {
                var typeBuilder = builder.DefineType(typeName, TypeAttributes.Serializable
                    | TypeAttributes.Class
                    | TypeAttributes.Public
                    | TypeAttributes.Sealed,
                    typeof(object), new[] { interfaceType });

                typeBuilder.DefineDefaultConstructor(MethodAttributes.Public);

                IEnumerable<PropertyInfo> properties = interfaceType.GetAllProperties();
                foreach (var property in properties)
                {
                    var fieldBuilder = typeBuilder.DefineField("field_" + property.Name, property.PropertyType,
                        FieldAttributes.Private);

                    var propertyBuilder = typeBuilder.DefineProperty(property.Name,
                        property.Attributes | PropertyAttributes.HasDefault, property.PropertyType, null);

                    var getMethod = GetGetMethodBuilder(property, typeBuilder, fieldBuilder);
                    var setMethod = GetSetMethodBuilder(property, typeBuilder, fieldBuilder);

                    propertyBuilder.SetGetMethod(getMethod);
                    propertyBuilder.SetSetMethod(setMethod);
                }

                // TODO: revisit it later
                return typeBuilder.CreateTypeInfo().AsType();
            }
            catch (Exception ex)
            {
                string message = $"Exception creating proxy ({typeName}) for {TypeCache.GetShortName(interfaceType)}";

                throw new InvalidOperationException(message, ex);
            }
        }

        MethodBuilder GetGetMethodBuilder(PropertyInfo propertyInfo, TypeBuilder typeBuilder, FieldBuilder fieldBuilder)
        {
            var getMethodBuilder = typeBuilder.DefineMethod("get_" + propertyInfo.Name,
                PropertyAccessMethodAttributes,
                propertyInfo.PropertyType,
                Type.EmptyTypes);

            var il = getMethodBuilder.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldfld, fieldBuilder);
            il.Emit(OpCodes.Ret);

            return getMethodBuilder;
        }

        MethodBuilder GetSetMethodBuilder(PropertyInfo propertyInfo, TypeBuilder typeBuilder, FieldBuilder fieldBuilder)
        {
            var setMethodBuilder = typeBuilder.DefineMethod("set_" + propertyInfo.Name,
                PropertyAccessMethodAttributes,
                null,
                new[] { propertyInfo.PropertyType });

            var il = setMethodBuilder.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(OpCodes.Stfld, fieldBuilder);
            il.Emit(OpCodes.Ret);

            return setMethodBuilder;
        }

        TResult GetModuleBuilderForType<TResult>(Type interfaceType, Func<ModuleBuilder, TResult> callback)
        {
            var assemblyName = interfaceType.Namespace + _proxyNamespaceSuffix;

#if !NETSTANDARD1_6

            var builder = _moduleBuilders.GetOrAdd(assemblyName, name =>
            {
                const AssemblyBuilderAccess access = AssemblyBuilderAccess.RunAndCollect;
                var assemblyBuilder = AppDomain.CurrentDomain.DefineDynamicAssembly(new AssemblyName(name), access);

                var moduleBuilder = assemblyBuilder.DefineDynamicModule(assemblyName);

                return moduleBuilder;
            });

#else
            var builder = _moduleBuilders.GetOrAdd(assemblyName, name =>
            {

                var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

                CSharpCompilation compilation = CSharpCompilation.Create(assemblyName, references: new[] {
                     MetadataReference.CreateFromFile(typeof(object).GetTypeInfo().Assembly.Location)

                }, options: options);

                using (var ms = new MemoryStream())
                {
                    var result = compilation.Emit(ms);

                    if (result.Success)
                    {
                        ms.Seek(0, SeekOrigin.Begin);

                        Assembly assembly = AssemblyLoadContext.Default.LoadFromStream(ms);

                        AssemblyBuilder asmBuilder = AssemblyBuilder.DefineDynamicAssembly(assembly.GetName(), AssemblyBuilderAccess.RunAndCollect);
                        var moduleBuilder = asmBuilder.DefineDynamicModule(assemblyName);

                        return moduleBuilder;
                    }
                }

                return null;
            });

#endif

            return callback(builder);
        }

    }
}