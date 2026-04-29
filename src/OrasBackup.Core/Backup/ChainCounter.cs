using OrasBackup.Core.Delta;

namespace OrasBackup.Core.Backup;

public static class ChainCounter
{
    /// <summary>Simple count: 0 if no basedOn, 1 if has basedOn (no cache walk).</summary>
    public static int Count(DeltaManifest manifest) =>
        manifest.BasedOn is null ? 0 : 1;

    /// <summary>Walk the full chain using a resolver to look up parent manifests.</summary>
    public static int Count(DeltaManifest manifest, Func<string, DeltaManifest?> resolver)
    {
        var depth = 0;
        var current = manifest;
        while (current?.BasedOn is not null)
        {
            depth++;
            current = resolver(current.BasedOn);
        }
        return depth;
    }
}
