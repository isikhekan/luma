using Luma;

static class RtssSetupChecks
{
    public static async Task RunAsync(Action<bool, string> check)
    {
        static Task Immediate(TimeSpan _, CancellationToken token) { token.ThrowIfCancellationRequested(); return Task.CompletedTask; }
        var platform = new Fake(); var setup = new RtssSetup(platform, Immediate);
        await setup.EnsureAsync(false, () => true, default);
        check(setup.State == RtssSetupState.NeedsConsent && platform.Installs == 0, "Missing RTSS never installs before first-use approval");
        platform.Server = "installed"; platform.Bridge = true;
        await setup.EnsureAsync(false, () => true, default);
        check(setup.State == RtssSetupState.Ready && platform.Installs == 0 && platform.Starts == 0, "Existing connected server skips acquisition and restart");

        platform = new Fake { Server = "custom-install" }; setup = new RtssSetup(platform, Immediate);
        await setup.EnsureAsync(false, () => false, default);
        check(setup.State == RtssSetupState.Ready && platform.Starts == 1 && platform.Installs == 0, "Custom installation starts without package-manager or install consent");
        platform.Disabled = true;
        await setup.EnsureAsync(true, () => true, default);
        check(setup.State == RtssSetupState.SettingsDisabled && platform.Installs == 0, "Existing disabled OSD and user profiles are preserved");

        platform = new Fake(); setup = new RtssSetup(platform, Immediate);
        var states = new List<RtssSetupState>(); setup.Changed += () => states.Add(setup.State);
        int safeCalls = 0;
        await setup.EnsureAsync(true, () => ++safeCalls > 3, default);
        check(setup.State == RtssSetupState.Ready && platform.Installs == 1 && platform.Starts == 1, "Approved acquisition detects installed server and connects");
        check(states.SequenceEqual(new[] { RtssSetupState.Deferred, RtssSetupState.Installing, RtssSetupState.Connecting, RtssSetupState.Ready }), "Game/fullscreen deferral precedes installation and connection");

        platform = new Fake { Manager = null }; setup = new RtssSetup(platform, Immediate);
        await setup.EnsureAsync(true, () => true, default);
        check(setup.State == RtssSetupState.NeedsPackageManager && platform.Installs == 0, "Missing Windows package manager has explicit recovery state");
        platform = new Fake { Materialize = false }; setup = new RtssSetup(platform, Immediate);
        await setup.EnsureAsync(true, () => true, default);
        check(setup.State == RtssSetupState.Failed && platform.Starts == 0, "A zero installer exit code without an installed binary is not success");
        platform = new Fake { Result = unchecked((int)0x800704c7), Materialize = false }; setup = new RtssSetup(platform, Immediate);
        await setup.EnsureAsync(true, () => true, default);
        check(setup.State == RtssSetupState.Cancelled && setup.ExitCode == platform.Result, "UAC cancellation is distinct from connection failure");
        platform = new Fake { Result = -1, Materialize = false }; setup = new RtssSetup(platform, Immediate);
        await setup.EnsureAsync(true, () => true, default);
        check(setup.State == RtssSetupState.Failed && platform.Installs == 1, "Network/hash/package failure ends one attempt without retries");
        platform.Result = 0; platform.Materialize = true;
        await setup.EnsureAsync(true, () => true, default);
        check(setup.State == RtssSetupState.Ready && platform.Installs == 2, "Explicit retry recovers after acquisition failure");
        platform = new Fake { Server = "installed", StartWorks = false }; setup = new RtssSetup(platform, Immediate);
        await setup.EnsureAsync(false, () => true, default);
        check(setup.State == RtssSetupState.Failed && platform.Installs == 0, "Server start failure does not reinstall existing RTSS");
        platform = new Fake { Server = "installed", Connects = false }; setup = new RtssSetup(platform, Immediate);
        await setup.EnsureAsync(false, () => true, default);
        check(setup.State == RtssSetupState.Failed, "Missing bridge times out instead of claiming ready");

        platform = new Fake(); using var cancel = new CancellationTokenSource();
        var deferred = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        setup = new RtssSetup(platform, async (_, token) => { deferred.TrySetResult(); await Task.Delay(Timeout.Infinite, token); });
        Task first = setup.EnsureAsync(true, () => false, cancel.Token);
        await deferred.Task;
        Task second = setup.EnsureAsync(true, () => true, default);
        check(ReferenceEquals(first, second) && platform.Installs == 0, "Concurrent requests share one deferred operation");
        cancel.Cancel(); await first;
        check(setup.State == RtssSetupState.Cancelled && !setup.Busy && platform.Installs == 0, "Shutdown or disabled notifications cancel deferred acquisition");

        using var installCancel = new CancellationTokenSource();
        var installing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        platform = new Fake { Install = async token => { installing.SetResult(); await Task.Delay(Timeout.Infinite, token); return 0; } };
        setup = new RtssSetup(platform, Immediate);
        first = setup.EnsureAsync(true, () => true, installCancel.Token); await installing.Task;
        check(setup.Busy && setup.State == RtssSetupState.Installing && ReferenceEquals(first, setup.EnsureAsync(true, () => true, default)), "An active installer cannot be launched twice");
        installCancel.Cancel(); await first;
        check(setup.State == RtssSetupState.Cancelled && platform.Starts == 0, "Cancelling acquisition does not start an unverified server");

        platform = new Fake(); setup = new RtssSetup(platform, (_, token) => { platform.Server = "installed-elsewhere"; return Immediate(default, token); });
        safeCalls = 0; await setup.EnsureAsync(true, () => ++safeCalls > 2, default);
        check(setup.State == RtssSetupState.Ready && platform.Installs == 0, "External installation during deferral is reused");

        var args = WindowsRtssSetup.InstallArguments;
        check(args.Contains("--exact") && args.Contains("--no-upgrade") && args.Contains("--source") && args.Contains("winget") && args.Contains("Guru3D.RTSS"), "Acquisition restricts package and source and preserves existing installation");
        check(!args.Any(a => a is "--force" or "--ignore-security-hash" or "--ignore-local-archive-malware-scan" or "--allow-reboot"), "Acquisition retains package manager verification and does not reboot");
        check(args.Contains("--version") && args.Contains(WindowsRtssSetup.PackageVersion), "Dependency version is pinned for reviewed manifests");
        check(!new AppSettings().RtssInstallApproved, "An upgrade does not silently grant third-party installation approval");
        check(System.Text.Json.JsonSerializer.Deserialize<AppSettings>("{\"RtssInstallApproved\":true}")!.RtssInstallApproved, "First-use approval persists for later automatic setup");
        check(RtssInstallation.ExecutableFromIcon("\"D:\\Apps\\RTSS\\RTSS.exe\",0") == @"D:\Apps\RTSS\RTSS.exe", "Quoted custom install icons parsed");
        check(RtssInstallation.ExecutableFromIcon("D:\\Apps, Tools\\RTSS.exe,0") == @"D:\Apps, Tools\RTSS.exe", "Commas in custom install paths preserved");
        check(RtssInstallation.ExecutableFromIcon("RTSS.exe") == null && RtssInstallation.ExecutableFromIcon("\"D:\\bad.exe") == null
            && RtssInstallation.ExecutableFromIcon("https://invalid/RTSS.exe") == null, "Relative, unterminated and remote executable icons rejected");
        check(!RtssInstallation.IsServer(Environment.ProcessPath!) && !RtssInstallation.IsServer(@"C:\Missing\RTSS.exe"), "Installation discovery rejects unrelated or absent binaries");
        const string ini = "; EnableOSD=1\n[Other]\nEnableOSD=1\n[osd]\n EnableOSD \t= 0 ; disabled\nOther=2\n";
        check(RtssInstallation.ReadIniValue(ini, "OSD", "EnableOSD") == "0" && RtssInstallation.ReadIniValue(ini, "Hooking", "EnableHooking") == null, "Configuration inspection respects sections, comments and missing keys");
        check(RtssInstallation.ReadIniValue("[OSD]\nEnableOSD=1\nEnableOSD=0", "OSD", "EnableOSD") == "0", "Last duplicate INI property takes precedence");
        Console.WriteLine("PASS: RTSS acquisition approval, existing installs, game deferral, cancellation, failure recovery, serialization and trusted command boundaries.");
    }
    private sealed class Fake : IRtssSetupPlatform
    {
        public string? Server, Manager = "winget";
        public bool Bridge, Disabled, Materialize = true, StartWorks = true, Connects = true;
        public int Result, Installs, Starts;
        public Func<CancellationToken, Task<int>>? Install;
        public string? FindServer() => Server;
        public bool BridgeAvailable() => Bridge;
        public string? FindPackageManager() => Manager;
        public Task<int> InstallAsync(string manager, CancellationToken token)
        { token.ThrowIfCancellationRequested(); Installs++; if (Install != null) return Install(token); if (Materialize) Server = "new-install"; return Task.FromResult(Result); }
        public bool StartServer() { Starts++; if (StartWorks && Connects) Bridge = true; return StartWorks; }
        public bool NotificationsDisabled(string server) => Disabled;
    }
}
