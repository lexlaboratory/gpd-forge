// GPD Forge - controller re-enumeration tests. GPL-3.0-or-later.
//
// The failure this guards against is not "the restart did not work" — it is restarting a controller
// that was fine. A pad yanked out from under a running game is a worse bug than the one this step
// exists to fix, so most of these tests are about NOT acting.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GpdForge.Hid;
using GpdForge.Tdp;
using Xunit;

namespace GpdForge.Core.Tests;

public class HidReenumeratorTests
{
    /// <summary>Serves a scripted set of nodes, then a second set once a restart has happened.</summary>
    private sealed class FakeEnumerator(
        IReadOnlyList<HidDeviceNode> before, IReadOnlyList<HidDeviceNode>? after = null)
        : IHidDeviceEnumerator
    {
        public int Calls { get; private set; }
        public IReadOnlyList<HidDeviceNode> Find(string idFragment)
        {
            Calls++;
            return Calls == 1 || after is null ? before : after;
        }
    }

    private sealed class SpyRunner(bool throws = false) : IProcessRunner
    {
        public List<string> Commands { get; } = new();

        public Task<string> RunAsync(string exePath, string arguments, CancellationToken ct)
        {
            Commands.Add($"{exePath} {arguments}");
            if (throws) throw new InvalidOperationException("pnputil is not available");
            return Task.FromResult(string.Empty);
        }
    }

    private const string Parent = @"USB\VID_2F24&PID_0135\7&160A07D9&0&2";
    private const string Iface0 = @"USB\VID_2F24&PID_0135&MI_00\8&15955740&0&0000";
    private const string Iface1 = @"HID\VID_2F24&PID_0135&MI_01\9&1ED39058&0&0000";

    private static HidDeviceNode Node(string id, int code = 0) => new(id, code);

    [Fact]
    public async Task A_controller_that_came_back_on_its_own_is_left_alone()
    {
        var runner = new SpyRunner();
        var hid = new HidReenumerator(
            new FakeEnumerator([Node(Parent), Node(Iface0), Node(Iface1)]), runner);

        var result = await hid.RestoreAsync(CancellationToken.None);

        Assert.False(result.Acted);
        Assert.True(result.Healthy);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task One_restart_of_the_composite_parent_covers_every_interface()
    {
        // The pad presents as seven nodes on the real device. Restarting them one by one would be
        // seven device restarts where the parent's re-enumeration already does the job.
        var runner = new SpyRunner();
        var hid = new HidReenumerator(
            new FakeEnumerator(
                [Node(Parent, 43), Node(Iface0, 43), Node(Iface1, 43)],
                [Node(Parent), Node(Iface0), Node(Iface1)]),
            runner);

        var result = await hid.RestoreAsync(CancellationToken.None);

        Assert.True(result.Acted);
        Assert.True(result.Healthy);
        var command = Assert.Single(runner.Commands);
        Assert.Contains("pnputil", command);
        Assert.Contains(Parent, command);
        Assert.DoesNotContain("MI_", command);
    }

    [Fact]
    public async Task A_fault_confined_to_one_interface_restarts_that_interface_only()
    {
        var runner = new SpyRunner();
        var hid = new HidReenumerator(
            new FakeEnumerator(
                [Node(Parent), Node(Iface0), Node(Iface1, 43)],
                [Node(Parent), Node(Iface0), Node(Iface1)]),
            runner);

        var result = await hid.RestoreAsync(CancellationToken.None);

        Assert.True(result.Healthy);
        Assert.Contains(Iface1, Assert.Single(runner.Commands));
    }

    [Fact]
    public async Task A_restart_that_did_not_help_is_reported_as_not_healthy()
    {
        // pnputil exits cleanly for a restart that leaves the node exactly as faulted as it was, so
        // the result is verified against the device rather than inferred from the exit code.
        var runner = new SpyRunner();
        var hid = new HidReenumerator(
            new FakeEnumerator([Node(Parent, 43)], [Node(Parent, 43)]), runner);

        var result = await hid.RestoreAsync(CancellationToken.None);

        Assert.True(result.Acted);
        Assert.False(result.Healthy);
        Assert.Contains("43", result.Detail);
    }

    [Fact]
    public async Task A_controller_Windows_cannot_see_at_all_is_not_pretended_to_be_fixed()
    {
        var runner = new SpyRunner();
        var hid = new HidReenumerator(new FakeEnumerator([]), runner);

        var result = await hid.RestoreAsync(CancellationToken.None);

        Assert.False(result.Acted);
        Assert.False(result.Healthy);
        Assert.Empty(runner.Commands);
    }

    [Fact]
    public async Task A_failing_pnputil_reports_the_reason_instead_of_throwing_into_the_resume()
    {
        var hid = new HidReenumerator(new FakeEnumerator([Node(Parent, 43)]), new SpyRunner(throws: true));

        var result = await hid.RestoreAsync(CancellationToken.None);

        Assert.False(result.Healthy);
        Assert.Contains("not available", result.Detail);
    }

    [Fact]
    public void The_composite_parent_is_recognised_structurally_not_by_name()
    {
        // Device names are localised — on the reference machine this pad is called "Dispositivo
        // definido por el proveedor compatible con HID". The instance ID is not.
        Assert.True(Node(Parent).IsCompositeParent);
        Assert.False(Node(Iface0).IsCompositeParent);
        Assert.False(Node(Iface1).IsCompositeParent);
    }
}
