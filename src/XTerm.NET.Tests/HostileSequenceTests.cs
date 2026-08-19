using System.Diagnostics;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace XTerm.Tests;

/// <summary>
/// Escape sequences with absurd parameters must not hang or exhaust memory.
///
/// <para>
/// A terminal emulator parses whatever the program on the other end emits, which is not a trusted
/// input. <c>CSI 999999999 L</c> (insert a billion lines) is an ordinary sequence with a large
/// parameter, reachable by <c>cat</c>-ing a file that happens to contain those bytes — and before
/// the counts were clamped it ran the insert loop once per requested line, splicing the line list
/// a billion times.
/// </para>
/// <para>
/// That matters most to an embedder. A host feeding this parser from a PTY read loop — which is
/// the normal way to use it — would have that loop wedged with no way back, taking every session
/// sharing the thread or lock with it. So these run on a worker with a hard timeout: a regression
/// must FAIL rather than hang the test run and look like infrastructure trouble.
/// </para>
/// </summary>
public class HostileSequenceTests
{
    private readonly ITestOutputHelper _out;

    public HostileSequenceTests(ITestOutputHelper output) => _out = output;

    /// <summary>Sequences whose parameter drives a loop, with the largest value a parser will parse.</summary>
    public static TheoryData<string, string> UnboundedParameterSequences() => new()
    {
        { "IL — insert lines",      "\x1b[999999999L" },
        { "DL — delete lines",      "\x1b[999999999M" },
        { "SU — scroll up",         "\x1b[999999999S" },
        { "SD — scroll down",       "\x1b[999999999T" },
        { "ICH — insert chars",     "\x1b[999999999@" },
        { "DCH — delete chars",     "\x1b[999999999P" },
        { "ECH — erase chars",      "\x1b[999999999X" },
        { "CUD — cursor down",      "\x1b[999999999B" },
        { "CUF — cursor forward",   "\x1b[999999999C" },
        { "CUP — cursor position",  "\x1b[999999999;999999999H" },
        { "DECSTBM — scroll region","\x1b[1;999999999r" },
        { "REP — repeat",           "x\x1b[999999999b" },
        { "TBC/CHT — tabs",         "\x1b[999999999I" },
    };

    [Theory]
    [MemberData(nameof(UnboundedParameterSequences))]
    public void AbsurdParameter_CompletesPromptly(string name, string sequence)
    {
        var elapsed = RunWithTimeout(
            () =>
            {
                var terminal = new Terminal();
                terminal.Resize(80, 24);
                terminal.Write(sequence);
            },
            TimeSpan.FromSeconds(10),
            name);

        _out.WriteLine($"{name}: {elapsed} ms");
    }

    /// <summary>
    /// The same sequences applied in bulk, as a program spamming them would. Guards against a
    /// per-call cost that is individually acceptable but compounds.
    /// </summary>
    [Fact]
    public void RepeatedAbsurdParameters_StayBounded()
    {
        var elapsed = RunWithTimeout(
            () =>
            {
                var terminal = new Terminal();
                terminal.Resize(80, 24);
                var sb = new StringBuilder();
                for (var i = 0; i < 500; i++)
                    sb.Append("\x1b[999999999L\x1b[999999999M\x1b[999999999S");
                terminal.Write(sb.ToString());
            },
            TimeSpan.FromSeconds(20),
            "500x IL+DL+SU");

        _out.WriteLine($"500 rounds: {elapsed} ms");
    }

    /// <summary>
    /// Arbitrary bytes, which is what a binary file looks like to a terminal. Asserts only that
    /// the parser survives: what it renders for invalid input is not specified.
    /// </summary>
    [Fact]
    public void BinaryGarbage_DoesNotThrowOrHang()
    {
        var elapsed = RunWithTimeout(
            () =>
            {
                var terminal = new Terminal();
                terminal.Resize(80, 24);
                var feed = new TerminalByteFeed(terminal);

                var rng = new Random(20260819); // seeded so a failure reproduces
                var junk = new byte[256 * 1024];
                rng.NextBytes(junk);

                for (var i = 0; i < junk.Length; i += 4096)
                    feed.Write(junk.AsSpan(i, Math.Min(4096, junk.Length - i)));
                feed.Flush();
            },
            TimeSpan.FromSeconds(30),
            "256 KB of random bytes");

        _out.WriteLine($"binary garbage: {elapsed} ms");
    }

    /// <summary>
    /// Runs <paramref name="action"/> on a worker and fails (rather than hanging) if it overruns.
    /// </summary>
    private static long RunWithTimeout(Action action, TimeSpan timeout, string what)
    {
        Exception? error = null;
        long elapsed = 0;
        var done = new ManualResetEventSlim(false);

        var thread = new Thread(() =>
        {
            try
            {
                var sw = Stopwatch.StartNew();
                action();
                sw.Stop();
                elapsed = sw.ElapsedMilliseconds;
            }
            catch (Exception ex) { error = ex; }
            finally { done.Set(); }
        })
        { IsBackground = true };

        thread.Start();

        Assert.True(done.Wait(timeout),
            $"'{what}' did not finish within {timeout.TotalSeconds:F0}s — a parameter is driving " +
            "an unbounded loop. An embedder feeding this from a PTY read loop would wedge that " +
            "loop permanently.");

        Assert.Null(error);
        return elapsed;
    }
}
