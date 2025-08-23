using Blish_HUD.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using MouseEventArgs = Blish_HUD.Input.MouseEventArgs;

namespace Blish_HUD {
    internal static class ExternalDirectxOverlay {
        private static Thread _udpListenerThread;
        private static volatile bool _listening;
        private const int LISTENPORT = 49152;


        //-------------------------------------------- RENDERING ---------------------------------------------//
        public static MemoryMappedFile HeaderMMF = null;
        public static MemoryMappedViewAccessor HeaderAccesor = null;
        public static MemoryMappedFile BodyMMF = null;
        public static MemoryMappedViewAccessor BodyAccessor = null;
        public static int Width = 0 ;
        public static int Height = 0;
        const string HEADERMAPNAME = "BlishHUD_Header";
        const string BODYMAPNAME = "BlishHUD_Body";
        const int HEADERSIZE = 12; //2 * 4 bytes for width, height, 4 byte bool

        //Just used as a buffer to receive data from GetBackBufferData. It's here to keep globals in this one file.
        public static Color[] PixelData;
        private static Color[] _previousFrameSent;

        //Used to store the pixel data as bytes for writing to shared memory.
        private static byte[] _pixelDataBytes;

        //Events
        private static EventWaitHandle _frameReadyEvent = new EventWaitHandle(false, EventResetMode.ManualReset, "BlishHUD_FrameReady");
        private static EventWaitHandle _frameConsumedEvent = new EventWaitHandle(true, EventResetMode.ManualReset, "BlishHUD_FrameConsumed");

        //Globals
        public static bool AutoUpdatesEnabled = false;

        //Mutex the rust side can use to check if Blish is still running.
        private static Mutex _isAliveMtx = new Mutex(true, "Global\\blish_isalive_mutex");

        private static uint _prevHash = 0;
        private static bool _wasHolding = false;

        //Because for some reason I can't make it work peroperly with GameService.Overlay.InterfaceHidden
        private static volatile bool _isInterfaceHidden = false;


        /*
            Header : [ width (u32) | height (u32) | hold (4 byte bool)]
            Body: [ Full Frame ]
         */

        private static readonly object _logLock = new object();
        private static string _logPath;
        //simple logging function to write debug messages to a file
        public static void Log(string level, string message, Exception ex = null) {
            if (_logPath == null) {
                var logsDir = Path.Combine(AppContext.BaseDirectory, "..", "logs");
                Directory.CreateDirectory(logsDir);

                string timestamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
                _logPath = Path.Combine(logsDir, $"BlishHUD-{timestamp}.log");
            }

            var now = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            var logEntry = $"[{now}] [BlishHUD] [{level.ToUpper()}] {message}";

            if (ex != null) {
                logEntry += Environment.NewLine + ex + Environment.NewLine;
            }

            lock (_logLock) {
                File.AppendAllText(_logPath, logEntry + Environment.NewLine);
            }
            Console.WriteLine(logEntry);
        }

        private static readonly object _writeLock = new object();
        public static void ProcessFrame(Color[] frame) {
            EnqueueFrame(frame);
        }

        private const int MAX_QUEUE_SIZE = 3;
        private static readonly ConcurrentQueue<Color[]> _frameQueue = new ConcurrentQueue<Color[]>();
        private static readonly AutoResetEvent _frameAvailable = new AutoResetEvent(false);
        private static Thread _workerThread;
        private static volatile bool _workerRunning = false;

        public static void StartWorker() {
            if (_workerRunning) return;
            _workerRunning = true;
            _workerThread = new Thread(FrameProcessingLoop) { IsBackground = true };
            _workerThread.Start();
        }

        // Call this to stop the worker thread
        public static void StopWorker() {
            _workerRunning = false;
            _frameAvailable.Set();
            _workerThread?.Join();
        }

        // Enqueue a frame (from your render thread)
        public static void EnqueueFrame(Color[] frame) {
            if (_isInterfaceHidden) {
                return;
            }
            if (!_workerRunning) {
                StartWorker();
            }
            while (_frameQueue.Count >= MAX_QUEUE_SIZE) {
                _frameQueue.TryDequeue(out _);
            }
            _frameQueue.Enqueue(frame);
            _frameAvailable.Set();
        }

        // Worker thread: only this thread calls WriteToSharedMemory
        private static void FrameProcessingLoop() {
            while (_workerRunning) {
                if (_frameQueue.TryDequeue(out var frame)) {
                    WriteToSharedMemory(frame);
                } else {
                    _frameAvailable.WaitOne();
                }
            }
        }

        //This sends a "shutdown frame" which is really just the last frame with hold = false
        private static void SendShutdownFrame() {
            while (_frameQueue.TryDequeue(out _)) { }
            HeaderAccesor.Write(8, 0);
            _previousFrameSent = null;
            _frameConsumedEvent.Reset();
            _frameReadyEvent.Set();
        }

        public static void WriteToSharedMemory(Color[] currentFrame) {
            try {
                if (Width < 50 || Height < 50) {
                    return;
                }

                //Wait for the rust side to consume the last frame sent.
                _frameConsumedEvent.WaitOne();

                bool framesDifferent = AreFramesDifferent(currentFrame, _previousFrameSent);

                // Write width, height
                HeaderAccesor.Write(0, Width);
                HeaderAccesor.Write(4, Height);

                int hold = framesDifferent ? 0 : 1;

                HeaderAccesor.Write(8, hold);

                if (framesDifferent) {
                    // Convert to bytes for WriteToMMF
                    ComputeColorToByte(currentFrame);

                    // Write frame data
                    WriteToMMF(_pixelDataBytes);

                    // Notify the rust DLL that a new frame is ready
                    _frameConsumedEvent.Reset();
                    _frameReadyEvent.Set();

                    _wasHolding = false;
                } else if (!_wasHolding) {
                    _frameConsumedEvent.Reset();
                    _frameReadyEvent.Set();

                    _wasHolding = true;
                }

                _previousFrameSent = currentFrame;
            } catch (Exception ex) {
                Log("Error", "Failed to write the frame to MMF", ex);
            }
        }

        //Checks if 2 frames are dfferent
        private static unsafe bool AreFramesDifferent(Color[] a, Color[] b) {
            if (b == null || b.Length != a.Length || a == null || b == null)
                return true;

            uint currentHash = ComputeFrameHash(a);

            if (currentHash != _prevHash) {
                _prevHash = currentHash;
                return true;
            }

            return false;
        }

        //Could be optimized, but fast enough for now. 
        private static uint ComputeFrameHash(Color[] frame) {
            Span<uint> data = MemoryMarshal.Cast<Color, uint>(frame);

            const uint fnvPrime = 16777619;
            uint hash = 2166136261;

            foreach (var value in data) {
                hash ^= value;
                hash *= fnvPrime;
            }

            return hash;
        }


        // This is required to optimize writing frames to shared memory.
        // Buffer.MemoryCopy is apparently a wrapper for memcpy, which already uses SMID and/or parallelization internally.
        private static void WriteToMMF(byte[] bytes) {
            unsafe {
                byte* destPtr = null;
                try {
                    BodyAccessor.SafeMemoryMappedViewHandle.AcquirePointer(ref destPtr);
                    destPtr += BodyAccessor.PointerOffset;

                    fixed (byte* srcPtr = bytes) {
                        Buffer.MemoryCopy(
                            srcPtr,
                            destPtr,
                            bytes.Length,
                            bytes.Length
                        );
                    }
                } finally {
                    if (destPtr != null) {
                        BodyAccessor.SafeMemoryMappedViewHandle.ReleasePointer();
                    }
                }
            }
        }

        private static void ComputeColorToByte(Color[] frame) {
            for (int i = 0; i < frame.Length; i++) {
                var c = frame[i];
                _pixelDataBytes[i * 4 + 0] = c.R;
                _pixelDataBytes[i * 4 + 1] = c.G;
                _pixelDataBytes[i * 4 + 2] = c.B;
                _pixelDataBytes[i * 4 + 3] = c.A;
            }
            //This code sometimes hangs for some reason, eventually come back and do it properly.
            /*int length = frame.Length;
            int chunkSize = 8192;
            int numChunks = (length + chunkSize - 1) / chunkSize;

            unsafe {
                fixed (Color* colorPtr = frame)
                fixed (byte* baseDestPtr = _pixelDataBytes) {
                    Color* srcPtr = colorPtr;
                    byte* destPtr = baseDestPtr;

                    Parallel.For(0, numChunks, chunk =>
                    {
                        int start = chunk * chunkSize;
                        int end = Math.Min(start + chunkSize, length);

                        Color* src = srcPtr + start;
                        byte* dest = destPtr + (start * 4);

                        for (int i = 0; i < end - start; i++) {
                            dest[i * 4 + 0] = src[i].R;
                            dest[i * 4 + 1] = src[i].G;
                            dest[i * 4 + 2] = src[i].B;
                            dest[i * 4 + 3] = src[i].A;
                        }
                    });
                }
            }*/
        }

        //When the game is resized. This will also be called on the first frame, so we can also run our initialization code here.
        public static void Resize(int w, int h) {
            if (HeaderMMF == null) {
                initializeMMF();
            }
            Width = w;
            Height = h;
            _pixelDataBytes = new byte[w*h*4];
        }

        private static void initializeMMF() {
            //TODO: Dynamic size (why do I need 30mb...I don't...)
            //Initialization goes here, move this elsewhere later
            int totalSize = (3840 * 2160 * 4) + 4; // Max size at 3840x2160 with 4 bytes per pixel
            HeaderMMF = MemoryMappedFile.CreateOrOpen(HEADERMAPNAME, HEADERSIZE, MemoryMappedFileAccess.ReadWrite);
            BodyMMF = MemoryMappedFile.CreateOrOpen(BODYMAPNAME, totalSize, MemoryMappedFileAccess.ReadWrite);
            HeaderAccesor = HeaderMMF.CreateViewAccessor(0, HEADERSIZE, MemoryMappedFileAccess.ReadWrite);
            BodyAccessor = BodyMMF.CreateViewAccessor(0, totalSize, MemoryMappedFileAccess.ReadWrite);
            _frameConsumedEvent.Set();

            GameService.Overlay.HideAllInterface.Value.Activated += (sender, e) => {
                if (!_isInterfaceHidden) {
                    _isInterfaceHidden = true;
                    SendShutdownFrame();
                } else {
                    _isInterfaceHidden = false;
                }
            };
            Log("Debug", "BlishHUD started successfully.");
        }


        //---------------------------------------------- INPUT -----------------------------------------------//

        public static void StartUdpServer() {
            _listening = true;
            _udpListenerThread = new Thread(UdpListenLoop) {
                IsBackground = true
            };
            _udpListenerThread.Start();
        }

        //TODO: use this
        public static void StopUdpServer() {
            _listening = false;
            _udpListenerThread?.Join();
        }

        //Message format: [<EventTypeID>(4bytes), x(4bytes), y(4bytes)]
        //Send mouse events over UDP. Faster than you'd expect.
        private static void UdpListenLoop() {
            using (var udpClient = new UdpClient(LISTENPORT)) {
                var remoteEP = new IPEndPoint(IPAddress.Any, 0);
                while (_listening) {
                    try {
                        byte[] data = udpClient.Receive(ref remoteEP);
                        if (data.Length >= 9) {
                            byte eventTypeId = data[0];
                            int x = BitConverter.ToInt32(data, 1);
                            int y = BitConverter.ToInt32(data, 5);

                            //Mouse moved
                            if (eventTypeId == 2) {
                                MouseState oldState = GameService.Input.Mouse.StaticMouseState;
                                GameService.Input.Mouse.StaticMouseState = new MouseState(
                                    x,
                                    y,
                                    oldState.ScrollWheelValue,
                                    oldState.LeftButton,
                                    oldState.RightButton,
                                    oldState.MiddleButton,
                                    oldState.XButton1,
                                    oldState.XButton2
                                );
                                GameService.Input.Mouse.HandleInput(new MouseEventArgs(
                                    MouseEventType.MouseMoved,
                                    x,
                                    y,
                                    0,
                                    0,
                                    Environment.TickCount,
                                    0
                                ));
                            }
                        }
                    } catch (SocketException) {
                        Log("Error", "SocketException occurred while receiving UDP data.");
                    }
                }
            }
        }

        //--------------------------------------------- WINDOW MANAGEMENT ------------------------------------------//

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, WndProcDelegate dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")]
        static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        struct WINDOWPOS {
            public IntPtr hwnd;
            public IntPtr hwndInsertAfter;
            public int x;
            public int y;
            public int cx;
            public int cy;
            public uint flags;
        }

        const int GWL_WNDPROC = -4;
        const int WM_SHOWWINDOW = 0x0018;
        const int WM_WINDOWPOSCHANGING = 0x0046;
        const uint SWP_HIDEWINDOW = 0x0080;
        const uint SWP_SHOWWINDOW = 0x0040;

        delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        static WndProcDelegate newWndProc;
        static IntPtr originalWndProc;

        public static void setupNewWndProc() {
            IntPtr hwnd = BlishHud.Instance.FormHandle;
            originalWndProc = GetWindowLongPtr(hwnd, GWL_WNDPROC);
            newWndProc = HookWndProc;
            SetWindowLongPtr(hwnd, GWL_WNDPROC, newWndProc);
            BlishHud.Instance.Form.Visible = false;
            BlishHud.Instance.Form.Hide();
        }
        static IntPtr CallOriginalWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) {
            return CallWindowProc(originalWndProc, hWnd, msg, wParam, lParam);
        }


        //Prevents the window from ever showing up, even if Blish tries to show it.
        static IntPtr HookWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam) {
            if (msg == WM_SHOWWINDOW) {
                return IntPtr.Zero;
            } else if (msg == WM_WINDOWPOSCHANGING) {
                unsafe {
                    WINDOWPOS* pos = (WINDOWPOS*)lParam;
                    if ((pos->flags & SWP_SHOWWINDOW) != 0) {
                        pos->flags &= ~SWP_SHOWWINDOW;
                        pos->flags |= SWP_HIDEWINDOW;
                    }
                }
            }

            return CallOriginalWndProc(hWnd, msg, wParam, lParam);
        }

        //This patches audio to work on linux. Basically it stubs the methods that wine does not implement.
        //Using Harmony would be 1000% better, but it did not seem feasible due to .Net versions.

        [DllImport("kernel32")]
        private static extern bool VirtualProtect(IntPtr lpAddress, UIntPtr dwSize, uint flNewProtect, out uint lpflOldProtect);

        public static void PatchUnregisterNotifications() {
            try {
                var method = typeof(NAudio.CoreAudioApi.AudioSessionManager)
                                .GetMethod("UnregisterNotifications", BindingFlags.Instance | BindingFlags.NonPublic);
                if (method == null) return;

                //Force compile
                RuntimeHelpers.PrepareMethod(method.MethodHandle);

                IntPtr ptr = method.MethodHandle.GetFunctionPointer();

                //0xC3 = ret
                //Only on x86_64 and probably x86.
                byte[] patch = { 0xC3 };

                //Make memory writable
                VirtualProtect(ptr, (UIntPtr)patch.Length, 0x40, out uint oldProtect);

                //Patch
                Marshal.Copy(patch, 0, ptr, patch.Length);

                //Restore protect
                VirtualProtect(ptr, (UIntPtr)patch.Length, oldProtect, out _);

            } catch (Exception ex) {
                Log("Debug", "Failed to patch audio", ex);
            }
        }
    }
}