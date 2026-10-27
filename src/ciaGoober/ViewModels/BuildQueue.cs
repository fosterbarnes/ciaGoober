using CommunityToolkit.Mvvm.ComponentModel;

namespace CiaGoober.ViewModels;

// Runs builds up to Settings.ParallelBuilds at once and reports into each game card.
// Every member runs on the UI thread; only ForwarderBuilder.BuildAsync runs on the thread pool.
public partial class BuildQueue : ObservableObject
{
    // Builder step lines are not shown; outcomes land in the card's status and Error.
    private static readonly IProgress<string> _NO_LOG = new Progress<string>();

    private readonly Queue<(GameViewModel Game, BuildJob Job)> _waiting = new();
    private CancellationTokenSource _cancel = new();
    private int _running;

    public BuildQueue(SettingsViewModel settings, ForwarderRegistry registry)
    {
        Settings = settings;
        Registry = registry;
    }

    public SettingsViewModel Settings { get; }
    public ForwarderRegistry Registry { get; }

    // Queued plus building.
    [ObservableProperty] private int _pending;

    // Snapshots the card now, so edits made while it waits do not change this build.
    public void Enqueue(GameViewModel game)
    {
        if (game.Status is GameStatus.Queued or GameStatus.Building) return;
        game.Error = null;
        BuildJob job;
        try
        {
            job = game.ToBuildJob();
        }
        catch (Exception ex)
        {
            Fail(game, ex.Message);
            return;
        }
        SetStatus(game, GameStatus.Queued, "Queued");
        _waiting.Enqueue((game, job));
        Pending++;
        StartWaiting();
    }

    // Drops queued jobs and cancels running ones; each returns to Idle as "Cancelled".
    public void CancelAll()
    {
        while (_waiting.TryDequeue(out var item))
        {
            SetCancelled(item.Game);
            Pending--;
        }
        _cancel.Cancel();
        _cancel = new CancellationTokenSource();
    }

    // Reads the limit per start so a settings change applies to the next job.
    private void StartWaiting()
    {
        while (_running < Math.Max(1, Settings.ParallelBuilds) && _waiting.TryDequeue(out var item))
            _ = RunAsync(item.Game, item.Job, _cancel.Token);
    }

    // Never faults: every outcome lands on the game card.
    private async Task RunAsync(GameViewModel game, BuildJob job, CancellationToken ct)
    {
        _running++;
        SetStatus(game, GameStatus.Building, "Building");
        try
        {
            var result = await Task.Run(
                () => ForwarderBuilder.BuildAsync(job, Registry, _NO_LOG, ct), ct);
            game.OutputPath = result.OutputPath;
            SetStatus(game, GameStatus.Built, "Built");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            SetCancelled(game);
        }
        catch (Exception ex)
        {
            Fail(game, ex.Message);
        }
        finally
        {
            _running--;
            Pending--;
            StartWaiting();
        }
    }

    private void Fail(GameViewModel game, string message)
    {
        game.Error = message;
        SetStatus(game, GameStatus.Failed, "Failed");
    }

    private static void SetCancelled(GameViewModel game) =>
        SetStatus(game, GameStatus.Idle, "Cancelled");

    private static void SetStatus(GameViewModel game, GameStatus status, string text)
    {
        game.Status = status;
        game.StatusText = text;
    }
}
