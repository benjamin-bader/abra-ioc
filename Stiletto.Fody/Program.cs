/*
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
using System.Text;
using System.Xml.Linq;
using Mono.Cecil;

namespace Stiletto.Fody
{
    public class Program
    {
        public static void Main(string[] args)
        {
            var asm = args.Length > 0
                ? args[0]
                : @"C:\Users\ben\Development\stiletto\Stiletto.Test\bin\Debug\Stiletto.Test.dll";
            var ad = AssemblyDefinition.ReadAssembly(asm, new ReaderParameters { ReadSymbols = true });
            var md = ad.MainModule;

            var config = XElement.Parse("<Config SuppressGraphviz=\"true\" />");
            var weaver = new ModuleWeaver()
                             {
                                 LogError = Console.WriteLine,
                                 ModuleDefinition = md,
                                 ReferenceCopyLocalPaths = new List<string>(),
                                 AssemblyResolver = new DefaultAssemblyResolver(),
                                 Config = config,
                             };

            weaver.Execute();

            ad.Write(asm, new WriterParameters() { WriteSymbols = true });
        }
    }
}
