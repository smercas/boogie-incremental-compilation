using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Microsoft.Boogie;

public interface ExecutionEngineOptions : HoudiniOptions, ConcurrencyOptions {

  public abstract class BaseProfiler {
    public abstract Task WriteResultsTo(TextWriter writer);
    public abstract IDisposable NewSection(string text);
    public abstract IDisposable NewSectionAndWriteResultsAfterwards(string text, TextWriter writer);
  }
  public class NullProfiler : BaseProfiler {
    public override async Task WriteResultsTo(TextWriter writer) { }
    public override IDisposable NewSection(string text) => new Util.EmptyDisposable();
    public override IDisposable NewSectionAndWriteResultsAfterwards(string text, TextWriter writer) => new Util.EmptyDisposable();
  }
  public class ActualProfiler : BaseProfiler {
    public sealed record Flattened(IReadOnlyList<double> Percentages, string Text, TimeSpan Duration) {
      public Flattened(IReadOnlyList<double> parentPercentages, string text, TimeSpan parentDuration, TimeSpan duration)
        : this(percentagesFrom(parentPercentages, parentDuration, duration), text, duration) { }
      private static IReadOnlyList<double> percentagesFrom(IReadOnlyList<double> parentPercentages, TimeSpan parentDuration, TimeSpan duration) {
        var result = new List<double>(parentPercentages.Count + 1) { duration / parentDuration };
        foreach (var percentage in parentPercentages.Reversed()) {
          result.Add(percentage * result[0]);
        }
        return result.Reversed();
      }
    }
    public abstract class Result(string text) {
      public string Text { get; } = text;
      public abstract TimeSpan Duration { get; }

      public abstract IReadOnlyList<Flattened> AsFlattened(IReadOnlyList<double> parentPercentages, TimeSpan parentDuration);
    }
    private sealed class One(string text, TimeSpan duration) : Result(text) {
      public override TimeSpan Duration { get; } = duration;

      public override IReadOnlyList<Flattened> AsFlattened(IReadOnlyList<double> parentPercentages, TimeSpan parentDuration) =>
        [new(parentPercentages, Text, parentDuration, Duration)];
    }
    private sealed class Multiple(string text, TimeSpan duration, List<Result> durations) : Result(text) {
      public List<Result> durations { get; } = durations;
      public IReadOnlyList<Result> Durations => durations;
      public override TimeSpan Duration { get; } = duration;

      public override IReadOnlyList<Flattened> AsFlattened(IReadOnlyList<double> parentPercentages, TimeSpan parentDuration) {
        var root = new Flattened(parentPercentages, Text, parentDuration, Duration);
        var unaccountedForDuration = Duration - new TimeSpan(Durations.Sum(r => r.Duration.Ticks));
        List<Flattened> unaccountedFor = unaccountedForDuration / Duration < 0.01 ? [] : [new Flattened(root.Percentages, "unaccounted for", root.Duration, unaccountedForDuration)]; // IPMTODO: maybe add customizable threshold
        return [root, .. Durations.SelectMany(p => p.AsFlattened(root.Percentages, root.Duration)), .. unaccountedFor];
      }
    }

    public override async Task WriteResultsTo(TextWriter writer) {
      if (!(visiting.Count == 1 && visiting.Peek().Pre.Count == 0)) { throw new InvalidOperationException("can't do this when still in a section"); }
      IReadOnlyList<Result> durations = visiting.Peek().Post;
      if (durations is [One o]) {
        await writer.WriteLineAsync($"{o.Text}: {o.Duration}");
        return;
      }
      if (durations is [Multiple m]) {
        await writer.WriteLineAsync($"{m.Text}: {m.Duration}");
        durations = m.Durations;
      }
      var totalDuration = new TimeSpan(durations.Sum(r => r.Duration.Ticks));
      foreach (var flattened in durations.SelectMany(p => p.AsFlattened([], totalDuration))) {
        await writer.WriteLineAsync($"{string.Join(null, flattened.Percentages.Select(p => $"{p,8:P2}".Replace(" %", "% ")))}{flattened.Text}: {flattened.Duration}");
      }
      visiting.Peek().Post.Clear();
    }

    private record PreResult(string Text, Stack<PreResult> Pre, List<Result> Post);
    private Stack<PreResult> visiting { get; } = new([new("Root", [], [])]);

    private IDisposable BaseNewSection(string text, System.Action post = null) {
      var section = new PreResult(text, [], []);
      var start = DateTime.UtcNow;
      visiting.Peek().Pre.Push(section);
      visiting.Push(section);
      return new Util.DisposableAction(() => {
        var duration = DateTime.UtcNow - start;
        List<PreResult> section_repeated = [visiting.Pop(), visiting.Peek().Pre.Pop()];
        Contract.Assert(section_repeated.All(sr => ReferenceEquals(sr, section)));
        Contract.Assert(section.Pre.Count == 0);
        visiting.Peek().Post.Add(section.Post switch {
          [] => new One(section.Text, duration),
          [..] => new Multiple(section.Text, duration, [.. section.Post]),
        });
        if (post is not null) { post(); }
      });
    }
    public override IDisposable NewSection(string text) => BaseNewSection(text);
    public override IDisposable NewSectionAndWriteResultsAfterwards(string text, TextWriter writer) => BaseNewSection(text, async () => { await using var w = writer; await WriteResultsTo(w); });
  }

  public BaseProfiler Profiler { get; set; }
  public OutputPrinter Printer { get; }
  bool ShowVerifiedProcedureCount { get; }
  string DescriptiveToolName { get; }
  bool TraceProofObligations { get; }
  string PrintFile { get; }
  List<Action<ExecutionEngineOptions, ProcessedProgram>> UseResolvedProgram { get; }

  string PrintCFGPrefix { get; }
  string CivlDesugaredFile { get; }
  bool CoalesceBlocks { get; }
  ShowEnvironment ShowEnv { get; }
  string Version { get; }
  string Environment { get; }
  HashSet<string> Libraries { get; set; }
  bool NoResolve { get; }
  bool NoTypecheck { get; }

  List<string> ProcsToCheck { get; }
  List<string> ProcsToIgnore { get; }
  int PrintErrorModel { get; }
  int EnhancedErrorMessages { get; }
  bool ForceBplErrors { get; }
  bool PrintAssignment { get; }
  bool ExtractLoops { get; }
  TextWriter ModelWriter { get; }
  bool ExpandLambdas { get; }
  bool PrintLambdaLifting { get; }
  bool UseAbstractInterpretation { get; }
  bool SoundLoopUnrolling { get; }
  bool Verify { get; }
  bool ContractInfer { get; }

  public enum ShowEnvironment
  {
    Never,
    DuringPrint,
    Always
  }

  public bool UserWantsToCheckRoutine(string methodFullname)
  {
    Contract.Requires(methodFullname != null);
    Func<string, bool> match = s => Regex.IsMatch(methodFullname, "^" + Regex.Escape(s).Replace(@"\*", ".*") + "$");
    return (ProcsToCheck.Count == 0 || ProcsToCheck.Any(match)) && !ProcsToIgnore.Any(match);
  }
}