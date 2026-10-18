using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

// Fake-device tests only: this executable never constructs the NVML backend.
static class FanBoostChecks
{
    static readonly StringBuilder report = new StringBuilder();
    static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        report.AppendLine("PASS: " + label);
    }
    static int Main(string[] args)
    {
        string output = Path.GetFullPath(args.Length > 0 ? args[0] : ".");
        Directory.CreateDirectory(output);
        try
        {
            var fixture = new Fixture();
            int result = fixture.Run();
            Check(result == 0 && fixture.Store.Last.Status == "Complete", "normal burst completes");
            Check(fixture.Clock.Seconds >= 15 && fixture.Clock.Seconds < 15.01, "exact fifteen-second monotonic target interval");
            Check(fixture.Device.Fans[0].Policy == 1 && fixture.Device.Fans[0].Target == 44 && fixture.Device.Fans[1].Policy == 0,
                "different manual and automatic policies restored independently");
            Check(fixture.Device.SpeedWrites == 3 && fixture.Device.PolicyWrites == 1 && !GpuFanBoost.HasIntent(fixture.Store.Last),
                "one boost write per fan, one appropriate restoration, no leftover ownership");
            Check(fixture.Events.IndexOf("read 1") < fixture.Events.IndexOf("speed 0 100"), "every original fan is captured before first mutation");
            Check(fixture.Events.IndexOf("intent 0") < fixture.Events.IndexOf("speed 0 100") &&
                fixture.Events.IndexOf("intent 1") < fixture.Events.IndexOf("speed 1 100"), "journal intent precedes each native write");

            fixture = new Fixture();
            fixture.Device.Fans[0] = new FanBoostReading(1, 100);
            fixture.Device.Fans[1] = new FanBoostReading(1, 100);
            result = fixture.Run();
            Check(result == 0 && fixture.Device.PolicyWrites == 0 && fixture.Device.Fans[0].Target == 100 && fixture.Device.Fans[1].Target == 100,
                "existing FireStorm manual 100% remains manual 100%, never becomes Auto");

            fixture = new Fixture();
            fixture.Device.FailBoostFan = 1;
            result = fixture.Run();
            Check(result == 1 && fixture.Store.Last.Status == "Failed" && fixture.Device.Fans[0].Target == 44 && fixture.Device.Fans[1].Policy == 0,
                "second-fan setter failure rolls back first fan without disturbing untouched fan");

            fixture = new Fixture();
            fixture.Device.FailBoostFan = 1; fixture.Device.ChangeThenFail = true;
            result = fixture.Run();
            Check(result == 1 && fixture.Device.Fans[0].Target == 44 && fixture.Device.Fans[1].Policy == 0,
                "setter that changes hardware then reports error is also rolled back");

            fixture = new Fixture(); fixture.Device.FailInitialReadFan = 1;
            result = fixture.Run();
            Check(result == 1 && fixture.Device.SpeedWrites == 0 && fixture.Device.PolicyWrites == 0,
                "one unsupported snapshot read prevents all fan writes");

            fixture = new Fixture(); fixture.Device.Fans[1] = new FanBoostReading(9, 50);
            result = fixture.Run();
            Check(result == 1 && fixture.Device.SpeedWrites == 0, "unknown original fan policy fails before mutation");

            fixture = new Fixture(); fixture.Device.ExternalChangeAt = 2;
            result = fixture.Run();
            Check(result == 0 && fixture.Store.Last.Status == "Cancelled" && fixture.Clock.Seconds < 3 &&
                fixture.Device.Fans[0].Target == 55 && fixture.Device.Fans[1].Policy == 0,
                "external controller cancels burst, its new target is preserved, other fan restored");

            fixture = new Fixture(); fixture.Stop.StopAt = 2;
            result = fixture.Run();
            Check(result == 0 && fixture.Store.Last.Status == "Cancelled" && fixture.Clock.Seconds < 3 && fixture.Device.Fans[0].Target == 44 && fixture.Device.Fans[1].Policy == 0,
                "user cancellation restores every still-owned fan");

            fixture = new Fixture(); fixture.Stop.StopAt = 0;
            result = fixture.Run();
            Check(result == 0 && fixture.Store.Last.Status == "Cancelled" && fixture.Device.SpeedWrites == 0,
                "parent already gone/cancel already set prevents initial writes");

            fixture = new Fixture(); fixture.Device.FailRestore = true;
            result = fixture.Run();
            Check(result == 1 && fixture.Store.Last.Status == "Failed" && fixture.Store.Last.Fans[0].Intent && fixture.Device.Fans[1].Policy == 0,
                "failed manual restoration is explicit, retains recovery intent, and continues restoring other fans");
            fixture.Device.FailRestore = false;
            string problem = GpuFanBoost.Restore(fixture.Device, fixture.Store.Last, fixture.Store);
            Check(problem == null && fixture.Device.Fans[0].Target == 44 && !GpuFanBoost.HasIntent(fixture.Store.Last),
                "parent recovery can retry restoration after helper failure");

            fixture = new Fixture(); fixture.Device.IgnoreRestore = true;
            result = fixture.Run();
            Check(result == 1 && fixture.Store.Last.Fans[0].Intent, "successful setter return without restored readback is not reported as success");

            fixture = new Fixture(); fixture.Store.FailWriteNumber = 2;
            result = fixture.Run();
            Check(result == 1 && fixture.Device.SpeedWrites == 0, "journal failure before native write leaves hardware untouched");

            fixture = new Fixture();
            FanBoostState saved = GpuFanBoost.Snapshot(fixture.Device, fixture.Clock.UtcNow);
            saved.Fans[0].Intent = true; saved.Fans[1].Intent = true;
            fixture.Device.Fans[0] = new FanBoostReading(1, 100); fixture.Device.Fans[1] = new FanBoostReading(1, 100);
            fixture.Device.OtherUuid = true;
            problem = GpuFanBoost.Restore(fixture.Device, saved, fixture.Store);
            Check(problem != null && fixture.Device.SpeedWrites == 0 && fixture.Device.PolicyWrites == 0,
                "UUID mismatch forbids restoring a different GPU");

            fixture = new Fixture();
            saved = GpuFanBoost.Snapshot(fixture.Device, fixture.Clock.UtcNow);
            saved.Status = "Running"; saved.Fans[0].Intent = true; saved.Message = "uji & catatan <aman>";
            string statePath = Path.Combine(output, "fake-state.xml");
            var fileStore = new FanBoostFileStore(statePath);
            fileStore.Write(saved); fileStore.Write(saved);
            FanBoostState parsed = GpuFanBoost.ReadState(statePath);
            Check(parsed != null && parsed.Status == "Running" && parsed.Fans.Count == 2 && parsed.Fans[0].Intent &&
                parsed.Fans[0].Target == 44 && parsed.Message == saved.Message && parsed.Uuid == saved.Uuid,
                "atomic journal replacement round-trips exact snapshot, status, and escaped message");
            File.WriteAllText(statePath, "<!DOCTYPE x [<!ENTITY e SYSTEM 'file:///C:/Windows/win.ini'>]><fanBoost version='1'>&e;</fanBoost>");
            Check(GpuFanBoost.ReadState(statePath) == null, "malformed or external-entity journal rejected");
            File.WriteAllText(statePath, new string('x', 65537));
            Check(GpuFanBoost.ReadState(statePath) == null, "oversize journal rejected");
            saved.StartedUtc = DateTime.UtcNow.AddMinutes(-3); fileStore.Write(saved);
            Check(GpuFanBoost.Recover(statePath).Contains("kedaluwarsa"), "stale recovery record is rejected before opening NVML");
            File.Delete(statePath);
            report.AppendLine("All checks use fake hardware; no live GPU writes executed.");
            File.WriteAllText(Path.Combine(output, "fan-boost-checks.txt"), report.ToString());
            Console.Write(report.ToString());
            return 0;
        }
        catch (Exception ex)
        {
            report.AppendLine(ex.ToString());
            File.WriteAllText(Path.Combine(output, "fan-boost-checks.txt"), report.ToString());
            Console.Write(report.ToString());
            return 1;
        }
    }

    sealed class Fixture
    {
        internal readonly List<string> Events = new List<string>();
        internal readonly FakeClock Clock = new FakeClock();
        internal readonly FakeDevice Device;
        internal readonly FakeStore Store;
        internal readonly FakeStop Stop;
        internal Fixture() { Device = new FakeDevice(Clock, Events); Store = new FakeStore(Events); Stop = new FakeStop(Clock); }
        internal int Run() { return GpuFanBoost.Execute(Device, Clock, Stop, Store); }
    }
    sealed class FakeClock : IFanBoostClock
    {
        internal double Elapsed;
        public DateTime UtcNow { get { return new DateTime(2026, 10, 19, 0, 0, 0, DateTimeKind.Utc).AddSeconds(Elapsed); } }
        public double Seconds { get { return Elapsed; } }
        public void Sleep(int milliseconds) { Elapsed += milliseconds / 1000.0; }
    }
    sealed class FakeStop : IFanBoostStop
    {
        readonly FakeClock clock;
        internal double StopAt = Double.PositiveInfinity;
        internal FakeStop(FakeClock clock) { this.clock = clock; }
        public string Reason { get { return clock.Seconds >= StopAt ? "Boost dibatalkan oleh pengguna/proses induk." : null; } }
    }
    sealed class FakeStore : IFanBoostStore
    {
        readonly List<string> events;
        internal FanBoostState Last;
        internal int Writes, FailWriteNumber;
        internal FakeStore(List<string> events) { this.events = events; }
        public void Write(FanBoostState state)
        {
            Writes++;
            if (Writes == FailWriteNumber) throw new IOException("fake journal failure");
            Last = state;
            foreach (FanBoostSnapshot fan in state.Fans) if (fan.Intent) events.Add("intent " + fan.Index);
        }
    }
    sealed class FakeDevice : IFanBoostDevice
    {
        readonly FakeClock clock;
        readonly List<string> events;
        internal readonly FanBoostReading[] Fans = { new FanBoostReading(1, 44), new FanBoostReading(0, 35) };
        internal int SpeedWrites, PolicyWrites, FailBoostFan = -1, FailInitialReadFan = -1;
        internal bool ChangeThenFail, FailRestore, IgnoreRestore, OtherUuid;
        internal double ExternalChangeAt = Double.PositiveInfinity;
        bool externalChanged;
        internal FakeDevice(FakeClock clock, List<string> events) { this.clock = clock; this.events = events; }
        public string Name { get { return "NVIDIA Fake GPU"; } }
        public string Uuid { get { return OtherUuid ? "GPU-another-fake-device" : "GPU-fake-test-device"; } }
        public int FanCount { get { return Fans.Length; } }
        public uint Minimum { get { return 0; } }
        public uint Maximum { get { return 100; } }
        public FanBoostReading Read(int fan)
        {
            events.Add("read " + fan);
            if (fan == FailInitialReadFan && SpeedWrites == 0) throw new InvalidOperationException("fake unsupported read");
            if (!externalChanged && clock.Seconds >= ExternalChangeAt)
            {
                Fans[0] = new FanBoostReading(1, 55); externalChanged = true;
            }
            return Fans[fan];
        }
        public void SetSpeed(int fan, uint percent)
        {
            events.Add("speed " + fan + " " + percent); SpeedWrites++;
            if (percent == 100 && fan == FailBoostFan)
            {
                if (ChangeThenFail) Fans[fan] = new FanBoostReading(1, percent);
                throw new InvalidOperationException("fake setter failure");
            }
            if (percent == 44 && FailRestore) throw new InvalidOperationException("fake restore failure");
            if (percent == 44 && IgnoreRestore) return;
            Fans[fan] = new FanBoostReading(1, percent);
        }
        public void SetPolicy(int fan, uint policy)
        {
            events.Add("policy " + fan + " " + policy); PolicyWrites++;
            Fans[fan] = new FanBoostReading(policy, 37); // Driver target may legitimately change in Auto.
        }
        public void Dispose() { }
    }
}
