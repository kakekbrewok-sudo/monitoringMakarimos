using System;
using System.Globalization;
using System.IO;
using System.Text;

static class FpsChecks
{
    static readonly StringBuilder log = new StringBuilder();
    static void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL: " + text); log.AppendLine("PASS: " + text); }
    const string Header = "Application,ProcessID,SwapChainAddress,TimeInSeconds,msBetweenPresents";
    static string Row(int pid, string chain, double time, double ms)
    { return String.Format(CultureInfo.InvariantCulture, "\"Game, 日本.exe\",{0},{1},{2:R},{3:R}", pid, chain, time, ms); }
    static int Main(string[] args)
    {
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("id-ID");
            var f = new FpsFrames(42); f.Accept("startup message", 0); f.Accept(Header, 0);
            for (int i = 1; i <= 61; i++) f.Accept(Row(42,"0x1",i/60.0,1000.0/60), 1);
            Check(f.Read(1).HasValue && Math.Abs(f.Read(1).Value-60)<.01, "60 FPS parsed from event timestamps under Indonesian locale");
            for (int i=1;i<=241;i++) f.Accept(Row(99,"0x2",i/240.0,1000.0/240),1);
            Check(Math.Abs(f.Read(1).Value-60)<.01, "frames from other process IDs ignored");
            for (int i=1;i<=31;i++) f.Accept(Row(42,"0x2",i/30.0,1000.0/30),1);
            Check(Math.Abs(f.Read(1).Value-60)<.01, "swap chains are not added together");
            f.Accept(Row(42,"0x1",1,0),1); f.Accept(Row(42,"0x1",1,double.NaN),1);
            f.Accept(Row(42,"0x1",double.PositiveInfinity,16),1); f.Accept("bad,data",1);
            Check(Math.Abs(f.Read(1).Value-60)<.01, "malformed, duplicate and invalid samples ignored");
            Check(!f.Read(4).HasValue, "stale FPS clears instead of displaying a frozen value");
            f.Accept(Header,5); Check(!f.Read(5).HasValue, "new capture header clears old samples");
            for(int i=1;i<=50;i++) f.Accept(Row(42,"x",i*.03,(i%2==0)?50:10),5);
            Check(Math.Abs(f.Read(5).Value-33.3333)<1, "rate uses total frame time rather than average of inverted times");
            f.Accept(Header,6);
            for(int chain=0;chain<12;chain++) for(int i=1;i<=5000;i++) f.Accept(Row(42,chain.ToString(),i/5000.0,.2),6);
            Check(f.BufferedFrames<=8*4096, "history memory bounded across extreme FPS and many swap chains");
            var presents = new FpsPresents(42);
            for(int i=0;i<=120;i++) { presents.Start(42,7,"a",i/120.0,false); presents.Stop(42,7,0,1); }
            Check(presents.Read(1).HasValue && Math.Abs(presents.Read(1).Value-120)<.01, "successful DirectX presents produce 120 FPS");
            for(int i=121;i<500;i++) {
                presents.Start(99,7,"a",i/120.0,false); presents.Stop(99,7,0,1);
                presents.Start(42,7,"a",i/120.0,true); presents.Stop(42,7,0,1);
                presents.Start(42,7,"a",i/120.0,false); presents.Stop(42,7,unchecked((int)0x887A000A),1);
                presents.Start(42,7,"a",i/120.0,false); presents.Stop(42,7,0x087a0001,1);
            }
            Check(Math.Abs(presents.Read(1).Value-120)<.01, "wrong PID, test, failed and occluded presents do not inflate FPS");
            Check(!presents.Read(4).HasValue, "native FPS becomes unavailable after frames stop");
            var rules = new FpsRules(); var app = new AppIdentity { ExecutablePath=@"D:\SteamLibrary\steamapps\common\Game\game.exe" };
            Check(rules.Tracks(app), "Steam game path recognized automatically");
            app.ExecutablePath=@"C:\Program Files\Epic Games\Game\Game.exe"; Check(rules.Tracks(app), "Epic game path recognized automatically");
            app.ExecutablePath=@"C:\Riot Games\VALORANT\live\VALORANT-Win64-Shipping.exe"; Check(rules.Tracks(app), "Riot game path recognized automatically");
            app.ExecutablePath=@"D:\Games\CONTROL Resonant\CONTROLResonant.exe"; Check(rules.Tracks(app), "CONTROL Resonant in a custom Games folder recognized automatically");
            app.ExecutablePath="E:/Library/GAMES/Example/Binaries/Win64/game.exe"; Check(rules.Tracks(app), "nested Games folder supports mixed case and forward slashes");
            app.ExecutablePath=@"D:\XboxGames\Example\Content\game.exe"; Check(rules.Tracks(app), "XboxGames installation recognized automatically");
            app.ExecutablePath=@"C:\Program Files (x86)\Steam\steamapps\common\FC 26\FC26.exe"; Check(rules.Tracks(app), "EA FC Steam installation recognized automatically");
            app.ExecutablePath=@"C:\Program Files\EA Games\EA SPORTS FC 26\FC26.exe"; Check(rules.Tracks(app), "EA app game installation recognized automatically");
            app.ExecutablePath=@"D:\Origin Games\FIFA 23\FIFA23.exe"; Check(rules.Tracks(app), "Origin game installation recognized automatically");
            app.ExecutablePath=@"C:\Program Files\EA Games\EA SPORTS FC 26\EAAntiCheat.GameServiceLauncher.exe"; Check(!rules.Tracks(app), "EA anti-cheat launcher excluded");
            app.ExecutablePath=@"D:\Games\EA\EADesktop.exe"; Check(!rules.Tracks(app), "EA app itself excluded even inside a game library");
            app.ExecutablePath=@"D:\GamesTools\utility.exe"; Check(!rules.Tracks(app), "partial folder names do not trigger capture");
            app.ExecutablePath=@"D:\Games\Example\launcher.exe"; Check(!rules.Tracks(app), "custom game launchers excluded from automatic capture");
            app.ExecutablePath=@"D:\Games\CONTROL Resonant\CONTROLResonant.exe"; rules.Set(app,false);
            var customRules=new FpsRules(); customRules.Restore(rules.Serialize());
            Check(!customRules.Tracks(app), "saved manual exclusion takes priority over Games folder detection");
            app.ExecutablePath=@"D:\SteamLibrary\steamapps\common\Game\launcher.exe"; Check(!rules.Tracks(app), "launchers excluded from automatic capture");
            app.ExecutablePath=@"C:\Browser\chrome.exe"; Check(!rules.Tracks(app), "ordinary browser does not start a helper");
            app.ExecutablePath=@"D:\日本語\Game=One\game.exe"; rules.Set(app,true);
            string saved=rules.Serialize(); var restored=new FpsRules(); restored.Restore(saved);
            Check(restored.Tracks(app), "manual game selection persists with Unicode and equals signs");
            restored.Set(app,false); Check(!restored.Tracks(app), "explicit exclusion overrides selection");
            app.ExecutablePath=@"D:\SteamLibrary\steamapps\common\Game\game.exe"; restored.Set(app,false);
            Check(!restored.Tracks(app), "automatic game detection can be disabled per application");
            restored.Restore("+not base64;;"); Check(!restored.Tracks(new AppIdentity()), "damaged preference does not crash or capture unknown process");
            using(var reader=new FpsReader(Path.Combine(args[0],"missing.exe")))
            {
                Check(reader.HelperId==0 && !reader.Read("none").HasValue, "idle monitoring starts no helper process");
                reader.SetTarget(new AppIdentity { ProcessId=42, Key="test" });
                System.Threading.Thread.Sleep(150);
                Check(reader.HelperId==0 && reader.Status.Contains("tidak ditemukan"), "missing component fails quietly with diagnostic status");
                reader.StopAsync().GetAwaiter().GetResult();
                Check(reader.HelperId==0, "shutdown leaves no helper");
            }
            File.WriteAllText(Path.Combine(args[0],"fps-checks.txt"),log.ToString()); return 0;
        }
        catch(Exception ex) { File.WriteAllText(Path.Combine(args[0],"fps-checks.txt"),log+"\n"+ex); return 1; }
    }
}
