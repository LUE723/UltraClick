using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;
namespace UltraClick {
    // Counts actual Windows mouse messages and Button.Click, never calls PerformClick.
    sealed class CountButton : Button {
        internal int Downs,Ups,Clicks,Other;
        protected override void WndProc(ref Message m) {
            if(m.Msg==0x201 || m.Msg==0x203)Downs++;
            if(m.Msg==0x202)Ups++;
            if(m.Msg==0x204 || m.Msg==0x207 || m.Msg==0x20B || m.Msg==0x100)Other++;
            base.WndProc(ref m);
        }
        protected override void OnClick(EventArgs e) { Clicks++;base.OnClick(e); }
        internal void ResetCounts() { Downs=Ups=Clicks=Other=0; }
    }
    sealed class LiveReceiver : Form {
        readonly CountButton pad=new CountButton();
        readonly Label status=new Label();
        readonly Button run=new Button();
        readonly Timer timer=new Timer{Interval=100};
        ClickEngine engine;
        int stage=-1,ticks;
        bool settling;
        string report="Real Windows input receiver; no mock sender.\r\n";
        internal LiveReceiver() {
            Text="UltraClick Real Input Test";ClientSize=new Size(680,430);StartPosition=FormStartPosition.CenterScreen;
            pad.Text="실제 좌클릭 수신 영역\nF8로 시작/정지하여 수신량 확인";pad.SetBounds(25,25,630,220);
            run.Text="실제 SendInput 자동 검사 (약 12초)";run.SetBounds(25,265,630,40);
            status.SetBounds(25,320,630,100);Controls.AddRange(new Control[]{pad,run,status});
            run.Click+=delegate {
                if(engine!=null)return;
                run.Enabled=false;report+="INPUT="+Marshal.SizeOf(typeof(Native.Input))+", MOUSEINPUT="+Marshal.SizeOf(typeof(Native.Mouse))+", unionOffset="+Marshal.OffsetOf(typeof(Native.Input),"data")+", extraOffset="+Marshal.OffsetOf(typeof(Native.Mouse),"extra")+"\r\n";
                engine=new ClickEngine(delegate(Native.Input[] items,int n) {
                    // Abort instead of sending clicks outside this disposable test surface.
                    if(Native.GetForegroundWindow()!=Handle || !pad.RectangleToScreen(pad.ClientRectangle).Contains(Cursor.Position))return 0;
                    return Native.SendBatch(items,n);
                },Native.GetForegroundWindow);
                stage=0;BeginStage();
            };
            timer.Tick+=delegate {
                status.Text="Received: down="+pad.Downs+", up="+pad.Ups+", Button.Click="+pad.Clicks+", other="+pad.Other;
                if(engine==null)return;
                if(++ticks<7)return;
                if(!settling){engine.Stop();settling=true;ticks=5;return;}
                long sent=engine.Count;
                bool ok=pad.Downs>0 && pad.Downs==pad.Ups && pad.Clicks==pad.Ups && pad.Other==0 && engine.Error==null;
                report+=(ok?"PASS":"FAIL")+" stage="+stage+" mode="+(stage<4?"Normal":"Game")+" preset="+(stage%4)+" received down="+pad.Downs+" up="+pad.Ups+" clicks="+pad.Clicks+" other="+pad.Other+" cumulativeSent="+sent+" error="+engine.Error+"\r\n";
                if(!ok || ++stage==9) { Finish();return; }
                BeginStage();
            };timer.Start();
            FormClosed+=delegate { timer.Stop();timer.Dispose();if(engine!=null)engine.Dispose(); };
        }
        void BeginStage() {
            pad.ResetCounts();ticks=0;settling=false;
            engine.Configure(stage>=4,stage%4,100,stage==8?20:0);
            Cursor.Position=pad.PointToScreen(new Point(pad.Width/2,pad.Height/2));
            engine.Start(Handle);
        }
        void Finish() {
            engine.Dispose();engine=null;run.Enabled=true;
            string path=Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"live-test-results.txt");
            File.WriteAllText(path,report);pad.Text="검사 완료 · 결과 파일 저장됨\n"+report.Substring(Math.Max(0,report.Length-250));
        }
    }
    static class LiveTestEntry {
        [STAThread] static void Main() { Application.EnableVisualStyles();Application.Run(new LiveReceiver()); }
    }
}
