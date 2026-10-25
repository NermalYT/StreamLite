using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace StreamLite.Interop
{
    /// <summary>
    /// Writes audio straight into a WaveRT driver's render pin through kernel streaming (the way WDM-KS pro-audio hosts
    /// do), bypassing the Windows endpoint. That lets StreamLite feed the virtual mic cable while its playback endpoint
    /// ("StreamLite - Mic Feed") is disabled - so it no longer shows up as an output in Windows, Discord or games.
    /// Needs administrator rights only to disable the endpoint; the kernel-streaming part works for any user.
    /// </summary>
    internal sealed class KsRender : IDisposable
    {
        [StructLayout(LayoutKind.Sequential)] struct SP_DEVICE_INTERFACE_DATA { public int cbSize; public Guid InterfaceClassGuid; public int Flags; public IntPtr Reserved; }
        [DllImport("setupapi.dll", SetLastError = true)] static extern IntPtr SetupDiGetClassDevs(ref Guid cls, IntPtr enumerator, IntPtr hwnd, int flags);
        [DllImport("setupapi.dll", SetLastError = true)] static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr devInfo, ref Guid cls, int index, ref SP_DEVICE_INTERFACE_DATA data);
        [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref SP_DEVICE_INTERFACE_DATA data, IntPtr detail, int size, out int required, IntPtr devInfo);
        [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr sec, uint disp, uint flags, IntPtr tmpl);
        [DllImport("kernel32.dll", SetLastError = true)] static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, int inSize, IntPtr outBuf, int outSize, out int ret, IntPtr ov);
        [DllImport("ksuser.dll")] static extern int KsCreatePin(SafeFileHandle filter, IntPtr connect, uint access, out IntPtr pin);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateWaitableTimerExW(IntPtr sec, string name, uint flags, uint access);
        [DllImport("kernel32.dll")] static extern bool SetWaitableTimer(IntPtr timer, ref long due, int period, IntPtr cb, IntPtr arg, bool resume);
        [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h, uint ms);

        static readonly Guid KSCATEGORY_RENDER = new Guid("65E8773E-8F56-11D0-A3B9-00A0C9223196");
        static readonly Guid KSPROPSETID_Pin = new Guid("8C134960-51AD-11CF-878A-94F801C10000");
        static readonly Guid KSPROPSETID_Connection = new Guid("1D58C920-AC9B-11CF-A5D6-28DB04C10000");
        static readonly Guid KSPROPSETID_RtAudio = new Guid("A855A48C-2F78-4729-9051-1968746B9EEF");
        static readonly Guid KSPROPSETID_Audio = new Guid("45FFAAA0-6E1B-11D0-BCF2-444553540000");
        static readonly Guid KSINTERFACESETID_Standard = new Guid("1A8766A0-62CE-11CF-A5D6-28DB04C10000");
        static readonly Guid KSMEDIUMSETID_Standard = new Guid("4747B320-62CE-11CF-A5D6-28DB04C10000");
        static readonly Guid KSDATAFORMAT_TYPE_AUDIO = new Guid("73647561-0000-0010-8000-00aa00389b71");
        static readonly Guid KSDATAFORMAT_SUBTYPE_PCM = new Guid("00000001-0000-0010-8000-00aa00389b71");
        static readonly Guid KSDATAFORMAT_SUBTYPE_FLOAT = new Guid("00000003-0000-0010-8000-00aa00389b71");
        static readonly Guid KSDATAFORMAT_SPECIFIER_WAVEFORMATEX = new Guid("05589f81-c356-11ce-bf01-00aa0055595a");
        const uint IOCTL_KS_PROPERTY = 0x002F0003;
        const int KSPROPERTY_TYPE_GET = 1, KSPROPERTY_TYPE_SET = 2;

        SafeFileHandle filter;
        IntPtr pin = IntPtr.Zero;
        SafeFileHandle pinHandle;
        IntPtr buffer; int bufBytes;
        public int Rate, Channels, Bits; public bool Float;
        public string Path, Error;
        int frameBytes;

        /// <summary>Kernel-streaming render filters whose path contains the text (e.g. "vbaudiovac" + "out").</summary>
        public static List<string> RenderFilters()
        {
            var r = new List<string>();
            var cls = KSCATEGORY_RENDER;
            IntPtr set = SetupDiGetClassDevs(ref cls, IntPtr.Zero, IntPtr.Zero, 0x12);
            var d = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA)) };
            for (int i = 0; SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref cls, i, ref d); i++)
            {
                int need; SetupDiGetDeviceInterfaceDetail(set, ref d, IntPtr.Zero, 0, out need, IntPtr.Zero);
                IntPtr buf = Marshal.AllocHGlobal(need);
                Marshal.WriteInt32(buf, 8);
                if (SetupDiGetDeviceInterfaceDetail(set, ref d, buf, need, out need, IntPtr.Zero)) r.Add(Marshal.PtrToStringUni(buf + 4));
                Marshal.FreeHGlobal(buf);
            }
            SetupDiDestroyDeviceInfoList(set);
            return r;
        }

        bool Prop(SafeFileHandle h, Guid set, int id, int flags, byte[] extraIn, IntPtr outBuf, int outSize, out int got)
        {
            int inSize = 24 + (extraIn != null ? extraIn.Length : 0);
            IntPtr inb = Marshal.AllocHGlobal(inSize);
            try
            {
                Marshal.Copy(set.ToByteArray(), 0, inb, 16);
                Marshal.WriteInt32(inb, 16, id); Marshal.WriteInt32(inb, 20, flags);
                if (extraIn != null) Marshal.Copy(extraIn, 0, inb + 24, extraIn.Length);
                return DeviceIoControl(h, IOCTL_KS_PROPERTY, inb, inSize, outBuf, outSize, out got, IntPtr.Zero);
            }
            finally { Marshal.FreeHGlobal(inb); }
        }

        /// <summary>Opens the filter's render (sink) pin with the first format the driver accepts and maps its buffer.</summary>
        public bool Open(string filterPath, int rate, int bufferMs)
        {
            Path = filterPath;
            filter = CreateFile(filterPath, 0xC0000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (filter.IsInvalid) { Error = "Can't open the cable driver (error " + Marshal.GetLastWin32Error() + ")."; return false; }
            int pinId = FindSinkPin();
            if (pinId < 0) { Error = "The cable has no streaming input pin."; return false; }
            // Float first (no conversion), then 32/24/16-bit integer; stereo at the engine's rate.
            int[][] tries = { new[] { 32, 1, 32 }, new[] { 32, 0, 32 }, new[] { 32, 0, 24 }, new[] { 24, 0, 24 }, new[] { 16, 0, 16 } };
            int err = 0;
            foreach (var t in tries)
            {
                err = CreatePin(pinId, rate, 2, t[0], t[1] == 1, t[2]);
                if (err == 0) { Rate = rate; Channels = 2; Bits = t[0]; Float = t[1] == 1; break; }
            }
            if (pin == IntPtr.Zero) { Error = "The cable refused every format (error " + err + ")."; return false; }
            pinHandle = new SafeFileHandle(pin, true);
            frameBytes = Channels * Bits / 8;
            // KSPROPERTY_RTAUDIO_BUFFER: the driver maps its cyclic buffer into our process.
            var extra = new byte[16];
            BitConverter.GetBytes((long)0).CopyTo(extra, 0);
            BitConverter.GetBytes(rate * frameBytes * bufferMs / 1000).CopyTo(extra, 8);
            IntPtr outb = Marshal.AllocHGlobal(16);
            try
            {
                int got;
                if (!Prop(pinHandle, KSPROPSETID_RtAudio, 1, KSPROPERTY_TYPE_GET, extra, outb, 16, out got))
                { Error = "The cable didn't give a WaveRT buffer (error " + Marshal.GetLastWin32Error() + ")."; return false; }
                buffer = Marshal.ReadIntPtr(outb, 0);
                bufBytes = Marshal.ReadInt32(outb, 8);
            }
            finally { Marshal.FreeHGlobal(outb); }
            if (buffer == IntPtr.Zero || bufBytes < frameBytes * 64) { Error = "The cable's buffer is unusable."; return false; }
            for (int i = 0; i < bufBytes; i += 4) Marshal.WriteInt32(buffer, i, 0);
            return true;
        }

        int FindSinkPin()
        {
            IntPtr outb = Marshal.AllocHGlobal(8);
            try
            {
                int got;
                if (!Prop(filter, KSPROPSETID_Pin, 1, KSPROPERTY_TYPE_GET, null, outb, 4, out got)) return -1;   // CTYPES
                int pins = Marshal.ReadInt32(outb);
                for (int p = 0; p < pins; p++)
                {
                    var pinIn = new byte[8]; BitConverter.GetBytes(p).CopyTo(pinIn, 0);
                    if (!Prop(filter, KSPROPSETID_Pin, 2, KSPROPERTY_TYPE_GET, pinIn, outb, 4, out got)) continue;     // DATAFLOW
                    int flow = Marshal.ReadInt32(outb);
                    if (!Prop(filter, KSPROPSETID_Pin, 7, KSPROPERTY_TYPE_GET, pinIn, outb, 4, out got)) continue;     // COMMUNICATION
                    int comm = Marshal.ReadInt32(outb);
                    if (flow == 1 && (comm == 1 || comm == 3)) return p;   // KSPIN_DATAFLOW_IN, KSPIN_COMMUNICATION_SINK/BOTH
                }
                return -1;
            }
            finally { Marshal.FreeHGlobal(outb); }
        }

        int CreatePin(int pinId, int rate, int ch, int bits, bool flt, int valid)
        {
            const int connectSize = 72, fmtSize = 64 + 40;
            IntPtr c = Marshal.AllocHGlobal(connectSize + fmtSize);
            try
            {
                for (int i = 0; i < connectSize + fmtSize; i += 4) Marshal.WriteInt32(c, i, 0);
                Marshal.Copy(KSINTERFACESETID_Standard.ToByteArray(), 0, c, 16);
                Marshal.WriteInt32(c, 16, 1);                                     // KSINTERFACE_STANDARD_LOOPED_STREAMING
                Marshal.Copy(KSMEDIUMSETID_Standard.ToByteArray(), 0, c + 24, 16); // KSMEDIUM_TYPE_ANYINSTANCE
                Marshal.WriteInt32(c, 48, pinId);
                Marshal.WriteInt32(c, 64, 0x40000000); Marshal.WriteInt32(c, 68, 1);   // KSPRIORITY_NORMAL
                IntPtr f = c + connectSize;                                         // KSDATAFORMAT_WAVEFORMATEXTENSIBLE
                int block = ch * bits / 8;
                Marshal.WriteInt32(f, 0, fmtSize);
                Marshal.WriteInt32(f, 8, block);
                Marshal.Copy(KSDATAFORMAT_TYPE_AUDIO.ToByteArray(), 0, f + 16, 16);
                Marshal.Copy((flt ? KSDATAFORMAT_SUBTYPE_FLOAT : KSDATAFORMAT_SUBTYPE_PCM).ToByteArray(), 0, f + 32, 16);
                Marshal.Copy(KSDATAFORMAT_SPECIFIER_WAVEFORMATEX.ToByteArray(), 0, f + 48, 16);
                IntPtr w = f + 64;
                Marshal.WriteInt16(w, 0, unchecked((short)0xFFFE));                 // WAVE_FORMAT_EXTENSIBLE
                Marshal.WriteInt16(w, 2, (short)ch);
                Marshal.WriteInt32(w, 4, rate);
                Marshal.WriteInt32(w, 8, rate * block);
                Marshal.WriteInt16(w, 12, (short)block);
                Marshal.WriteInt16(w, 14, (short)bits);
                Marshal.WriteInt16(w, 16, 22);
                Marshal.WriteInt16(w, 18, (short)valid);
                Marshal.WriteInt32(w, 20, 3);                                       // front left + right
                Marshal.Copy((flt ? KSDATAFORMAT_SUBTYPE_FLOAT : KSDATAFORMAT_SUBTYPE_PCM).ToByteArray(), 0, w + 24, 16);
                IntPtr h;
                int r = KsCreatePin(filter, c, 0xC0000000, out h);
                if (r == 0) pin = h;
                return r;
            }
            finally { Marshal.FreeHGlobal(c); }
        }

        bool SetState(int state)
        {
            IntPtr v = Marshal.AllocHGlobal(4);
            try
            {
                Marshal.WriteInt32(v, state);
                // KSPROPERTY_CONNECTION_STATE takes the state as the property's value (written, not read).
                int inSize = 24; IntPtr inb = Marshal.AllocHGlobal(inSize);
                try
                {
                    Marshal.Copy(KSPROPSETID_Connection.ToByteArray(), 0, inb, 16);
                    Marshal.WriteInt32(inb, 16, 0); Marshal.WriteInt32(inb, 20, KSPROPERTY_TYPE_SET);
                    int got;
                    return DeviceIoControl(pinHandle, IOCTL_KS_PROPERTY, inb, inSize, v, 4, out got, IntPtr.Zero);
                }
                finally { Marshal.FreeHGlobal(inb); }
            }
            finally { Marshal.FreeHGlobal(v); }
        }

        /// <summary>Current play position in the cyclic buffer (bytes), via KSPROPERTY_AUDIO_POSITION.</summary>
        long PlayPosition()
        {
            IntPtr outb = Marshal.AllocHGlobal(16);
            try
            {
                int got;
                if (!Prop(pinHandle, KSPROPSETID_Audio, 5, KSPROPERTY_TYPE_GET, null, outb, 16, out got)) return -1;
                return Marshal.ReadInt64(outb, 0);
            }
            finally { Marshal.FreeHGlobal(outb); }
        }

        public volatile bool Stop;
        public long FramesWritten;

        /// <summary>Tests: runs the pin (playing the zeroed buffer, i.e. silence) and records how the driver's play
        /// position moves - each change's size in frames and the time since the previous change.</summary>
        public List<double[]> TracePosition(int ms)
        {
            var steps = new List<double[]>();
            if (!SetState(1) || !SetState(2) || !SetState(3)) { Error = "state"; return steps; }
            try
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long last = PlayPosition(); double lastT = 0;
                while (sw.ElapsedMilliseconds < ms)
                {
                    long p = PlayPosition();
                    if (p != last)
                    {
                        double t = sw.Elapsed.TotalMilliseconds;
                        steps.Add(new[] { ((p - last + bufBytes) % bufBytes) / (double)frameBytes, t - lastT });
                        last = p; lastT = t;
                    }
                    Thread.SpinWait(200);
                }
            }
            finally { SetState(2); SetState(1); SetState(0); }
            return steps;
        }

        /// <summary>Minimum lead the safety timer keeps (ms; grows by itself after a catch-up, settles back when calm).</summary>
        public volatile float LeadMs;
        /// <summary>Average distance between what's written and what the cable plays (ms) - the cable's share of the delay.</summary>
        public volatile float AheadMs;
        /// <summary>Times the cable caught up with the writer (each adds 0.5 ms of lead).</summary>
        public int CatchUps;
        /// <summary>Frames the source has of real sound right now (null = always enough).</summary>
        public Func<int> Available;
        /// <summary>
        /// With Available: the drift between the mic's clock and the cable's, steered from the cable's lead (the one
        /// place the safety margin is kept) - the source reads that much faster (+) or slower (-).
        /// </summary>
        public Action<float> Trim;
        readonly LeadSteer steer = new LeadSteer();
        const int TraceLen = 16;
        readonly int[] traceTick = new int[TraceLen], traceAhead = new int[TraceLen], traceFill = new int[TraceLen], traceCritical = new int[TraceLen];
        readonly float[] traceTrim = new float[TraceLen];
        int traceNext;
        /// <summary>The last fill-ins the cable writer had to make (newest last), for status.txt.</summary>
        public string FillTrace
        {
            get
            {
                lock (writeGate)
                {
                    var sb = new System.Text.StringBuilder();
                    int n = Math.Min(traceNext, 6);
                    for (int i = traceNext - n; i < traceNext; i++)
                    {
                        int k = i % TraceLen;
                        sb.Append((Environment.TickCount - traceTick[k]) / 1000.0 >= 0 ? ((Environment.TickCount - traceTick[k]) / 1000.0).ToString("0.000") + " s ago: lead " + traceAhead[k] + "/" + traceCritical[k] + ", filled " + traceFill[k] + ", trim " + (traceTrim[k] * 1e6).ToString("0") + " ppm; " : "");
                    }
                    return sb.ToString();
                }
            }
        }
        public int Fills { get { return traceNext; } }
        /// <summary>The mic's clock against the cable's, as the steering learned it (ppm; start value from an earlier run).</summary>
        public double DriftPpm { get { return steer.DriftPpm; } set { steer.DriftPpm = value; } }

        /// <summary>
        /// How much to write: what's wanted, but no more than the real sound there is - unless the cable is closer than
        /// critical to playing past what's written: then enough to get back there (the source fills in).
        /// </summary>
        internal static int FramesToWrite(int want, int ready, int ahead, int critical)
        {
            if (want <= ready) return want;
            int must = ahead < critical ? critical - ahead : 0;
            return Math.Min(want, Math.Max(ready, must));
        }

        /// <summary>
        /// Drift steering from the cable's lead: just before each mic packet goes in the lead is at its lowest; the
        /// lowest of 40 packets (120 ms) is steered to Target by reading the mic's sound up to 0.3% faster or slower.
        /// Proportional plus integral: the integral learns the clocks' actual difference (a USB mic against VB-CABLE's
        /// system clock can be 1000 ppm and more), so the lead sits on target instead of below it by however much the
        /// proportional part alone would need to push that hard.
        /// </summary>
        internal sealed class LeadSteer
        {
            public int Target;
            public float Trim;
            double integral;
            int low = int.MaxValue, n;
            bool filled;
            /// <summary>The writer had to fill in: the lead was short, most likely for another reason than the clocks (a
            /// late packet, the cable jumping ahead).</summary>
            public void NoteFill() { filled = true; }
            /// <summary>The clocks' difference learned so far (ppm); set it from an earlier run to start right.</summary>
            public double DriftPpm { get { return integral * 1e6; } set { integral = Math.Max(-0.003, Math.Min(0.003, value / 1e6)); Trim = (float)integral; } }
            /// <summary>The lead before a packet (frames); true when Trim was just updated.</summary>
            public bool BeforePacket(int ahead)
            {
                if (ahead < low) low = ahead;
                if (++n < 40) return false;
                double err = low - Target;
                // A window with fill-ins teaches it only a quarter as much: a one-off (a late packet, the cable jumping
                // ahead) hardly winds it up, while clocks that really are far apart still get learned.
                integral = Math.Max(-0.003, Math.Min(0.003, integral + err / 1200 * 0.003 * (filled ? 0.25 : 1)));
                filled = false;
                Trim = (float)Math.Max(-0.003, Math.Min(0.003, integral + err / 240 * 0.003));
                low = int.MaxValue; n = 0;
                return true;
            }
        }

        readonly object writeGate = new object();
        Action<float[], int> source;
        float[] mono; byte[] bytes;
        long write = -1;
        int lead, baseLead, maxLead, lastCatchUp, startTick;
        volatile bool running;

        int BytesFor(float ms) { return Math.Max(frameBytes, (int)(Rate * ms / 1000) * frameBytes); }

        /// <summary>
        /// Tops the cable up so that targetMs of audio sits ahead of its play position (or the safety lead, whichever
        /// is more). Called by the mic thread right after each packet - so a packet reaches the cable at once instead
        /// of waiting for a timer - and by the safety timer. Thread-safe.
        /// </summary>
        public void Kick(float targetMs) { Top(BytesFor(targetMs), 0); }

        /// <summary>
        /// The mic thread, right after a packet of packetMs: tops up to one packet beyond the safety lead (+0.5 ms, at
        /// least 1.5 ms) - so just before the next packet there is still more than the lead queued and the safety timer
        /// never has to step in (it would drain the small ring ahead of the packet).
        /// </summary>
        public void KickPacket(float packetMs) { Top(0, BytesFor(packetMs)); }

        void Top(int targetBytes, int packetBytes)
        {
            lock (writeGate)
            {
                if (!running) return;
                if (packetBytes > 0) targetBytes = packetBytes + Math.Max(lead + BytesFor(0.5f), BytesFor(1.5f));
                long play = PlayPosition();
                if (play < 0) return;
                play %= bufBytes;
                // Start at the lead the writer keeps (the one-buffer path: at the steering target, and only once there's
                // sound - a lead set up before the first packet would drain away and the start would run short).
                if (write < 0 && Available != null && Available() < 16) return;
                if (write < 0) write = (play + lead + (Available != null ? BytesFor(1f) : 0)) % bufBytes;
                long ahead = (write - play + bufBytes) % bufBytes;
                if (ahead > bufBytes - frameBytes * 32)
                {
                    // Fell behind (the cable played what we hadn't written yet): resync, and keep more lead from now on -
                    // except in the first seconds, while the mic and everything else are still starting up.
                    if (Environment.TickCount - startTick > 3000)
                    {
                        if (lead < maxLead) { lead = Math.Min(maxLead, lead + BytesFor(0.5f)); LeadMs = lead / frameBytes * 1000f / Rate; }
                        CatchUps++; lastCatchUp = Environment.TickCount;
                    }
                    write = (play + lead) % bufBytes; ahead = lead;
                }
                else if (lead > baseLead && Environment.TickCount - lastCatchUp > 20000)
                {
                    // 20 calm seconds: give back some of the extra lead.
                    lead = Math.Max(baseLead, lead - BytesFor(0.25f)); LeadMs = lead / frameBytes * 1000f / Rate; lastCatchUp = Environment.TickCount;
                }
                long target = Math.Max(lead, Math.Min(bufBytes / 2, targetBytes));
                long need = target - ahead;
                int frames = (int)(need / frameBytes);
                // Only real sound: while the mic is late the cable's own lead rides it out. Just before the cable would
                // play past what's written (its position moves up to 1.5 ms at once), the source fills in.
                var avail = Available;
                int critical = (int)(Math.Max(BytesFor(1.5f), lead) / frameBytes);   // the safety lead: two position jumps can fall between checks
                var trim = Trim;
                if (avail != null && trim != null && packetBytes > 0)
                {
                    // Just before a packet's sound goes in, the lead is at its lowest: steer that to just above critical.
                    steer.Target = critical + 24;
                    if (steer.BeforePacket((int)(ahead / frameBytes))) trim(steer.Trim);
                }
                if (avail != null)
                {
                    // After a packet all of its sound goes in (the cable's lead is the one buffer, steered above); in
                    // between, only what keeps the cable from playing past what's written.
                    int ready = avail(), cap = (int)(bufBytes / 2 / frameBytes);
                    frames = FramesToWrite(packetBytes > 0 ? Math.Min(cap, Math.Max(frames, ready)) : frames, ready, (int)(ahead / frameBytes), critical);
                    if (frames > ready && frames >= 16)
                    {
                        steer.NoteFill();
                        // Trace of fill-ins (status.txt): when, the lead then, how much, the drift steering.
                        int k = traceNext++ % TraceLen;
                        traceTick[k] = Environment.TickCount; traceAhead[k] = (int)(ahead / frameBytes); traceFill[k] = frames - ready; traceTrim[k] = steer.Trim; traceCritical[k] = critical;
                    }
                }
                if (frames >= 16)
                {
                    source(mono, frames);
                    Encode(mono, frames, bytes);
                    int n = frames * frameBytes, first = (int)Math.Min(n, bufBytes - write);
                    Marshal.Copy(bytes, 0, buffer + (int)write, first);
                    if (n > first) Marshal.Copy(bytes, first, buffer, n - first);
                    write = (write + n) % bufBytes;
                    FramesWritten += frames;
                    float ms = (ahead + (ahead + n)) / 2f / frameBytes * 1000f / Rate;
                    AheadMs = AheadMs == 0 ? ms : AheadMs * 0.98f + ms * 0.02f;
                }
            }
        }

        /// <summary>
        /// Streams until Stop: a safety timer (every 0.5 ms) keeps at least leadMs written ahead of the play position,
        /// filling from read(dst, frames) with mono samples (copied to both channels); Kick tops up sooner. VB-CABLE moves
        /// its play position in ~1 ms steps (never more than 1.5 ms apart). If the cable ever catches up, the lead grows by
        /// 0.5 ms (up to maxLeadMs). Returns false if the driver stops cooperating.
        /// </summary>
        public bool Run(Action<float[], int> read, float leadMs, float maxLeadMs = 10)
        {
            if (!SetState(1) || !SetState(2)) { Error = "The cable didn't accept the stream (state)."; return false; }   // ACQUIRE, PAUSE
            lock (writeGate)
            {
                source = read;
                baseLead = lead = Math.Min(Math.Max(frameBytes * 48, BytesFor(leadMs)), bufBytes / 2 / frameBytes * frameBytes);
                maxLead = Math.Min(BytesFor(maxLeadMs), bufBytes / 2 / frameBytes * frameBytes);
                LeadMs = lead / frameBytes * 1000f / Rate;
                mono = new float[bufBytes / frameBytes];
                bytes = new byte[bufBytes];
                write = -1; lastCatchUp = startTick = Environment.TickCount;
            }
            if (!SetState(3)) { Error = "The cable didn't start (state)."; return false; }   // RUN
            running = true;
            IntPtr timer = CreateWaitableTimerExW(IntPtr.Zero, null, 0x2, 0x1F0003);         // high resolution
            long lastPlay = -1; int stalls = 0;
            try
            {
                while (!Stop)
                {
                    long due = -5000;    // 0.5 ms
                    if (timer != IntPtr.Zero) { SetWaitableTimer(timer, ref due, 0, IntPtr.Zero, IntPtr.Zero, false); WaitForSingleObject(timer, 20); }
                    else Thread.Sleep(1);
                    long play = PlayPosition();
                    if (play < 0) { Error = "Lost the cable's play position."; return false; }
                    if (play == lastPlay) { if (++stalls > 800) { Error = "The cable stopped playing."; return false; } } else stalls = 0;
                    lastPlay = play;
                    Kick(0);
                }
                return true;
            }
            finally
            {
                lock (writeGate) running = false;
                if (timer != IntPtr.Zero) CloseHandle(timer);
                SetState(2); SetState(1); SetState(0);
            }
        }

        void Encode(float[] mono, int frames, byte[] o)
        {
            int bps = Bits / 8;
            for (int i = 0; i < frames; i++)
            {
                float v = mono[i];
                if (v > 1f) v = 1f; else if (v < -1f) v = -1f;
                int at = i * frameBytes;
                if (Float)
                {
                    int bitsF = BitConverter.ToInt32(BitConverter.GetBytes(v), 0);
                    for (int c = 0; c < 2; c++) { int k = at + c * 4; o[k] = (byte)bitsF; o[k + 1] = (byte)(bitsF >> 8); o[k + 2] = (byte)(bitsF >> 16); o[k + 3] = (byte)(bitsF >> 24); }
                }
                else if (bps == 2) { short s = (short)(v * 32767); for (int c = 0; c < 2; c++) { o[at + c * 2] = (byte)s; o[at + c * 2 + 1] = (byte)(s >> 8); } }
                else if (bps == 3) { int s = (int)(v * 8388607); for (int c = 0; c < 2; c++) { int k = at + c * 3; o[k] = (byte)s; o[k + 1] = (byte)(s >> 8); o[k + 2] = (byte)(s >> 16); } }
                else { int s = (int)(v * 2147483392.0); for (int c = 0; c < 2; c++) { int k = at + c * 4; o[k] = (byte)s; o[k + 1] = (byte)(s >> 8); o[k + 2] = (byte)(s >> 16); o[k + 3] = (byte)(s >> 24); } }
            }
        }

        public void Dispose()
        {
            Stop = true;
            if (pinHandle != null) { pinHandle.Dispose(); pinHandle = null; }
            if (filter != null) { filter.Dispose(); filter = null; }
        }
    }
}
