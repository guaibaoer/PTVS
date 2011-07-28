﻿/* ****************************************************************************
 *
 * Copyright (c) Microsoft Corporation. 
 *
 * This source code is subject to terms and conditions of the Apache License, Version 2.0. A 
 * copy of the license can be found in the License.html file at the root of this distribution. If 
 * you cannot locate the Apache License, Version 2.0, please send an email to 
 * vspython@microsoft.com. By using this source code in any fashion, you are agreeing to be bound 
 * by the terms of the Apache License, Version 2.0.
 *
 * You must not remove this notice, or any other, from this software.
 *
 * ***************************************************************************/

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Text;
using IronPython.Hosting;
using IronPython.Modules;
using IronPython.Runtime;
using IronPython.Runtime.Operations;
using IronPython.Runtime.Types;
using Microsoft.PythonTools.Analysis;
using Microsoft.PythonTools.Interpreter;
using Microsoft.PythonTools.Parsing;
using Microsoft.Scripting.Actions;
using Microsoft.Scripting.Hosting;
using Microsoft.Scripting.Hosting.Providers;
using Microsoft.Scripting.Runtime;
using Microsoft.Win32;

namespace Microsoft.IronPythonTools.Interpreter {
    class IronPythonInterpreter : IPythonInterpreter, IDotNetPythonInterpreter {
        private readonly Dictionary<PythonType, IronPythonType> _types = new Dictionary<PythonType, IronPythonType>();
        private readonly ScriptEngine _engine;
        private readonly CodeContext _codeContext;
        private readonly CodeContext _codeContextCls;

        private readonly TopNamespaceTracker _namespaceTracker;
        private readonly Dictionary<string, IronPythonModule> _modules = new Dictionary<string, IronPythonModule>();
        private readonly HashSet<string> _assemblyLoadSet = new HashSet<string>();
        private readonly IronPythonInterpreterFactory _factory;
        private IInterpreterState _state;
        private PythonTypeDatabase _typeDb;

        public IronPythonInterpreter(IronPythonInterpreterFactory factory)
            : this(factory, Python.CreateEngine(new Dictionary<string, object> { { "NoAssemblyResolveHook", true } })) {
        }

        public IronPythonInterpreter(IronPythonInterpreterFactory factory, ScriptEngine engine) {
            _engine = engine;
            _factory = factory;

            var pythonContext = HostingHelpers.GetLanguageContext(_engine) as PythonContext;
            _codeContextCls = new ModuleContext(new PythonDictionary(), pythonContext).GlobalContext;
            _codeContextCls.ModuleContext.ShowCls = true;

            _codeContext = new ModuleContext(
                new PythonDictionary(),
                HostingHelpers.GetLanguageContext(_engine) as PythonContext
                ).GlobalContext;

            _namespaceTracker = new TopNamespaceTracker(_codeContext.LanguageContext.DomainManager);

            AddAssembly(LoadAssemblyInfo(typeof(string).Assembly));
            AddAssembly(LoadAssemblyInfo(typeof(Debug).Assembly));
            
            string installDir = GetPythonInstallDir();
            if (installDir != null) {
                var dllDir = Path.Combine(installDir, "DLLs");
                if (Directory.Exists(dllDir)) {
                    foreach (var assm in Directory.GetFiles(dllDir)) {
                        try {
                            var asm = Assembly.LoadFile(Path.Combine(dllDir, assm));
                            _engine.Runtime.LoadAssembly(asm);

                            AddAssembly(LoadAssemblyInfo(asm));
                        } catch {
                        }
                    }
                }
            }

            LoadAssemblies();

            LoadModules();

            
            if (factory.ConfigurableDatabaseExists()) {
                LoadNewTypeDb();
            }
        }

        private void LoadModules() {
            var names = _engine.Operations.GetMember<PythonTuple>(_engine.GetSysModule(), "builtin_module_names");
            foreach (string modName in names) {
                try {
                    PythonModule mod = Importer.Import(_codeContextCls, modName, PythonOps.EmptyTuple, 0) as PythonModule;
                    Debug.Assert(mod != null);

                    if (modName == "__builtin__") {
                        _modules[modName] = new IronPythonBuiltinModule(this, mod, modName);
                    } else {
                        _modules[modName] = new IronPythonModule(this, mod, modName);
                    }
                } catch {
                    // importing can throw, ignore that module
                    continue;
                }
            }
       }

        public void Initialize(IInterpreterState state) {
            state.SpecializeFunction("clr", "AddReference", (n) => AddReference(n, null));
            state.SpecializeFunction("clr", "AddReferenceByPartialName", (n) => AddReference(n, ClrModule.LoadAssemblyByPartialName));
            state.SpecializeFunction("clr", "AddReferenceByName", (n) => AddReference(n, null));
            state.SpecializeFunction("clr", "AddReferenceToFile", (n) => AddReference(n, (s) => ClrModule.LoadAssemblyFromFile(_codeContext, s)));
            state.SpecializeFunction("clr", "AddReferenceToFileAndPath", (n) => AddReference(n, (s) => ClrModule.LoadAssemblyFromFileWithPath(_codeContext, s)));
            _state = state;
        }

        /// <summary>
        /// VS seems to load extensions via Assembly.LoadFrom. When an assembly is being loaded via Assembly.Load the CLR fusion probes privatePath 
        /// set in App.config (devenv.exe.config) first and then tries the code base of the assembly that called Assembly.Load if it was itself loaded via LoadFrom. 
        /// In order to locate IronPython.Modules correctly, the call to Assembly.Load must originate from an assembly in IronPythonTools installation folder. 
        /// Although Microsoft.Scripting is also in that folder it can be loaded first by IronRuby and that causes the Assembly.Load to search in IronRuby's 
        /// installation folder. Adding a reference to IronPython.Modules also makes sure that the assembly is loaded from the same location as IronPythonToolsCore.
        /// </summary>
        private static void LoadAssemblies() {
            GC.KeepAlive(typeof(IronPython.Modules.ArrayModule)); // IronPython.Modules
        }
       

        internal static string GetPythonInstallDir() {
            using (var ipy = Registry.LocalMachine.OpenSubKey("SOFTWARE\\IronPython")) {
                if (ipy != null) {
                    using (var twoSeven = ipy.OpenSubKey("2.7")) {
                        if (twoSeven != null) {
                            var installPath = twoSeven.OpenSubKey("InstallPath");
                            if (installPath != null) {
                                var res = installPath.GetValue("") as string;
                                if (res != null) {
                                    return res;
                                }
                            }
                        }
                    }
                }
            }

            var paths = Environment.GetEnvironmentVariable("PATH");
            if (paths != null) {
                foreach (string dir in paths.Split(Path.PathSeparator)) {
                    try {
                        if (IronPythonExistsIn(dir)) {
                            return dir;
                        }
                    } catch {
                        // ignore
                    }
                }
            }

            return null;
        }

        private static bool IronPythonExistsIn(string/*!*/ dir) {
            return File.Exists(Path.Combine(dir, "ipy.exe"));
        }

        public ScriptEngine Engine {
            get {
                return _engine;
            }
        }

        private KeyValuePair<Assembly, TopNamespaceTracker> LoadAssemblyInfo(Assembly assm) {
            var nsTracker = new TopNamespaceTracker(_codeContext.LanguageContext.DomainManager);
            nsTracker.LoadAssembly(assm);
            return new KeyValuePair<Assembly, TopNamespaceTracker>(assm, nsTracker);
        }

        private void AddAssembly(KeyValuePair<Assembly, TopNamespaceTracker> assembly) {
            _namespaceTracker.LoadAssembly(assembly.Key);
        }

        private void AddReference(Microsoft.PythonTools.Parsing.Ast.CallExpression node, Func<string, Assembly> partialLoader) {
            // processes a call to clr.AddReference updating project state
            // so that it contains the newly loaded assembly.
            foreach (var arg in node.Args) {
                var cexpr = arg.Expression as Microsoft.PythonTools.Parsing.Ast.ConstantExpression;
                if (cexpr == null || !(cexpr.Value is string || cexpr.Value is AsciiString)) {
                    // can't process this add reference
                    continue;
                }

                // TODO: Should we do a .NET reflection only load rather than
                // relying on the CLR module here?  That would prevent any code from
                // running although at least we don't taint our own modules which
                // are loaded with this current code.
                var asmName = cexpr.Value as string;
                if (asmName == null) {
                    // check for byte string
                    var bytes = cexpr.Value as AsciiString;
                    if (bytes != null) {
                        asmName = bytes.String;
                    }
                }
                if (asmName != null && _assemblyLoadSet.Add(asmName)) {
                    Assembly asm = null;
                    try {
                        if (partialLoader != null) {
                            asm = partialLoader(asmName);
                        } else {
                            try {
                                asm = ClrModule.LoadAssemblyByName(_codeContext, asmName);
                            } catch {
                                asm = ClrModule.LoadAssemblyByPartialName(asmName);
                            }
                        }

                        if (asm == null && _state != null) {
                            var invalidPathChars = Path.GetInvalidPathChars();
                            foreach (var dir in _state.AnalysisDirectories) {
                                if (dir.IndexOfAny(invalidPathChars) == -1 && asmName.IndexOfAny(invalidPathChars) == -1) {

                                    string path = Path.Combine(dir, asmName);
                                    if (File.Exists(path)) {
                                        asm = Assembly.LoadFrom(path);
                                    } else if (File.Exists(path + ".dll")) {
                                        asm = Assembly.LoadFrom(path + ".dll");
                                    } else if (File.Exists(path + ".exe")) {
                                        asm = Assembly.LoadFrom(path + ".exe");
                                    }
                                }

                            }
                        }
                    } catch {
                    }
                    AddAssembly(asm);                
                }
            }
        }

        public void AddAssembly(Assembly asm) {
            if (asm != null && !_namespaceTracker.PackageAssemblies.Contains(asm)) {
                if (_namespaceTracker.LoadAssembly(asm)) {
                    var modNamesChanged = ModuleNamesChanged;
                    if (modNamesChanged != null) {
                        modNamesChanged(this, EventArgs.Empty);
                    }
                }
            }
        }

        #region IPythonInterpreter Members

        public IPythonType GetBuiltinType(BuiltinTypeId id) {
            switch (id) {
                case BuiltinTypeId.Bool: return GetTypeFromType(typeof(bool));
                case BuiltinTypeId.BuiltinFunction: return GetTypeFromType(typeof(BuiltinFunction));
                case BuiltinTypeId.BuiltinMethodDescriptor: return GetTypeFromType(typeof(BuiltinMethodDescriptor));
                case BuiltinTypeId.Complex: return GetTypeFromType(typeof(Complex));
                case BuiltinTypeId.Dict: return GetTypeFromType(typeof(PythonDictionary));
                case BuiltinTypeId.Float: return GetTypeFromType(typeof(double));
                case BuiltinTypeId.Function: return GetTypeFromType(typeof(PythonFunction));
                case BuiltinTypeId.Generator: return GetTypeFromType(typeof(PythonGenerator));
                case BuiltinTypeId.Int: return GetTypeFromType(typeof(int));
                case BuiltinTypeId.List: return GetTypeFromType(typeof(List));
                case BuiltinTypeId.Long: return GetTypeFromType(typeof(System.Numerics.BigInteger));
                case BuiltinTypeId.Unknown: return GetTypeFromType(typeof(DynamicNull));
                case BuiltinTypeId.Object: return GetTypeFromType(typeof(object));
                case BuiltinTypeId.Set: return GetTypeFromType(typeof(SetCollection));
                case BuiltinTypeId.Str: return GetTypeFromType(typeof(string));
                case BuiltinTypeId.Bytes: return GetTypeFromType(typeof(string));   // keep strings and bytes the same on Ipy because '' and u'abc' create the same type
                case BuiltinTypeId.Tuple: return GetTypeFromType(typeof(PythonTuple));
                case BuiltinTypeId.Type: return GetTypeFromType(typeof(PythonType));
                case BuiltinTypeId.NoneType: return GetTypeFromType(typeof(DynamicNull));
                case BuiltinTypeId.Ellipsis: return GetTypeFromType(typeof(Ellipsis));
                case BuiltinTypeId.DictKeys: return GetTypeFromType(typeof(DictionaryKeyEnumerator));
                case BuiltinTypeId.DictValues: return GetTypeFromType(typeof(DictionaryValueEnumerator));
                default:
                    return null;
            }
        }

        public IList<string> GetModuleNames() {
            List<string> res = new List<string>(_modules.Keys);

            foreach (var r in _namespaceTracker.Keys) {
                res.Add(r);
            }

            if (_typeDb != null) {
                foreach (var name in _typeDb.GetModuleNames()) {
                    res.Add(name);
                }
            }
            
            return res;
        }


        public event EventHandler ModuleNamesChanged;

        public IronPythonModule GetModule(string name) {
            return _modules[name];
        }

        public IPythonModule ImportModule(string name) {
            if (_typeDb != null) {
                var res = _typeDb.GetModule(name);
                if (res != null) {
                    return res;
                }
            }

            IronPythonModule mod;
            if (_modules.TryGetValue(name, out mod)) {
                return mod;
            }

            var ns = _namespaceTracker.TryGetPackage(name);
            if (ns != null) {
                return MakeObject(ns) as IPythonModule;
            }

            return null;
        }

        public IModuleContext CreateModuleContext() {
            return new IronPythonModuleContext();
        }

        #endregion

        internal IPythonType GetTypeFromType(Type type) {
            if (type.IsGenericParameter && type.GetInterfaces().Length != 0) {
                // generic parameter with constraints, IronPython will throw an 
                // exception while constructing the PythonType 
                // http://ironpython.codeplex.com/workitem/30905
                // Return the type for the interface
                return GetTypeFromType(type.GetInterfaces()[0]);
            }

            var pyType = DynamicHelpers.GetPythonTypeFromType(type);
            
            return GetTypeFromType(pyType);
        }

        internal IPythonType GetTypeFromType(PythonType type) {
            IronPythonType res;
            lock (_types) {
                if (!_types.TryGetValue(type, out res)) {
                    _types[type] = res = new IronPythonType(this, type);
                }
            }
            return res;
        }

        internal bool TryGetMember(object obj, string name, out object value) {
            return TryGetMember(_codeContext, obj, name, out value);
        }

        internal bool TryGetMember(object obj, string name, bool showClr, out object value) {
            var cctx = showClr ? _codeContextCls : _codeContext;
            return TryGetMember(cctx, obj, name, out value);
        }

        private bool TryGetMember(CodeContext codeContext, object obj, string name, out object value) {
            NamespaceTracker nt = obj as NamespaceTracker;
            if (nt != null) {
                value = NamespaceTrackerOps.GetCustomMember(codeContext, nt, name);
                return value != OperationFailed.Value;
            }

            object result = Builtin.getattr(codeContext, obj, name, this);
            if (result == this) {
                value = null;
                return false;
            } else {
                value = result;
                return true;
            }
        }

        public CodeContext CodeContext {
            get {
                return _codeContext;
            }
        }

        internal static IList<string> DirHelper(object obj, bool showClr) {
            NamespaceTracker nt = obj as NamespaceTracker;
            if (nt != null) {
                return nt.GetMemberNames();
            }
            
            var dir = showClr ? ClrModule.DirClr(obj) : ClrModule.Dir(obj);
            int len = dir.__len__();
            string[] result = new string[len];
            for (int i = 0; i < len; i++) {
                // TODO: validate
                result[i] = dir[i] as string;
            }
            return result;
        }

        private readonly Dictionary<object, IMember> _members = new Dictionary<object, IMember>();

        internal IMember MakeObject(object obj) {
            if (obj == null) {
                return null;
            }
            lock (this) {
                IMember res;
                if (!_members.TryGetValue(obj, out res)) {
                    PythonModule mod = obj as PythonModule;
                    if (mod != null) {
                        // FIXME: name
                        object name;
                        if (!mod.Get__dict__().TryGetValue("__name__", out name) || !(name is string)) {
                            name = "";
                        }
                        _members[obj] = res = new IronPythonModule(this, mod, (string)name);
                    }

                    PythonType type = obj as PythonType;
                    if (type != null) {
                        _members[obj] = res = GetTypeFromType(type);
                    }

                    BuiltinFunction func = obj as BuiltinFunction;
                    if (func != null) {
                        _members[obj] = res = new IronPythonBuiltinFunction(this, func);
                    }

                    BuiltinMethodDescriptor methodDesc = obj as BuiltinMethodDescriptor;
                    if (methodDesc != null) {
                        _members[obj] = res = new IronPythonBuiltinMethodDescriptor(this, methodDesc);
                    }

                    ReflectedField field = obj as ReflectedField;
                    if (field != null) {
                        return new IronPythonField(this, field);
                    }

                    ReflectedProperty prop = obj as ReflectedProperty;
                    if (prop != null) {
                        _members[obj] = res = new IronPythonProperty(this, prop);
                    }

                    ReflectedExtensionProperty extProp = obj as ReflectedExtensionProperty;
                    if (extProp != null) {
                        _members[obj] = res = new IronPythonExtensionProperty(this, extProp);
                    }

                    NamespaceTracker ns = obj as NamespaceTracker;
                    if (ns != null) {
                        _members[obj] = res = new IronPythonNamespace(this, ns);
                    }

                    Method method = obj as Method;
                    if (method != null) {
                        _members[obj] = res = new IronPythonGenericMember(this, method, PythonMemberType.Method);
                    }

                    var classMethod = obj as ClassMethodDescriptor;
                    if (classMethod != null) {
                        _members[obj] = res = new IronPythonGenericMember(this, classMethod, PythonMemberType.Method);
                    }

                    var typeSlot = obj as PythonTypeTypeSlot;
                    if (typeSlot != null) {
                        _members[obj] = res = new IronPythonGenericMember(this, typeSlot, PythonMemberType.Property);
                    }

                    ReflectedEvent eventObj = obj as ReflectedEvent;
                    if (eventObj != null) {
                        return new IronPythonEvent(this, eventObj);
                    }

                    if (res == null) {
                        var genericTypeSlot = obj as PythonTypeSlot;
                        if (genericTypeSlot != null) {
                            _members[obj] = res = new IronPythonGenericMember(this, genericTypeSlot, PythonMemberType.Property);
                        }
                    }

                    TypeGroup tg = obj as TypeGroup;
                    if (tg != null) {
                        _members[obj] = res = new IronPythonTypeGroup(this, tg);
                    }

                    var attrType = (obj != null) ? obj.GetType() : typeof(DynamicNull);
                    if (attrType == typeof(bool) || attrType == typeof(int) || attrType == typeof(Complex) ||
                        attrType == typeof(string) || attrType == typeof(long) || attrType == typeof(double) ||
                        attrType.IsEnum || obj == null) {
                        _members[obj] = res = new IronPythonConstant(this, obj);
                    }

                    if (res == null) {
                        Debug.Assert(!(obj is bool));
                        _members[obj] = res = new PythonObject<object>(this, obj);
                    }
                }

                return res;
            }
        }

        #region IDotNetPythonInterpreter Members

        public IPythonType GetBuiltinType(Type type) {
            return GetTypeFromType(type);
        }

        #endregion

        internal void LoadNewTypeDb() {
            _typeDb = new PythonTypeDatabase(_factory.GetConfiguredDatabasePath(), false, (IronPythonBuiltinModule)_modules["__builtin__"]);
        }
    }
}