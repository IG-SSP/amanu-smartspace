using static Amanu.Core.Localization.Localized;

namespace Amanu.App;

public enum UpdateOutcome { UpToDate, Ready, Busy, NotInstalled, Failed }
public sealed record UpdateResult(UpdateOutcome Outcome, string Message);

/// <summary>Fork releases are distributed manually until a dedicated signed feed exists.</summary>
public static class UpdateService
{
    public static Task<UpdateResult> CheckAsync(Func<bool> busy, bool interactive, CancellationToken token) =>
        Task.FromResult(new UpdateResult(UpdateOutcome.NotInstalled,
            T("SmartSpace fork: updates are installed manually.", "Форк SmartSpace: обновления устанавливаются вручную.")));
    public static bool RestartIntoUpdate(Func<bool> busy) => false;
}
