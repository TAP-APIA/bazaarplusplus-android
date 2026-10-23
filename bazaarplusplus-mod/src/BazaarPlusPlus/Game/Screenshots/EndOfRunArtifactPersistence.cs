#nullable enable
using BazaarPlusPlus.Core.Runtime;
using BazaarPlusPlus.Storage.Paths;
using BazaarPlusPlus.Storage.RunScreenshot;

namespace BazaarPlusPlus.Game.Screenshots;

internal sealed class EndOfRunArtifactPersistence : IEndOfRunArtifactPersistence
{
    private readonly RunScreenshotSqliteStore? _store;

    internal EndOfRunArtifactPersistence(IBppServices services)
    {
        if (services == null)
            throw new ArgumentNullException(nameof(services));

        try
        {
            _store = new RunScreenshotSqliteStore(
                PathConstants.RunLogDatabase(services.Paths.RequireDataRoot())
            );
        }
        catch (Exception)
        {
            // On the Android port the SQLite native library cannot be dlopen'ed from shared
            // storage, so the store's static initializer throws. Screenshot persistence is an
            // optional artifact: degrade it instead of failing the whole composition.
            _store = null;
        }
    }

    public Task<ScreenshotMetadataPersistenceOutcome> PersistAsync(
        ScreenshotCaptureResult capture,
        bool isPrimary
    )
    {
        if (capture == null || _store == null)
            return Task.FromResult(ScreenshotMetadataPersistenceOutcome.Unavailable());

        var record = RunScreenshotRecordMapper.CreateRecord(capture, isPrimary);
        var store = _store;

        return Task.Run(() =>
        {
            try
            {
                store.Save(record);
                return ScreenshotMetadataPersistenceOutcome.Saved();
            }
            catch (Exception ex)
            {
                return ScreenshotMetadataPersistenceOutcome.Failed(ex);
            }
        });
    }
}
