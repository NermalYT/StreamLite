using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamLite.Audio
{
    /// <summary>
    /// Minimal ONNX Runtime binding through its C API (onnxruntime.dll from Microsoft.ML.OnnxRuntime.DirectML 1.24.4),
    /// so no managed package is needed. Function-table indexes are from onnxruntime_c_api.h (ORT_API_VERSION 24).
    /// Runs on the CPU or, with DirectML, on the graphics card.
    /// </summary>
    internal sealed class OnnxSession : IDisposable
    {
        const int ApiVersion = 24;
#if LINUX
        const string LibName = "libonnxruntime.so";
        static IntPtr LoadLibraryW(string path) { IntPtr h; return NativeLibrary.TryLoad(path, out h) ? h : IntPtr.Zero; }
        static IntPtr GetProcAddress(IntPtr mod, string name) { return NativeLibrary.GetExport(mod, name); }
#else
        const string LibName = "onnxruntime.dll";
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)] static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32", CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr mod, string name);
#endif

        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr GetApiFn(uint version);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr ErrMsgFn(IntPtr status);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CreateEnvFn(int level, [MarshalAs(UnmanagedType.LPStr)] string id, out IntPtr env);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr OutPtrFn(out IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr PtrIntFn(IntPtr p, int v);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr PtrFn(IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate void ReleaseFn(IntPtr p);
#if LINUX
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CreateSessionFn(IntPtr env, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr opts, out IntPtr session);   // ORTCHAR_T = char
#else
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CreateSessionFn(IntPtr env, [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr opts, out IntPtr session);   // ORTCHAR_T = wchar_t
#endif
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CountFn(IntPtr session, out UIntPtr n);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr NameFn(IntPtr session, UIntPtr i, IntPtr alloc, out IntPtr name);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr TypeInfoFn(IntPtr session, UIntPtr i, out IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CastFn(IntPtr typeInfo, out IntPtr tensorInfo);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr DimsFn(IntPtr info, [Out] long[] dims, UIntPtr n);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr CpuInfoFn(int allocType, int memType, out IntPtr info);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr TensorFn(IntPtr info, IntPtr data, UIntPtr bytes, long[] shape, UIntPtr rank, int type, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr RunFn(IntPtr session, IntPtr runOpts, IntPtr[] inNames, IntPtr[] inputs, UIntPtr nIn, IntPtr[] outNames, UIntPtr nOut, [In, Out] IntPtr[] outputs);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr AllocFreeFn(IntPtr alloc, IntPtr p);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr DmlFn(IntPtr opts, int device);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr ConfigFn(IntPtr opts, [MarshalAs(UnmanagedType.LPStr)] string key, [MarshalAs(UnmanagedType.LPStr)] string value);
        [UnmanagedFunctionPointer(CallingConvention.Winapi)] delegate IntPtr EpApiFn([MarshalAs(UnmanagedType.LPStr)] string name, uint version, out IntPtr api);

        static IntPtr lib, api;
        static ErrMsgFn errMsg;
        static ReleaseFn releaseStatus, releaseValue, releaseSession, releaseOptions, releaseEnv, releaseMemInfo, releaseTypeInfo;
        static RunFn run;
        static TensorFn createTensor;
        static PtrFn getData0;
        static IntPtr env, cpuInfo;
        static readonly object gate = new object();

        static T Fn<T>(int index) where T : class
        {
            IntPtr p = Marshal.ReadIntPtr(api, index * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(p, typeof(T));
        }

        static void Check(IntPtr status)
        {
            if (status == IntPtr.Zero) return;
            string msg = Marshal.PtrToStringAnsi(errMsg(status));
            releaseStatus(status);
            throw new InvalidOperationException("ONNX Runtime: " + msg);
        }

        /// <summary>Folder holding onnxruntime.dll, DirectML.dll and the models (next to the exe, in "ai").</summary>
        public static string Folder
        {
            get
            {
                string env = Environment.GetEnvironmentVariable("STREAMLITE_AI");
                if (!string.IsNullOrEmpty(env)) return env;
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ai");
            }
        }

        /// <summary>The ONNX Runtime library: in the models folder, or (Linux packages) next to the program.</summary>
        static string LibPath
        {
            get
            {
                string p = Path.Combine(Folder, LibName);
                return File.Exists(p) ? p : Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LibName);
            }
        }

        public static bool Available { get { return File.Exists(LibPath); } }

        static void Init()
        {
            lock (gate)
            {
                if (api != IntPtr.Zero) return;
                // DirectML.dll first, from our folder (the one in System32 is older than ONNX Runtime needs).
                string dml = Path.Combine(Folder, "DirectML.dll");
                if (File.Exists(dml)) LoadLibraryW(dml);
                lib = LoadLibraryW(LibPath);
                if (lib == IntPtr.Zero) throw new DllNotFoundException(LibName + " could not be loaded (error " + Marshal.GetLastWin32Error() + ").");
                IntPtr baseP = GetProcAddress(lib, "OrtGetApiBase");
                var getBase = (PtrFn)Marshal.GetDelegateForFunctionPointer(baseP, typeof(PtrFn));
                IntPtr apiBase = getBase(IntPtr.Zero);   // OrtGetApiBase takes no arguments; the extra one is ignored on x64
                var getApi = (GetApiFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(apiBase, 0), typeof(GetApiFn));
                api = getApi(ApiVersion);
                if (api == IntPtr.Zero) throw new InvalidOperationException("This onnxruntime.dll doesn't support API version " + ApiVersion + ".");
                errMsg = Fn<ErrMsgFn>(2);
                releaseStatus = Fn<ReleaseFn>(93);
                releaseEnv = Fn<ReleaseFn>(92);
                releaseMemInfo = Fn<ReleaseFn>(94);
                releaseSession = Fn<ReleaseFn>(95);
                releaseValue = Fn<ReleaseFn>(96);
                releaseTypeInfo = Fn<ReleaseFn>(98);
                releaseOptions = Fn<ReleaseFn>(100);
                run = Fn<RunFn>(9);
                createTensor = Fn<TensorFn>(49);
                IntPtr e;
                Check(Fn<CreateEnvFn>(3)(3, "StreamLite", out e));   // 3 = errors only
                env = e;
                IntPtr ci;
                Check(Fn<CpuInfoFn>(69)(0, 0, out ci));              // device allocator, default memory
                cpuInfo = ci;
            }
        }

        IntPtr session;
        public readonly string[] InputNames, OutputNames;
        public readonly long[][] InputShapes, OutputShapes;
        readonly IntPtr[] inNamePtrs, outNamePtrs;
        public readonly bool OnGpu;

        /// <param name="gpu">true = DirectML on the graphics card (device 0), false = CPU (one thread).</param>
        /// <param name="fuse">false = DirectML runs node by node (for graphs its fusion can't take).</param>
        public OnnxSession(string modelPath, bool gpu, int cpuThreads = 1, bool fuse = true)
        {
            Init();
            IntPtr opts;
            Check(Fn<OutPtrFn>(10)(out opts));
            try
            {
                Check(Fn<PtrIntFn>(23)(opts, 99));               // all graph optimizations
                Check(Fn<PtrIntFn>(13)(opts, 0));                // sequential
                if (gpu)
                {
                    T("mempattern"); Check(Fn<PtrFn>(17)(opts));                  // DirectML needs memory patterns off
                    if (!fuse) Check(Fn<ConfigFn>(130)(opts, "ep.dml.disable_graph_fusion", "1"));   // AddSessionConfigEntry
                    IntPtr dmlApi; T("getapi");
                    Check(Fn<EpApiFn>(195)("DML", ApiVersion, out dmlApi));   // GetExecutionProviderApi -> OrtDmlApi
                    if (dmlApi == IntPtr.Zero) throw new InvalidOperationException("This onnxruntime.dll has no DirectML support.");
                    var append = (DmlFn)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(dmlApi, 0), typeof(DmlFn));
                    T("append " + dmlApi.ToString("X")); Check(append(opts, 0)); T("appended");
                }
                else Check(Fn<PtrIntFn>(24)(opts, cpuThreads));
                IntPtr s; T("create");
                Check(Fn<CreateSessionFn>(7)(env, modelPath, opts, out s));
                session = s;
                OnGpu = gpu;
            }
            finally { releaseOptions(opts); }

            IntPtr alloc; Check(Fn<OutPtrFn>(78)(out alloc));
            var free = Fn<AllocFreeFn>(76);
            UIntPtr n;
            Check(Fn<CountFn>(30)(session, out n)); int nIn = (int)n;
            Check(Fn<CountFn>(31)(session, out n)); int nOut = (int)n;
            InputNames = new string[nIn]; OutputNames = new string[nOut];
            InputShapes = new long[nIn][]; OutputShapes = new long[nOut][];
            var getIn = Fn<NameFn>(36); var getOut = Fn<NameFn>(37);
            var inInfo = Fn<TypeInfoFn>(33); var outInfo = Fn<TypeInfoFn>(34);
            for (int i = 0; i < nIn; i++) { IntPtr p; Check(getIn(session, (UIntPtr)i, alloc, out p)); InputNames[i] = Marshal.PtrToStringAnsi(p); free(alloc, p); InputShapes[i] = Shape(inInfo, i); }
            for (int i = 0; i < nOut; i++) { IntPtr p; Check(getOut(session, (UIntPtr)i, alloc, out p)); OutputNames[i] = Marshal.PtrToStringAnsi(p); free(alloc, p); OutputShapes[i] = Shape(outInfo, i); }
            inNamePtrs = new IntPtr[nIn]; outNamePtrs = new IntPtr[nOut];
            for (int i = 0; i < nIn; i++) inNamePtrs[i] = Ansi(InputNames[i]);
            for (int i = 0; i < nOut; i++) outNamePtrs[i] = Ansi(OutputNames[i]);
        }

        long[] Shape(TypeInfoFn get, int i)
        {
            IntPtr ti; Check(get(session, (UIntPtr)i, out ti));
            try
            {
                IntPtr tensor; Check(Fn<CastFn>(55)(ti, out tensor));
                UIntPtr rank; Check(Fn<CountFn>(61)(tensor, out rank));
                var dims = new long[(int)rank];
                if (dims.Length > 0) Check(Fn<DimsFn>(62)(tensor, dims, rank));
                return dims;
            }
            finally { releaseTypeInfo(ti); }
        }

        static void T(string m) { if (Environment.GetEnvironmentVariable("STREAMLITE_ONNX_TRACE") != null) Console.Error.WriteLine("[onnx] " + m); }

        static IntPtr Ansi(string s)
        {
            var b = Encoding.UTF8.GetBytes(s + "\0");
            IntPtr p = Marshal.AllocHGlobal(b.Length);
            Marshal.Copy(b, 0, p, b.Length);
            return p;
        }

        /// <summary>
        /// One inference. inputs[i] are pinned float arrays shaped like InputShapes[i] (shapes given explicitly);
        /// outputs[i] receive the results (must be at least as long as the output).
        /// </summary>
        public void Run(float[][] inputs, long[][] shapes, float[][] outputs) { Run((Array[])inputs, shapes, (Array[])outputs); }

        static int ElemType(Array a, out int size)
        {
            if (a is float[]) { size = 4; return 1; }
            if (a is byte[]) { size = 1; return 2; }
            if (a is long[]) { size = 8; return 7; }
            if (a is int[]) { size = 4; return 6; }
            throw new ArgumentException("Unsupported tensor element type " + a.GetType());
        }

        static void CopyOut(IntPtr src, Array dst, int count)
        {
            if (dst is float[]) Marshal.Copy(src, (float[])dst, 0, count);
            else if (dst is byte[]) Marshal.Copy(src, (byte[])dst, 0, count);
            else if (dst is long[]) Marshal.Copy(src, (long[])dst, 0, count);
            else if (dst is int[]) Marshal.Copy(src, (int[])dst, 0, count);
        }

        /// <summary>One inference with float / byte / long / int tensors (types follow the arrays).</summary>
        public void Run(Array[] inputs, long[][] shapes, Array[] outputs)
        {
            int nIn = inputs.Length;
            var handles = new GCHandle[nIn];
            var values = new IntPtr[nIn];
            var outs = new IntPtr[outNamePtrs.Length];
            try
            {
                for (int i = 0; i < nIn; i++)
                {
                    handles[i] = GCHandle.Alloc(inputs[i], GCHandleType.Pinned);
                    IntPtr v;
                    int size, type = ElemType(inputs[i], out size);
                    Check(createTensor(cpuInfo, handles[i].AddrOfPinnedObject(), (UIntPtr)(inputs[i].Length * size), shapes[i], (UIntPtr)shapes[i].Length, type, out v));
                    values[i] = v;
                }
                Check(run(session, IntPtr.Zero, inNamePtrs, values, (UIntPtr)nIn, outNamePtrs, (UIntPtr)outs.Length, outs));
                var getData = Fn<CastFn>(51);
                var count = Fn<CountFn>(64); var shapeOf = Fn<CastFn>(65); var relShape = Fn<ReleaseFn>(99);
                for (int i = 0; i < outs.Length && i < outputs.Length; i++)
                {
                    if (outputs[i] == null) continue;
                    IntPtr ts; Check(shapeOf(outs[i], out ts));
                    UIntPtr cnt; Check(count(ts, out cnt)); relShape(ts);
                    IntPtr data; Check(getData(outs[i], out data));
                    CopyOut(data, outputs[i], Math.Min(outputs[i].Length, (int)cnt));
                }
            }
            finally
            {
                for (int i = 0; i < nIn; i++) { if (values[i] != IntPtr.Zero) releaseValue(values[i]); if (handles[i].IsAllocated) handles[i].Free(); }
                foreach (var o in outs) if (o != IntPtr.Zero) releaseValue(o);
            }
        }

        public void Dispose()
        {
            if (session != IntPtr.Zero) { releaseSession(session); session = IntPtr.Zero; }
            if (inNamePtrs != null) foreach (var p in inNamePtrs) Marshal.FreeHGlobal(p);
            if (outNamePtrs != null) foreach (var p in outNamePtrs) Marshal.FreeHGlobal(p);
        }
    }
}
