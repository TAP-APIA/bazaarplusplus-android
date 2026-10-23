#nullable enable
using BazaarPlusPlus.Infrastructure;
using BazaarPlusPlus.Infrastructure.Logging;
using UnityEngine;

namespace BazaarPlusPlus.Core.Runtime;

internal sealed class BppMountableRegistry
{
    private readonly List<IBppMountable> _mountables = new();

    public void Register(IBppMountable mountable) => _mountables.Add(mountable);

    public void MountAll(GameObject host, IBppServices services)
    {
        for (var index = 0; index < _mountables.Count; index++)
        {
            // Traced by index, not by type name: reflecting over these generic mount types makes the
            // interop layer resolve types it cannot represent, which aborts before the mount runs.
            BppStartupTrace.Write("mount:" + index);

            _mountables[index].Mount(host, services);
        }

        BppStartupTrace.Write("mount:done");
    }

    public void UnmountAll(GameObject host)
    {
        for (var i = _mountables.Count - 1; i >= 0; i--)
            _mountables[i].Unmount(host);
    }
}
