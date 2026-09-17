using System;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace UltraClick {
    static class Native {
        [StructLayout(LayoutKind.Sequential)] internal struct Mouse { internal int x,y; internal uint data,flags,time; internal UIntPtr extra; }
        [StructLayout(LayoutKind.Explicit)] internal struct Union { [FieldOffset(0)] internal Mouse mouse; }
        [StructLayout(LayoutKind.Sequential)] internal struct Input { internal uint type; internal Union data; }
        [DllImport("user32.dll")] internal static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);
        [DllImport("user32.dll")] internal static extern void mouse_event(uint flags,uint x,uint y,uint data,UIntPtr extra);
        internal delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll", SetLastError=true, ExactSpelling=true)] internal static extern uint SendInput(uint n, [In] Input[] inputs, int size);
        [DllImport("kernel32.dll")] static extern void SetLastError(uint error);
        internal static long Failures;
        internal static int LastError;
        internal static string LastFailure="없음";
        internal static void ValidateLayout() {
            if(Marshal.SizeOf(typeof(Input))!=(IntPtr.Size==8?40:28) ||
               Marshal.SizeOf(typeof(Mouse))!=(IntPtr.Size==8?32:24) ||
               Marshal.OffsetOf(typeof(Input),"data").ToInt32()!=(IntPtr.Size==8?8:4) ||
               Marshal.OffsetOf(typeof(Mouse),"extra").ToInt32()!=(IntPtr.Size==8?24:20))
                throw new InvalidOperationException("INPUT/MOUSEINPUT ABI mismatch");
        }
        [DllImport("user32.dll", SetLastError=true)] internal static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint thread);
        [DllImport("user32.dll")] internal static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] internal static extern IntPtr CallNextHookEx(IntPtr hook,int code,IntPtr msg,IntPtr data);
        [DllImport("kernel32.dll")] internal static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] internal static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] internal static extern bool SetForegroundWindow(IntPtr window);
        [DllImport("user32.dll")] internal static extern bool PostMessage(IntPtr hwnd,int msg,IntPtr w,IntPtr l);
        [DllImport("winmm.dll")] internal static extern uint timeBeginPeriod(uint ms);
        [DllImport("winmm.dll")] internal static extern uint timeEndPeriod(uint ms);
        internal static uint SendBatch(Input[] items,int count) {
            if(count<1 || count>items.Length)throw new ArgumentOutOfRangeException("count");
            for(int i=0;i<count;i++) {
                Mouse m=items[i].data.mouse;
                if(items[i].type!=0 || (m.flags!=2 && m.flags!=4) || m.x!=0 || m.y!=0 || m.data!=0 || m.time!=0 || m.extra!=UIntPtr.Zero)
                    throw new InvalidOperationException("Only plain left mouse down/up is permitted");
            }
            SetLastError(0);
            uint sent=SendInput((uint)count,items,Marshal.SizeOf(typeof(Input)));
            int error=Marshal.GetLastWin32Error(); // Capture before any other interop/logging.
            if(sent!=(uint)count) {
                Interlocked.Increment(ref Failures); LastError=error;
                LastFailure="SendInput "+sent+"/"+count+"; Win32="+error;
                try {
                    string folder=System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"UltraClick");
                    System.IO.Directory.CreateDirectory(folder);
                    System.IO.File.AppendAllText(System.IO.Path.Combine(folder,"errors.log"),DateTime.Now.ToString("o")+" "+LastFailure+Environment.NewLine);
                } catch { }
            }
            return sent;
        }
        internal static bool Send(uint flags) {
            Input item = new Input(); item.data.mouse.flags = flags;
            return SendInput(1,new Input[]{item},Marshal.SizeOf(typeof(Input))) == 1;
        }
    }
    // Both the hook and polling feed one edge detector, avoiding autorepeat/double toggles.
    sealed class KeyEdges {
        bool f8, esc;
        internal int Update(int key,bool down) {
            bool old = key == 119 ? f8 : esc;
            if(key == 119) f8 = down; else esc = down;
            return down && !old ? (key == 119 ? 1 : 2) : 0;
        }
    }
    // Every active iteration blocks for at least 1 ms. No spin or catch-up backlog.
    sealed class ClickEngine : IDisposable {
        readonly object gate=new object();
        readonly AutoResetEvent wake=new AutoResetEvent(false);
        readonly ManualResetEvent cancel=new ManualResetEvent(true);
        readonly Func<Native.Input[],int,uint> send;
        readonly Func<IntPtr> foreground;
        readonly Thread worker;
        readonly Native.Input[] batch=new Native.Input[256];
        readonly Native.Input[] down=new Native.Input[1], up=new Native.Input[1];
        bool active,quitting,game=true;
        int preset,targetCps,pulse,generation;
        IntPtr target;
        long count,waits;
        string error;
        internal static readonly int[] Batches={8,32,64,128};
        internal static readonly int[] Rests={8,4,2,1};
        internal static readonly int[] Caps={1000,8000,32000,100000};
        internal ClickEngine(Func<Native.Input[],int,uint> sender,Func<IntPtr> getForeground) {
            send=sender; foreground=getForeground;
            for(int i=0;i<batch.Length;i++)batch[i].data.mouse.flags=(uint)(i%2==0?2:4);
            down[0].data.mouse.flags=2; up[0].data.mouse.flags=4;
            worker=new Thread(Run){IsBackground=true,Name="UltraClick paced input",Priority=ThreadPriority.BelowNormal}; worker.Start();
        }
        internal bool Active { get { lock(gate)return active; } }
        internal string Error { get { lock(gate)return error; } }
        internal long Count { get { return Interlocked.Read(ref count); } }
        internal long Waits { get { return Interlocked.Read(ref waits); } }
        internal bool Alive { get { return worker.IsAlive; } }
        internal void Configure(bool mode,int performance,int cps,int hold) {
            if(performance<0 || performance>3 || cps<0 || cps>1000000 || hold<0 || hold>100)throw new ArgumentOutOfRangeException();
            lock(gate) { if(active)throw new InvalidOperationException("Pause first"); game=mode;preset=performance;targetCps=cps;pulse=hold; }
        }
        internal void Start(IntPtr window) { lock(gate) { if(quitting)return; target=window;error=null;active=true;generation++;cancel.Reset(); } wake.Set(); }
        internal void Stop() { lock(gate) { active=false;generation++;cancel.Set(); } }
        void Fail(string reason) { lock(gate) { active=false;generation++;error=reason;cancel.Set(); } }
        void Rest(int ms) { Interlocked.Increment(ref waits);cancel.WaitOne(Math.Max(1,ms)); }
        void Run() {
            Stopwatch clock=Stopwatch.StartNew(); double next=0;int previous=-1;
            try {
                while(true) {
                    bool mode,run;int p,cps,hold,version;IntPtr window;
                    lock(gate) { if(quitting)return;run=active;mode=game;p=preset;cps=targetCps;hold=pulse;window=target;version=generation; }
                    if(!run) { wake.WaitOne();continue; }
                    if(previous!=version) { next=clock.Elapsed.TotalMilliseconds;previous=version; }
                    if(mode && (window==IntPtr.Zero || foreground()!=window)) { Fail("게임 창 포커스 변경: 자동 정지. 게임에서 다시 시작하세요.");continue; }
                    double now=clock.Elapsed.TotalMilliseconds;
                    if(now<next) { Rest((int)Math.Min(50,Math.Ceiling(next-now)));continue; }
                    lock(gate) { if(!active || generation!=version)continue; }
                    int rate=cps==0?Caps[p]:Math.Min(cps,Caps[p]);
                    int clicks=Math.Max(1,Math.Min(Batches[p],(int)Math.Ceiling(rate*Rests[p]/1000.0)));
                    double started=clock.Elapsed.TotalMilliseconds;
                    if(mode && hold>0) {
                        bool ok=false,released=false;
                        try { ok=send(down,1)==1;if(ok)Rest(hold); }
                        finally { if(ok)released=send(up,1)==1; }
                        if(!ok || !released) { Fail("입력 전송 실패. 실행 권한을 확인하세요.");continue; }
                        Interlocked.Increment(ref count);clicks=1;
                    } else {
                        uint sent=send(batch,clicks*2);
                        Interlocked.Add(ref count,(long)(sent/2));
                        if(sent!=(uint)(clicks*2)) {
                            // A partial odd batch may leave a down event; best-effort release then stop.
                            if((sent&1)!=0 && send(up,1)==1)Interlocked.Increment(ref count);
                            Fail("입력 일부/전체 전송 실패: 자동 정지. 실행 권한을 확인하세요.");continue;
                        }
                    }
                    double finished=clock.Elapsed.TotalMilliseconds;
                    // At least as much blocking time as work time (bounded 50 ms slices),
                    // in addition to preset rest and the requested rate. Never repay missed clicks.
                    double rest=Math.Max(Rests[p],Math.Ceiling(finished-started));
                    if(mode && hold>0)rest=Math.Max(rest,hold);
                    next=Math.Max(started+clicks*1000.0/rate,finished+rest);
                    Rest((int)Math.Min(50,Math.Ceiling(next-finished)));
                }
            } catch(Exception ex) { Fail("클릭 엔진 오류: "+ex.Message); }
        }
        public void Dispose() {
            lock(gate){if(quitting)return;quitting=true;active=false;generation++;cancel.Set();}wake.Set();
            if(worker.Join(1000)){wake.Dispose();cancel.Dispose();}
        }
    }

    sealed class MainForm : Form {
        const int Command=0x8001;
        readonly ClickEngine engine;
        readonly ComboBox mode=new ComboBox(), speed=new ComboBox(), pulse=new ComboBox();
        readonly Label state=new Label(), info=new Label(), inputs=new Label();
        readonly Button toggle=new Button();
        readonly NumericUpDown targetCps=new NumericUpDown(); readonly Label meter=new Label(), load=new Label(); readonly Stopwatch meterClock=Stopwatch.StartNew(); long meterCount; int uiTicks;
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        readonly KeyEdges edges=new KeyEdges();
        readonly bool test;
        readonly bool hookTest;
        Form receiver;
        Native.HookProc callback;
        IntPtr hook, mouseHook; Native.HookProc mouseCallback;
        bool toggleOn, holdOn, f8Armed, holdArmed, closing;
        internal MainForm(bool mock, bool integration) {
            test=mock; hookTest=integration;
            engine=new ClickEngine(mock ? (Func<Native.Input[],int,uint>)(delegate(Native.Input[] items,int n){return (uint)n;}) : Native.SendBatch,Native.GetForegroundWindow);
            Text="UltraClick 5 · Verified Mouse Input"; ClientSize=new Size(600,630);
            Font=new Font("Malgun Gothic",10); FormBorderStyle=FormBorderStyle.FixedSingle; MaximizeBox=false;
            StartPosition=FormStartPosition.CenterScreen;
            state.SetBounds(22,16,485,45); state.Font=new Font(Font.FontFamily,22,FontStyle.Bold);
            AddChoice(mode,75,new object[]{"Game Mode · 현재 시점 / 조준점","Normal · 현재 커서 위치"});
            AddChoice(speed,115,new object[]{"Balanced · 낮은 CPU","Fast · 중간 부하","Ultra · 높은 처리량","Maximum · 제한된 최대 처리량"});
            AddChoice(pulse,155,new object[]{"고속 burst · 게임 인식 여부 확인 필요","게임 누름/뗌 각각 20 ms (기본)","게임 누름/뗌 각각 40 ms","게임 누름/뗌 각각 10 ms","게임 누름/뗌 각각 1 ms"});
            pulse.SelectedIndex=1;
            Button live=new Button{Text="실제 좌클릭 진단 창"}; live.SetBounds(22,580,250,32);
            live.Click+=delegate { if(!engine.Active)new LiveReceiver().Show(); }; Controls.Add(live);
            toggle.SetBounds(22,250,235,38); toggle.Click+=delegate{Toggle();};
            Button exit=new Button{Text="종료 (ESC)"}; exit.SetBounds(270,250,235,38); exit.Click+=delegate{Close();};
            inputs.SetBounds(22,295,550,30); info.SetBounds(22,425,550,120);
            Label targetLabel=new Label{Text="Target CPS (0 = 프리셋 최대)"}; targetLabel.SetBounds(22,205,270,30); targetCps.SetBounds(310,202,195,30); targetCps.Maximum=1000000; targetCps.ThousandsSeparator=true; meter.SetBounds(22,335,550,30); load.SetBounds(22,372,550,48); Controls.AddRange(new Control[]{targetLabel,targetCps,meter,load}); Controls.AddRange(new Control[]{state,toggle,exit,inputs,info});
            timer.Interval=15; timer.Tick+=delegate { uiTicks++;
                if(!test || hookTest) { SetHold((Native.GetAsyncKeyState(5)&0x8000)!=0); }
                UpdateStatus();
            };
            UpdateStatus();
        }
        void AddChoice(ComboBox box,int y,object[] items) {
            box.SetBounds(22,y,483,30); box.DropDownStyle=ComboBoxStyle.DropDownList; box.Items.AddRange(items); box.SelectedIndex=0; Controls.Add(box);
        }
        protected override void OnShown(EventArgs e) {
            base.OnShown(e);
            f8Armed=(Native.GetAsyncKeyState(119)&0x8000)==0; holdArmed=(Native.GetAsyncKeyState(5)&0x8000)==0;
            if(!test || hookTest) {
                callback=Hook;
                hook=Native.SetWindowsHookEx(13,callback,Native.GetModuleHandle(null),0);
                if(hook==IntPtr.Zero) { Program.Failed=true; MessageBox.Show("전역 키보드 훅 설치 실패. 안전을 위해 시작하지 않습니다."); Close(); return; }
            }
                        if(!test || hookTest) {
                mouseCallback=MouseHook;
                mouseHook=Native.SetWindowsHookEx(14,mouseCallback,Native.GetModuleHandle(null),0);
                if(mouseHook==IntPtr.Zero) { Program.Failed=true; Close(); return; }
            }
            timer.Start();
            if(hookTest) { BeginInvoke((Action)HookIntegration); return; }
            if(test) BeginInvoke((Action)delegate {
                try {
                                        if(engine.Active || engine.Count!=0 || toggleOn || holdOn)throw new Exception("Startup must be paused");
                    f8Armed=false;
                    if(KeyCommand(119,true)!=0)throw new Exception("Startup F8 held");
                    KeyCommand(119,false);
                    if(KeyCommand(119,true)!=1 || KeyCommand(119,true)!=0)throw new Exception("F8 edge");
                    KeyCommand(119,false);
                    holdArmed=false; SetHold(true);
                    if(engine.Active || holdOn)throw new Exception("Startup side button held");
                    SetHold(false);
                    mode.SelectedIndex=1;
                    Dispatch(1); if(!engine.Active)throw new Exception("F8 start");
                    Dispatch(1); if(engine.Active)throw new Exception("F8 stop");
                                        foreach(int index in new int[]{0,1,2,3}) {
                        speed.SelectedIndex=index;
                        SetHold(true); if(!engine.Active || toggleOn || !holdOn)throw new Exception("Hold alone");
                        SetHold(false); if(engine.Active)throw new Exception("Hold release");
                        Dispatch(1); SetHold(true); SetHold(false);
                        if(!engine.Active || !toggleOn)throw new Exception("Toggle survives release");
                        SetHold(true); Dispatch(1);
                        if(!engine.Active || toggleOn || !holdOn)throw new Exception("Hold survives toggle off");
                        SetHold(false); if(engine.Active)throw new Exception("Both off");
                    }
                    BeginUiLoadTest();
                } catch(Exception ex) { System.IO.File.WriteAllText("self-test-error.txt",ex.ToString()); Program.Failed=true; Close(); }
            });
        }
        void BeginUiLoadTest() {
            speed.SelectedIndex=3;targetCps.Value=1000000;Dispatch(1);int before=uiTicks;
            System.Windows.Forms.Timer finish=new System.Windows.Forms.Timer{Interval=2200};
            finish.Tick+=delegate {
                finish.Stop();finish.Dispose();
                try {
                    if(uiTicks-before<30 || engine.Count==0 || meter.Text=="Sent CPS: 0")throw new Exception("UI responsiveness / Sent CPS");
                    using(Bitmap bitmap=new Bitmap(Width,Height)) { DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));bitmap.Save("ui-preview.png"); }
                    Dispatch(1);if(engine.Active)throw new Exception("UI stop under load");
                    System.IO.File.WriteAllText("ui-test-results.txt","PASS: startup OFF, F8 edges, hold/toggle combinations in all presets, UI timer ticks under Maximum load: "+(uiTicks-before)+"; "+meter.Text+"; stop; ESC close dispatch.");
                    Dispatch(2);
                } catch(Exception ex){Program.Failed=true;System.IO.File.WriteAllText("self-test-error.txt",ex.ToString());Close();}
            };finish.Start();
        }
        void HookIntegration() {
            receiver=new Form { Text="UltraClick background hotkey test", Size=new Size(300,100) };
            receiver.Show(); receiver.Activate(); Native.SetForegroundWindow(receiver.Handle);
            int step=0;
            System.Windows.Forms.Timer sequence=new System.Windows.Forms.Timer { Interval=120 };
            sequence.Tick+=delegate {
                try {
                    switch(step++) {
                        case 0: if(Native.GetForegroundWindow()!=receiver.Handle)throw new Exception("Test target not foreground"); Native.keybd_event(119,0,0,UIntPtr.Zero); break;
                        case 1: Native.keybd_event(119,0,2,UIntPtr.Zero); if(!engine.Active)throw new Exception("Background F8 start"); break;
                        case 2: if(!engine.Active || engine.Count==0)throw new Exception("Repeat toggled or no mock input"); Native.keybd_event(119,0,0,UIntPtr.Zero); break;
                        case 3: Native.keybd_event(119,0,2,UIntPtr.Zero); if(engine.Active)throw new Exception("Background F8 stop"); break;
                        case 4: Native.mouse_event(0x80,0,0,1,UIntPtr.Zero); break;
                        case 5: if(!engine.Active || !holdOn || toggleOn)throw new Exception("Background Hold start"); Native.mouse_event(0x100,0,0,1,UIntPtr.Zero); break;
                        case 6: if(engine.Active || holdOn)throw new Exception("Background Hold stop"); Native.keybd_event(119,0,0,UIntPtr.Zero); break;
                        case 7: Native.keybd_event(119,0,2,UIntPtr.Zero); Native.mouse_event(0x80,0,0,1,UIntPtr.Zero); break;
                        case 8: if(!engine.Active || !holdOn || !toggleOn)throw new Exception("Background both on"); Native.mouse_event(0x100,0,0,1,UIntPtr.Zero); break;
                        case 9: if(!engine.Active || holdOn || !toggleOn)throw new Exception("Background toggle survives release"); Native.keybd_event(27,0,0,UIntPtr.Zero); Native.keybd_event(27,0,2,UIntPtr.Zero); break;
                        case 10: throw new Exception("Background ESC failed");
                    }
                } catch(Exception ex) { Program.Failed=true; System.IO.File.WriteAllText("hook-test-error.txt",ex.ToString()); sequence.Stop(); Close(); }
            };
            FormClosed+=delegate { sequence.Stop(); sequence.Dispose(); receiver.Close(); };
            sequence.Start();
        }
        IntPtr Hook(int code,IntPtr message,IntPtr data) {
            if(code>=0) {
                int key=Marshal.ReadInt32(data), msg=message.ToInt32();
                if(key==119 || key==27) {
                    bool down=msg==0x100 || msg==0x104;
                    bool up=msg==0x101 || msg==0x105;
                    if(down || up) {
                        int command=KeyCommand(key,down);
                        if(command!=0) Native.PostMessage(Handle,Command,(IntPtr)command,IntPtr.Zero);
                        return (IntPtr)1; // Consume our shortcuts: do not trigger target-app key actions/beeps.
                    }
                }
            }
            return Native.CallNextHookEx(hook,code,message,data);
        }
        int KeyCommand(int key,bool down) {
            if(key==119 && !f8Armed) { if(!down)f8Armed=true; return 0; }
            return edges.Update(key,down);
        }
        IntPtr MouseHook(int code,IntPtr message,IntPtr data) {
            // Generated left clicks must never change hold state. Ignore all injected mouse events.
            if(code>=0 && (Marshal.ReadInt32(data,12)&1)!=0)return Native.CallNextHookEx(mouseHook,code,message,data);
            if(code>=0 && (message.ToInt32()==0x20B || message.ToInt32()==0x20C)) {
                int mouseData=Marshal.ReadInt32(data,8);
                if(((mouseData>>16)&0xffff)==1) {
                    Native.PostMessage(Handle,Command,(IntPtr)(message.ToInt32()==0x20B?3:4),IntPtr.Zero);
                    return (IntPtr)1;
                }
            }
            return Native.CallNextHookEx(mouseHook,code,message,data);
        }
        void Poll(int key) { int command=KeyCommand(key,(Native.GetAsyncKeyState(key)&0x8000)!=0); if(command!=0)Dispatch(command); }
        void Dispatch(int command) { if(command==1)Toggle(); else if(command==2)Close(); else if(command==3)SetHold(true); else if(command==4)SetHold(false); }
        protected override void WndProc(ref Message m) { if(m.Msg==Command) { Dispatch(m.WParam.ToInt32()); return; } base.WndProc(ref m); }
        void Toggle() {
            if(closing)return;
            toggleOn=!toggleOn;
            ApplyActivation();
        }
        void SetHold(bool down) {
            if(closing)return;
            if(!holdArmed) { if(!down)holdArmed=true; return; }
            if(holdOn==down)return;
            holdOn=down;
            ApplyActivation();
        }
        void ApplyActivation() {
            
            if(!toggleOn && !holdOn) engine.Stop();
            else if(!engine.Active) {
                bool game=mode.SelectedIndex==0;
                IntPtr target=Native.GetForegroundWindow();
                if(game && target==Handle) { toggleOn=holdOn=false; holdArmed=false; UpdateStatus(); return; }
                engine.Configure(game,speed.SelectedIndex,(int)targetCps.Value,new int[]{0,20,40,10,1}[pulse.SelectedIndex]);
                engine.Start(target);
            }
            UpdateStatus();
        }
        void UpdateStatus() {
            bool active=engine.Active;
            if(!active && engine.Error!=null && (toggleOn || holdOn)) { toggleOn=holdOn=false; holdArmed=false; }
            state.Text=active ? "● ON · 클릭 중" : "● OFF · 일시정지";
            state.ForeColor=active ? Color.SeaGreen : Color.DimGray;
            inputs.Text="F8 Toggle: "+(toggleOn ? "ON" : "OFF")+"     |     XButton1 Hold: "+(holdOn ? "ON" : "OFF");
            toggle.Text=toggleOn ? "일시정지 (F8)" : "시작 (F8)";
            mode.Enabled=speed.Enabled=pulse.Enabled=targetCps.Enabled=!active;
            if(meterClock.ElapsedMilliseconds>=1000) { long current=engine.Count; meter.Text="Sent CPS: "+((current-meterCount)/meterClock.Elapsed.TotalSeconds).ToString("N0")+"   |   전송 클릭: "+current.ToString("N0"); meterCount=current;meterClock.Restart(); }
            if(meter.Text.Length==0)meter.Text="Sent CPS: 0";
            int p=speed.SelectedIndex; load.Text="Burst 최대 "+ClickEngine.Batches[p]+"클릭 / 매회 최소 "+ClickEngine.Rests[p]+"ms 대기 / 상한 "+ClickEngine.Caps[p].ToString("N0")+" CPS\n예상 부하: "+new string[]{"낮음","중간","중간~높음","높음 (대기 보장)"}[p]+" · CPU % 고정 제한은 아닙니다.";
            info.Text=engine.Error ?? "F8: 토글 · 측면 뒤로가기 버튼: 누르는 동안 클릭 · ESC: 종료\n게임 창에서 F8로 시작. 창 전환 시 게임 모드는 자동 정지.\nGame Mode는 커서를 이동하지 않습니다. Sent CPS는 게임 인식량이 아닙니다.";
            info.Text+="\nSendInput 실패: "+Interlocked.Read(ref Native.Failures)+" | 마지막 Win32: "+Native.LastError+"\n"+Native.LastFailure;
        }
        protected override void OnFormClosed(FormClosedEventArgs e) {
            closing=true; toggleOn=holdOn=false; engine.Stop(); timer.Stop(); timer.Dispose();
            if(mouseHook!=IntPtr.Zero) { Native.UnhookWindowsHookEx(mouseHook); mouseHook=IntPtr.Zero; }
            if(hook!=IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook=IntPtr.Zero; }
            engine.Dispose(); base.OnFormClosed(e); GC.KeepAlive(callback); GC.KeepAlive(mouseCallback);
        }
    }
    static class Program {
        internal static bool Failed;
        [STAThread] static int Main(string[] args) {
            bool integration=Array.IndexOf(args,"--hook-test")>=0;
            bool test=Array.IndexOf(args,"--self-test")>=0 || integration,created;
            using(Mutex mutex=new Mutex(true,"Local\\UltraClick.SingleInstance",out created)) {
                if(!created)return 2;
                bool timing=Native.timeBeginPeriod(1)==0;
                try {
                    Native.ValidateLayout();
                    if(test || Array.IndexOf(args,"--engine-test")>=0) Tests(); if(Array.IndexOf(args,"--engine-test")>=0)return 0;
                    Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm(test,integration)); return Failed ? 1 : 0;
                } catch(Exception ex) { if(test)System.IO.File.WriteAllText("self-test-error.txt",ex.ToString()); else MessageBox.Show(ex.Message); return 1; }
                finally { if(timing)Native.timeEndPeriod(1); mutex.ReleaseMutex(); }
            }
        }
        static void Assert(bool condition,string message) { if(!condition)throw new Exception(message); }
        static void Tests() {
            KeyEdges keys=new KeyEdges();
            Assert(keys.Update(119,true)==1 && keys.Update(119,true)==0,"F8 repeat");
            keys.Update(119,false); Assert(keys.Update(119,true)==1,"F8 next edge");
            Assert(keys.Update(27,true)==2 && keys.Update(27,true)==0,"ESC repeat");
            EngineTests.Run();
        }
    }
}




