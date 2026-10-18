using Blackglass.MissionHost.Agent.Tests.Support;

namespace Blackglass.MissionHost.Agent.Tests;

/// <summary>Task 5: systemctl's fixed argument lists (A10-D4), strict parsing, exit status mapping and the process runner.</summary>
public class SystemdTests
{
    static readonly Guid Attempt = Guid.Parse("0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41");

    sealed class RecordingRunner(CommandResult result) : IProcessRunner
    {
        public readonly List<(string Exe, IReadOnlyList<string> Args)> Calls = [];
        public Task<CommandResult> RunAsync(string executable, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct)
        {
            Calls.Add((executable, arguments));
            return Task.FromResult(result);
        }
    }

    [Fact]
    public async Task Only_start_stop_list_and_show_exist_each_with_a_fixed_argument_list()
    {
        var runner = new RecordingRunner(new CommandResult(0, "", false));
        var systemctl = new Systemctl("/usr/bin/systemctl", runner);
        await systemctl.StartAsync(Attempt, default);
        await systemctl.StopAsync(Attempt, default);
        await systemctl.ListAsync(default);
        await systemctl.ShowAsync(Attempt, default);
        Assert.All(runner.Calls, c => Assert.Equal("/usr/bin/systemctl", c.Exe));
        Assert.Equal(["--no-ask-password", "start", "--", "blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service"], runner.Calls[0].Args);
        Assert.Equal(["--no-ask-password", "stop", "--", "blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service"], runner.Calls[1].Args);
        Assert.Equal(["--no-ask-password", "--no-pager", "--no-legend", "--plain", "--full", "--all", "list-units", "--", "blackglass-mission@*.service"], runner.Calls[2].Args);
        Assert.Equal(["--no-ask-password", "--no-pager", "show", "--property=LoadState,ActiveState,SubState,Result,ExecMainCode,ExecMainStatus,InvocationID", "--",
            "blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service"], runner.Calls[3].Args);
        Assert.Throws<ArgumentException>(() => AttemptIds.Unit(Guid.Empty));
        Assert.DoesNotContain(typeof(ISystemd).GetMethods(), m => m.Name.Contains("Enable", StringComparison.Ordinal) || m.Name.Contains("Mask", StringComparison.Ordinal)
            || m.Name.Contains("Restart", StringComparison.Ordinal) || m.Name.Contains("Reload", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_failed_or_timed_out_command_is_never_success_and_never_a_complete_inventory()
    {
        var failing = new Systemctl("/usr/bin/systemctl", new RecordingRunner(new CommandResult(1, "", false)));
        Assert.False(await failing.StartAsync(Attempt, default));
        Assert.False((await failing.ListAsync(default)).Complete);
        Assert.Null(await failing.ShowAsync(Attempt, default));
        var timedOut = new Systemctl("/usr/bin/systemctl", new RecordingRunner(new CommandResult(0, "", true)));
        Assert.False(await timedOut.StopAsync(Attempt, default));
        Assert.False((await timedOut.ListAsync(default)).Complete);
    }

    [Fact]
    public void A_listing_is_complete_only_when_every_line_is_a_mission_unit()
    {
        const string good = "blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service loaded active running Blackglass mission\n"
            + "● blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e42.service loaded failed failed Blackglass mission\n";
        var inventory = Systemctl.ParseList(good);
        Assert.True(inventory.Complete);
        Assert.Equal(["active", "failed"], inventory.Units.Select(u => u.ActiveState));
        Assert.True(Systemctl.ParseList("").Complete); // no units at all
        Assert.False(Systemctl.ParseList(good + "ssh.service loaded active running OpenSSH\n").Complete);
        Assert.False(Systemctl.ParseList(good + "garbage\n").Complete);
        Assert.False(Systemctl.ParseList("blackglass-mission@0192f1a4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service loaded weird running x\n").Complete);
        Assert.False(Systemctl.ParseList(good + good).Complete); // duplicates
        Assert.False(Systemctl.ParseList("blackglass-mission@0192F1A4-5c2e-7b1d-9a0e-3f6c1d2b8e41.service loaded active running x\n").Complete);
    }

    [Fact]
    public void Show_output_maps_to_an_exit_status()
    {
        var exited = Systemctl.ParseShow("LoadState=loaded\nActiveState=failed\nSubState=failed\nResult=exit-code\nExecMainCode=1\nExecMainStatus=3\nInvocationID=abc\n")!;
        Assert.True(exited.Terminal);
        Assert.Equal(new ExitStatus(3, null), exited.Exit);
        var killed = Systemctl.ParseShow("LoadState=loaded\nActiveState=failed\nSubState=failed\nResult=signal\nExecMainCode=2\nExecMainStatus=9\nInvocationID=abc\n")!;
        Assert.Equal(new ExitStatus(null, "SIGKILL"), killed.Exit);
        var forgotten = Systemctl.ParseShow("LoadState=loaded\nActiveState=inactive\nSubState=dead\nResult=success\nExecMainCode=0\nExecMainStatus=0\nInvocationID=\n")!;
        Assert.False(forgotten.Exit.Known); // garbage-collected: the status is unknown, never invented
        Assert.Null(Systemctl.ParseShow("nonsense"));
    }

    [Theory]
    [InlineData("code=exited\nstatus=0\n", 0, null)]
    [InlineData("code=exited\nstatus=137\n", 137, null)]
    [InlineData("code=killed\nstatus=TERM\n", null, "SIGTERM")]
    [InlineData("code=dumped\nstatus=SIGSEGV\n", null, "SIGSEGV")]
    [InlineData("code=killed\nstatus=9\n", null, "SIGKILL")]
    public void An_exit_record_parses_to_its_status(string text, int? code, string? signal) => Assert.Equal(new ExitStatus(code, signal), ExitRecords.Parse(text));

    [Theory]
    [InlineData("code=exited\nstatus=300\n")]
    [InlineData("code=exited\nstatus=-1\n")]
    [InlineData("code=killed\nstatus=NOPE\n")]
    [InlineData("status=0\n")]
    [InlineData("")]
    public void A_bad_exit_record_is_no_status(string text) => Assert.Null(ExitRecords.Parse(text));

    [Fact]
    public async Task The_process_runner_uses_no_shell_a_fixed_environment_and_a_timeout()
    {
        if (!OperatingSystem.IsLinux()) return;
        var runner = new ProcessRunner();
        var env = await runner.RunAsync("/usr/bin/env", [], TimeSpan.FromSeconds(10), default);
        Assert.Equal(0, env.ExitCode);
        Assert.Equal(["LANG", "LC_ALL", "SYSTEMD_COLORS", "SYSTEMD_PAGER", "SYSTEMD_URLIFY"],
            env.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Split('=')[0]).Order(StringComparer.Ordinal));
        var literal = await runner.RunAsync("/bin/echo", ["$(id)", ";", "ls"], TimeSpan.FromSeconds(10), default);
        Assert.Equal("$(id) ; ls\n", literal.Stdout); // arguments reach the program as they are: nothing interprets them
        var slow = await runner.RunAsync("/bin/sleep", ["5"], TimeSpan.FromMilliseconds(200), default);
        Assert.True(slow.TimedOut);
    }
}
