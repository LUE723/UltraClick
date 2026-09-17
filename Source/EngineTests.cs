using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
namespace UltraClick {
    static class EngineTests {
        static void Check(bool ok,string message) { if(!ok)throw new Exception(message); }
        internal static void Run() {
            using(StreamWriter log=new StreamWriter("engine-test-results.txt")) {
                log.AutoFlush=true;
                log.WriteLine("Mock sender only; no real mouse input. CPU percent is relative to ONE logical CPU.");
                for(int p=0;p<4;p++) {
                    long calls=0;
                    using(ClickEngine e=new ClickEngine(delegate(Native.Input[] b,int n){
                        Check(n<=256 && n%2==0,"Bounded batch");
                        for(int i=0;i<n;i++)Check(b[i].data.mouse.flags==(i%2==0?2u:4u),"Event order");
                        Interlocked.Increment(ref calls);return (uint)n;
                    },delegate{return (IntPtr)42;})) {
                        Thread.Sleep(40);Check(e.Count==0 && !e.Active,"Startup OFF");
                        e.Configure(false,p,1000000,0);
                        Process process=Process.GetCurrentProcess();TimeSpan cpu=process.TotalProcessorTime;
                        Stopwatch wall=Stopwatch.StartNew();e.Start((IntPtr)42);Thread.Sleep(2100);e.Stop();Thread.Sleep(30);
                        double seconds=wall.Elapsed.TotalSeconds,used=(process.TotalProcessorTime-cpu).TotalSeconds/seconds*100;
                        long stopped=e.Count;Thread.Sleep(40);Check(e.Count==stopped,"Stop settles");
                        Check(stopped>0 && stopped/seconds<=ClickEngine.Caps[p]*1.1,"CPS cap");
                        Check(e.Waits>=calls,"Blocking wait after each burst");
                        Check(used<80,"Mock CPU unexpectedly high");
                        log.WriteLine("Preset {0}: {1:F0} mock CPS; CPU {2:F2}%; batches {3}; waits {4}",p,stopped/seconds,used,calls,e.Waits);
                    }
                }
                using(ClickEngine e=new ClickEngine(delegate(Native.Input[] b,int n){return (uint)n;},delegate{return (IntPtr)42;})) {
                    e.Configure(false,3,100,0);e.Start((IntPtr)42);Thread.Sleep(1200);e.Stop();Thread.Sleep(25);
                    Check(e.Count>=70 && e.Count<=125,"Custom 100 CPS pacing");log.WriteLine("Target 100 CPS: {0} clicks in ~1.2s",e.Count);
                    e.Configure(false,3,1,0);e.Start((IntPtr)42);Thread.Sleep(50);
                    Stopwatch stop=Stopwatch.StartNew();e.Dispose();Check(!e.Alive && stop.ElapsedMilliseconds<250,"Interrupt low CPS wait on exit");
                    log.WriteLine("Exit during low-CPS wait: {0} ms",stop.ElapsedMilliseconds);
                }
                int downs=0,ups=0;IntPtr foreground=(IntPtr)42;
                using(ClickEngine e=new ClickEngine(delegate(Native.Input[] b,int n){if(b[0].data.mouse.flags==2)Interlocked.Increment(ref downs);else Interlocked.Increment(ref ups);return (uint)n;},delegate{return foreground;})) {
                    e.Configure(true,3,0,100);e.Start((IntPtr)42);Thread.Sleep(30);e.Stop();Thread.Sleep(30);
                    Check(downs>0 && downs==ups,"Release on pulse cancellation");
                    foreground=(IntPtr)43;e.Start((IntPtr)42);Thread.Sleep(30);Check(!e.Active && e.Error!=null,"Game focus stop");
                }
                int releases=0;
                using(ClickEngine e=new ClickEngine(delegate(Native.Input[] b,int n){if(n==1){releases++;return 1;}return 3;},delegate{return (IntPtr)42;})) {
                    e.Configure(false,3,0,0);e.Start((IntPtr)42);Thread.Sleep(50);
                    Check(!e.Active && e.Error!=null && releases==1 && e.Count==2,"Partial send recovery/count");
                }
                using(ClickEngine e=new ClickEngine(delegate(Native.Input[] b,int n){throw new Exception("mock failure");},delegate{return (IntPtr)42;})) {
                    e.Configure(false,3,0,0);e.Start((IntPtr)42);Thread.Sleep(30);Check(!e.Active && e.Error!=null,"Sender exception");
                }
                log.WriteLine("PASS: startup, presets, saturation, pacing, stop, exit, game focus, release, partial input, sender error.");
            }
        }
    }
}
