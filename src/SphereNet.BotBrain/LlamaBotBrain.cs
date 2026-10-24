using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using LLama;
using LLama.Common;
using LLama.Native;
using LLama.Sampling;
using LLama.Transformers;
using Microsoft.Extensions.Logging;
using SphereNet.Game.PlayerBots;

namespace SphereNet.BotBrain;

/// <summary>
/// The bots' brain: a small instruction-tuned language model (a GGUF file, e.g. Gemma 3
/// 1B, quantized to 4 bits) run by llama.cpp on the CPU, inside the server process. One
/// worker thread writes up to <see cref="BotBrainSettings.Parallel"/> replies at once,
/// each in a sequence of one shared context; the queue holds
/// <see cref="BotBrainSettings.Queue"/> more and refuses the rest, so the cost is
/// bounded however many players talk at once: a bot whose thought was refused, or not
/// answered in time, says one of its own lines. llama.cpp runs on
/// <see cref="BotBrainSettings.Threads"/> threads, leaving the rest of the cores to the
/// game.
/// </summary>
public sealed class LlamaBotBrain : IBotBrain, IDisposable
{
    private readonly BotBrainSettings _settings;
    private readonly ILogger _logger;
    private readonly LLamaWeights _weights;
    private readonly ModelParams _params;
    private readonly BlockingCollection<BotThought> _queue;
    private readonly Thread _worker;
    private readonly CancellationTokenSource _stop = new();

    public long Thoughts;
    public long Refused;
    public long Skipped;
    public long TotalMs;
    public long MaxMs;

    private LlamaBotBrain(BotBrainSettings settings, ILogger logger, LLamaWeights weights, ModelParams parameters)
    {
        _settings = settings;
        _logger = logger;
        _weights = weights;
        _params = parameters;
        _queue = new BlockingCollection<BotThought>(Math.Max(1, settings.Queue));
        _worker = new Thread(Run) { IsBackground = true, Name = "BotBrain", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    /// <summary>Load the model the settings name; null (logged) when there is none or
    /// it does not load. Loading a 1-2 GB model takes a few seconds.</summary>
    public static LlamaBotBrain? Load(BotBrainSettings settings, ILogger logger)
    {
        if (!File.Exists(settings.ModelPath))
        {
            logger.LogWarning("[botbrain] model file {Path} not found; bots answer from their own lines", settings.ModelPath);
            return null;
        }
        try
        {
            // llama.cpp reports every step of loading and of each context on stdout:
            // only its errors reach the server log.
            if (Interlocked.Exchange(ref _logHooked, 1) == 0)
                NativeLibraryConfig.All.WithLogCallback((level, message) =>
                {
                    if (level == LLamaLogLevel.Error) logger.LogWarning("[botbrain] llama.cpp: {Message}", message.TrimEnd());
                });
            var sw = Stopwatch.StartNew();
            var parameters = new ModelParams(settings.ModelPath)
            {
                // One memory for all the sequences written together, each up to Context.
                ContextSize = (uint)(settings.Context * Math.Max(1, settings.Parallel)),
                SeqMax = (uint)Math.Max(1, settings.Parallel),
                KVUnified = true,
                GpuLayerCount = 0,
                Threads = settings.Threads,
                BatchThreads = settings.Threads,
            };
            var weights = LLamaWeights.LoadFromFile(parameters);
            logger.LogInformation("[botbrain] {Model} loaded in {Ms} ms ({Threads} threads, {Parallel} replies at once, context {Context} each)",
                Path.GetFileName(settings.ModelPath), sw.ElapsedMilliseconds, settings.Threads, settings.Parallel, settings.Context);
            return new LlamaBotBrain(settings, logger, weights, parameters);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "[botbrain] could not load {Path}; bots answer from their own lines", settings.ModelPath);
            return null;
        }
    }

    private static int _logHooked;

    public bool TryThink(BotThought thought)
    {
        if (_queue.IsAddingCompleted || !_queue.TryAdd(thought))
        {
            Interlocked.Increment(ref Refused);
            return false;
        }
        return true;
    }

    private void Run()
    {
        var work = new List<BotThought>(Parallel);
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                // The first thought waited for, then whatever else is waiting, up to one
                // per sequence: they are written together.
                work.Clear();
                work.Add(_queue.Take(_stop.Token));
                while (work.Count < Parallel && _queue.TryTake(out var more)) work.Add(more);
                for (int i = work.Count - 1; i >= 0; i--)
                    if (work[i].Abandoned)
                    {
                        Interlocked.Increment(ref Skipped);
                        work[i].Finish(null);
                        work.RemoveAt(i);
                    }
                if (work.Count == 0) continue;
                try
                {
                    ThinkTogether(work).GetAwaiter().GetResult();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[botbrain] a batch of thoughts failed");
                    ForgetAll();
                    foreach (var t in work)
                        if (!t.Done) t.Finish(null);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (InvalidOperationException) { } // the queue was closed
    }

    private int Parallel => Math.Max(1, _settings.Parallel);

    /// <summary>One sequence of the shared context: the tokens its memory holds (a
    /// prompt that starts the way its last one did is only read from where it differs,
    /// and every prompt starts with the same rules), and the thought it is writing.</summary>
    private sealed class Slot(int id)
    {
        public readonly LLamaSeqId Seq = (LLamaSeqId)id;
        public readonly List<LLamaToken> Held = [];
        public BotThought? Thought;
        public LLamaToken[] Prompt = [];
        public DefaultSamplingPipeline? Sampler;
        public StreamingTokenDecoder? Decoder;
        public readonly StringBuilder Text = new();
        public int Pos;
        public int Logits;
        public long StartedTicks;
    }

    // The worker's context, made once (its buffers are a few hundred megabytes), with
    // a sequence per thought written at the same time.
    private LLamaContext? _context;
    private Slot[] _slots = [];
    private LLamaBatch? _batch;

    private string Prompt(BotThought thought)
    {
        var template = new LLamaTemplate(_weights) { AddAssistant = true };
        template.Add("system", thought.Persona);
        foreach (var (player, bot) in thought.History)
        {
            template.Add("user", player);
            template.Add("assistant", bot);
        }
        template.Add("user", thought.Line);
        return PromptTemplateTransformer.ToModelPrompt(template);
    }

    /// <summary>Write several replies at once: on the CPU a step costs about the same for
    /// one sequence as for a few (it is bound by reading the weights), so the replies
    /// come out together in little more than the time of one. Each reply is handed over
    /// the moment it ends.</summary>
    private async Task ThinkTogether(List<BotThought> work)
    {
        var ctx = _context ??= _weights.CreateContext(_params);
        if (_slots.Length != Parallel) _slots = Enumerable.Range(0, Parallel).Select(i => new Slot(i)).ToArray();
        var batch = _batch ??= new LLamaBatch();
        int limit = _settings.Context - _settings.Tokens - 1;

        // Each thought to a sequence, its prompt read up to its last token.
        var active = new List<Slot>(work.Count);
        for (int i = 0; i < work.Count; i++)
        {
            var slot = _slots[i];
            var thought = work[i];
            var tokens = ctx.Tokenize(Prompt(thought), addBos: true, special: true);
            if (tokens.Length > limit || tokens.Length < 2)
            {
                Finish(slot, thought, null);
                continue;
            }
            int keep = 0;
            while (keep < slot.Held.Count && keep < tokens.Length - 1 && slot.Held[keep] == tokens[keep]) keep++;
            ctx.NativeHandle.MemorySequenceRemove(slot.Seq, keep, -1);
            if (slot.Held.Count > keep) slot.Held.RemoveRange(keep, slot.Held.Count - keep);
            slot.Thought = thought;
            slot.Prompt = tokens;
            slot.Pos = keep;
            slot.Text.Clear();
            slot.StartedTicks = Stopwatch.GetTimestamp();
            slot.Sampler?.Dispose();
            slot.Sampler = new DefaultSamplingPipeline
            {
                Temperature = _settings.Temperature,
                TopP = 0.9f,
                RepeatPenalty = 1.1f,
            };
            slot.Decoder = new StreamingTokenDecoder(ctx);
            active.Add(slot);
        }
        if (active.Count == 0) return;

        // The prompts but their last tokens, in batches; then every last token in one
        // batch, so each sequence's logits are there to sample from.
        batch.Clear();
        foreach (var slot in active)
            while (slot.Pos < slot.Prompt.Length - 1)
            {
                if (batch.TokenCount >= BatchSize)
                {
                    await Decode(ctx, batch);
                    batch.Clear();
                }
                batch.Add(slot.Prompt[slot.Pos], slot.Pos, slot.Seq, false);
                slot.Pos++;
            }
        if (batch.TokenCount > 0) await Decode(ctx, batch);
        batch.Clear();
        foreach (var slot in active)
        {
            slot.Logits = batch.TokenCount;
            batch.Add(slot.Prompt[slot.Pos], slot.Pos, slot.Seq, true);
            slot.Pos++;
            slot.Held.Clear();
            slot.Held.AddRange(slot.Prompt);
        }
        await Decode(ctx, batch);

        // A token for every sequence still writing, each step one batch.
        for (int n = 0; n < _settings.Tokens && active.Count > 0; n++)
        {
            var next = new List<(Slot Slot, LLamaToken Token)>(active.Count);
            foreach (var slot in active)
            {
                var token = slot.Sampler!.Sample(ctx.NativeHandle, slot.Logits);
                slot.Sampler.Accept(token);
                bool end = token.IsEndOfGeneration(ctx.Vocab) || slot.Thought!.Abandoned;
                if (!end)
                {
                    slot.Decoder!.Add(token);
                    slot.Text.Append(slot.Decoder.Read());
                    end = slot.Text.ToString().Contains('\n') || n == _settings.Tokens - 1;
                }
                if (end) Finish(slot, slot.Thought!, slot.Thought!.Abandoned ? null : slot.Text.ToString());
                else next.Add((slot, token));
            }
            active = next.Select(x => x.Slot).ToList();
            if (active.Count == 0) break;
            batch.Clear();
            foreach (var (slot, token) in next)
            {
                slot.Logits = batch.TokenCount;
                batch.Add(token, slot.Pos, slot.Seq, true);
                slot.Pos++;
                slot.Held.Add(token);
            }
            await Decode(ctx, batch);
        }
        foreach (var slot in active) Finish(slot, slot.Thought!, slot.Text.ToString());
    }

    private async Task Decode(LLamaContext ctx, LLamaBatch batch)
    {
        if (await ctx.DecodeAsync(batch, _stop.Token) != DecodeResult.Ok)
            throw new InvalidDataException("llama_decode failed");
    }

    private void Finish(Slot slot, BotThought thought, string? reply)
    {
        long ms = slot.StartedTicks == 0 ? 0 : (Stopwatch.GetTimestamp() - slot.StartedTicks) * 1000 / Stopwatch.Frequency;
        thought.ThinkMs = ms;
        Interlocked.Increment(ref Thoughts);
        Interlocked.Add(ref TotalMs, ms);
        if (ms > Interlocked.Read(ref MaxMs)) Interlocked.Exchange(ref MaxMs, ms);
        slot.Thought = null;
        thought.Finish(reply);
    }

    private const int BatchSize = 512;

    /// <summary>Clear every sequence after a failed read: the next prompts are read whole.</summary>
    private void ForgetAll()
    {
        _context?.NativeHandle.MemoryClear(true);
        foreach (var slot in _slots) slot.Held.Clear();
    }

    public long Pending => _queue.Count;

    public void Dispose()
    {
        _queue.CompleteAdding();
        _stop.Cancel();
        _worker.Join(2000);
        foreach (var slot in _slots) slot.Sampler?.Dispose();
        _context?.Dispose();
        _weights.Dispose();
        _stop.Dispose();
    }
}
