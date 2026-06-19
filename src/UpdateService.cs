using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace Capper;

/// <summary>
/// Thin wrapper over Velopack's <see cref="UpdateManager"/> that checks the GitHub Releases of the
/// Capper repo for a newer installed build. It is a no-op when the app isn't running as an installed
/// Velopack release (e.g. a dev F5 run or the loose exe), so callers can invoke it unconditionally.
/// </summary>
internal sealed class UpdateService
{
    private const string RepoUrl = "https://github.com/openface/Capper";

    private readonly UpdateManager _mgr = new(new GithubSource(RepoUrl, null, prerelease: false));

    /// <summary>True only when launched from a Velopack install (so updating is actually possible).</summary>
    public bool IsInstalled => _mgr.IsInstalled;

    /// <summary>Returns the available update, or null if up to date / not installable. Never throws.</summary>
    public async Task<UpdateInfo?> CheckAsync()
    {
        if (!_mgr.IsInstalled) return null;
        try { return await _mgr.CheckForUpdatesAsync(); }
        catch { return null; } // offline, rate-limited, etc. — silently stay on the current build
    }

    /// <summary>Download the update and relaunch into it. Does not return on success.</summary>
    public async Task DownloadAndApplyAsync(UpdateInfo info)
    {
        await _mgr.DownloadUpdatesAsync(info);
        _mgr.ApplyUpdatesAndRestart(info);
    }
}
