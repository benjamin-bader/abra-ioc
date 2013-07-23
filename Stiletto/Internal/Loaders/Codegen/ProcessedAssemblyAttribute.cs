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

namespace Stiletto.Internal.Loaders.Codegen
{
    /// <summary>
    /// Represents a marker indicating that the annotated module has already
    /// been processed and contains compiler-generated code.
    /// </summary>
    [AttributeUsage(AttributeTargets.Module)]
    public class ProcessedAssemblyAttribute : Attribute
    {
    }
}
